using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Speech;

/// <summary>
/// Google Cloud Text-to-Speech (REST v1). Authenticates with an API key, or with a service-account JSON key file
/// (an RS256-signed JWT exchanged for an access token). Rate and pitch go through audioConfig.
/// </summary>
internal sealed class GoogleEngine(HttpClient http, Func<GoogleSettings> settings, Func<DateTimeOffset>? clock = null) : ITtsEngine
{
    internal const string BaseUrl = "https://texttospeech.googleapis.com/v1/";
    internal const string Scope = "https://www.googleapis.com/auth/cloud-platform";
    private const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private (string File, string Token, DateTimeOffset Expires)? _token;

    public string Id => "google";
    public string DisplayName => "Google Cloud";
    public bool IsAvailable => UnavailableReason is null;

    public string? UnavailableReason
    {
        get
        {
            var s = settings();
            if (!string.IsNullOrWhiteSpace(s.ApiKey)) return null;
            if (string.IsNullOrWhiteSpace(s.ServiceAccountFile)) return "Enter a Google Cloud API key or choose a service-account key file.";
            return File.Exists(s.ServiceAccountFile.Trim()) ? null : "The Google service-account key file was not found.";
        }
    }

    private DateTimeOffset Now => clock?.Invoke() ?? DateTimeOffset.UtcNow;

    public async Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct)
    {
        using var request = await CreateRequestAsync(HttpMethod.Get, "voices", ct).ConfigureAwait(false);
        using var json = await CloudHttp.GetJsonAsync(http, request, DisplayName, ct).ConfigureAwait(false);
        return ParseVoices(json.RootElement);
    }

    public async Task<SynthesizedAudio> SynthesizeAsync(SynthesisRequest synthesis, CancellationToken ct)
    {
        using var request = await CreateRequestAsync(HttpMethod.Post, "text:synthesize", ct).ConfigureAwait(false);
        request.Content = JsonContent.Create(BuildBody(synthesis));
        using var json = await CloudHttp.GetJsonAsync(http, request, DisplayName, ct).ConfigureAwait(false);
        var content = json.RootElement.GetText("audioContent")
            ?? throw new InvalidOperationException("Google Cloud returned no audio.");
        return AudioSniffer.Wrap(Convert.FromBase64String(content));
    }

    internal static JsonObject BuildBody(SynthesisRequest synthesis)
    {
        var voice = synthesis.VoiceId.Trim();
        if (voice.Length == 0) throw new ArgumentException("A Google Cloud voice must be selected.", nameof(synthesis));
        var audioConfig = new JsonObject { ["audioEncoding"] = "LINEAR16" };
        if (synthesis.Rate != 0) audioConfig["speakingRate"] = Prosody.RateMultiplier(synthesis.Rate);
        // Chirp and Journey voices reject pitch.
        if (synthesis.Pitch != 0 && !voice.Contains("Chirp", StringComparison.OrdinalIgnoreCase)
            && !voice.Contains("Journey", StringComparison.OrdinalIgnoreCase))
            audioConfig["pitch"] = Math.Clamp(synthesis.Pitch, -10, 10) * 2.0; // semitones, ±20 max
        return new JsonObject
        {
            ["input"] = new JsonObject { ["text"] = synthesis.Text },
            ["voice"] = new JsonObject { ["languageCode"] = LanguageOf(voice), ["name"] = voice },
            ["audioConfig"] = audioConfig,
        };
    }

    /// <summary>"en-US-Wavenet-D" → "en-US".</summary>
    internal static string LanguageOf(string voice)
    {
        var parts = voice.Split('-');
        return parts.Length >= 3 ? $"{parts[0]}-{parts[1]}" : "en-US";
    }

    /// <summary>"en-US-Wavenet-D" → "WaveNet"; "en-US-Chirp3-HD-Aoede" → "Chirp3".</summary>
    internal static string? TierOf(string voice)
    {
        var parts = voice.Split('-');
        if (parts.Length < 4) return null;
        return parts[2].Equals("Wavenet", StringComparison.OrdinalIgnoreCase) ? "WaveNet" : parts[2];
    }

    internal static IReadOnlyList<VoiceInfo> ParseVoices(JsonElement root) =>
        root.GetArray("voices")
            .Select(v => (Voice: v, Name: v.GetText("name")))
            .Where(v => !string.IsNullOrEmpty(v.Name))
            .Select(v => new VoiceInfo(v.Name!, v.Name!,
                v.Voice.GetArray("languageCodes").Select(l => l.GetString()).FirstOrDefault(l => !string.IsNullOrEmpty(l))
                ?? LanguageOf(v.Name!))
            {
                Gender = v.Voice.GetText("ssmlGender") switch
                {
                    "FEMALE" => "Female",
                    "MALE" => "Male",
                    _ => null,
                },
                Tier = TierOf(v.Name!),
            })
            .ToArray();

    private async Task<HttpRequestMessage> CreateRequestAsync(HttpMethod method, string path, CancellationToken ct)
    {
        if (UnavailableReason is { } reason) throw new InvalidOperationException(reason);
        var s = settings();
        var request = new HttpRequestMessage(method, new Uri(BaseUrl + path)); // "text:synthesize" must not parse as a scheme
        if (!string.IsNullOrWhiteSpace(s.ApiKey))
        {
            request.Headers.Add("X-Goog-Api-Key", s.ApiKey.Trim());
        }
        else
        {
            var token = await GetAccessTokenAsync(s.ServiceAccountFile.Trim(), ct).ConfigureAwait(false);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }
        return request;
    }

    private async Task<string> GetAccessTokenAsync(string file, CancellationToken ct)
    {
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_token is { } cached && cached.File == file && cached.Expires > Now.AddMinutes(1)) return cached.Token;
            var key = ServiceAccountKey.Parse(await File.ReadAllTextAsync(file, ct).ConfigureAwait(false));
            var assertion = CreateJwt(key, Now);
            using var request = new HttpRequestMessage(HttpMethod.Post, key.TokenUri ?? DefaultTokenUri)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                    ["assertion"] = assertion,
                }),
            };
            using var json = await CloudHttp.GetJsonAsync(http, request, DisplayName, ct).ConfigureAwait(false);
            var token = json.RootElement.GetText("access_token")
                ?? throw new InvalidOperationException("Google did not return an access token.");
            var lifetime = json.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 3600;
            _token = (file, token, Now.AddSeconds(lifetime));
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    /// <summary>A signed JWT asserting the service account for the cloud-platform scope.</summary>
    internal static string CreateJwt(ServiceAccountKey key, DateTimeOffset now)
    {
        var header = Base64Url("""{"alg":"RS256","typ":"JWT"}"""u8.ToArray());
        var issued = now.ToUnixTimeSeconds();
        var claims = new JsonObject
        {
            ["iss"] = key.ClientEmail,
            ["scope"] = Scope,
            ["aud"] = key.TokenUri ?? DefaultTokenUri,
            ["iat"] = issued,
            ["exp"] = issued + 3600,
        };
        var payload = Base64Url(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        using var rsa = RSA.Create();
        rsa.ImportFromPem(key.PrivateKey);
        var signature = rsa.SignData(Encoding.ASCII.GetBytes($"{header}.{payload}"), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{header}.{payload}.{Base64Url(signature)}";
    }

    internal static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>The fields Cuebot needs from a Google service-account JSON key.</summary>
internal sealed record ServiceAccountKey(string ClientEmail, string PrivateKey, string? TokenUri)
{
    public static ServiceAccountKey Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var email = root.GetText("client_email");
        var key = root.GetText("private_key");
        if (string.IsNullOrEmpty(email) || string.IsNullOrEmpty(key))
            throw new InvalidOperationException("The Google key file is not a service-account key (client_email/private_key missing).");
        return new ServiceAccountKey(email, key, root.GetText("token_uri"));
    }

    public override string ToString() => $"ServiceAccountKey {{ ClientEmail = {ClientEmail} }}"; // never print the key
}
