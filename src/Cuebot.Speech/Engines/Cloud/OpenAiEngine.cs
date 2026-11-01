using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Speech;

/// <summary>
/// Speaks through OpenAI's speech endpoint (POST /v1/audio/speech) with the user's own API key. The voices are a fixed
/// list. Rate maps to speed (0.25..4, 1 normal); pitch is not supported. "Instructions" steer the delivery (tone,
/// accent) on the gpt-4o models only.
/// </summary>
internal sealed class OpenAiEngine(HttpClient http, Func<OpenAiSpeechSettings> settings) : ITtsEngine
{
    internal const string Endpoint = "https://api.openai.com/v1/audio/speech";
    internal const string DefaultModel = "gpt-4o-mini-tts";

    /// <summary>OpenAI's input limit per request.</summary>
    internal const int MaxCharacters = 4096;

    internal static readonly IReadOnlyList<string> Models = ["gpt-4o-mini-tts", "tts-1", "tts-1-hd"];

    /// <summary>Moods offered in voice aliases; the chosen one is sent as the instructions.</summary>
    internal static readonly IReadOnlyList<string> Moods =
        ["Cheerful", "Calm", "Excited", "Sad", "Serious", "Whispering", "Spooky", "Like a sports announcer", "Like a pirate"];

    internal static readonly IReadOnlyList<string> VoiceNames =
        ["alloy", "ash", "ballad", "cedar", "coral", "echo", "fable", "marin", "nova", "onyx", "sage", "shimmer", "verse"];

    public string Id => "openai";
    public string DisplayName => "OpenAI";
    public bool IsAvailable => !string.IsNullOrWhiteSpace(settings().ApiKey);
    public string? UnavailableReason => IsAvailable ? null : "Enter an OpenAI API key in the speech settings.";

    public Task<IReadOnlyList<VoiceInfo>> GetVoicesAsync(CancellationToken ct)
    {
        if (!IsAvailable) throw new InvalidOperationException(UnavailableReason);
        IReadOnlyList<VoiceInfo> voices = [.. VoiceNames.Select(v => new VoiceInfo(v, CultureInfo.InvariantCulture.TextInfo.ToTitleCase(v), null) { Styles = Moods })];
        return Task.FromResult(voices);
    }

    public async Task<SynthesizedAudio> SynthesizeAsync(SynthesisRequest synthesis, CancellationToken ct)
    {
        var key = settings().ApiKey;
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException(UnavailableReason);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(BuildBody(synthesis, settings())),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());
        var wav = await CloudHttp.GetBytesAsync(http, request, DisplayName, ct).ConfigureAwait(false);
        // OpenAI streams WAV with placeholder sizes; rebuilding gives a header every player accepts.
        return SynthesizedAudio.Wav(Wav.Normalize(wav));
    }

    internal static JsonObject BuildBody(SynthesisRequest synthesis, OpenAiSpeechSettings settings)
    {
        var model = Models.Contains(settings.Model.Trim()) ? settings.Model.Trim() : DefaultModel;
        var voice = VoiceNames.Contains(synthesis.VoiceId) ? synthesis.VoiceId : "alloy";
        var text = synthesis.Text.Length > MaxCharacters ? synthesis.Text[..MaxCharacters] : synthesis.Text;
        var body = new JsonObject
        {
            ["model"] = model,
            ["input"] = text,
            ["voice"] = voice,
            ["response_format"] = "wav",
        };
        if (synthesis.Rate != 0) body["speed"] = RateToSpeed(synthesis.Rate);
        // A voice alias's style wins over the engine-wide instructions; tts-1 models ignore both.
        var instructions = synthesis.Style.Trim().Length > 0 ? synthesis.Style.Trim() : settings.Instructions.Trim();
        if (instructions.Length > 0 && model.StartsWith("gpt-", StringComparison.Ordinal)) body["instructions"] = instructions;
        return body;
    }

    /// <summary>Maps the -10..10 rate scale to OpenAI's speed (0.5..2, 1 normal; the API allows 0.25..4).</summary>
    internal static double RateToSpeed(int rate)
    {
        var clamped = Math.Clamp(rate, -10, 10);
        return Math.Round(clamped < 0 ? 1.0 + clamped * 0.05 : 1.0 + clamped * 0.1, 2);
    }
}
