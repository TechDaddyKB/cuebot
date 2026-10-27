using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Cuebot.Speech.Tests.Fakes;

namespace Cuebot.Speech.Tests;

public class CloudEngineTests
{
    private static readonly byte[] WavBytes = Wav.FromPcm16([1, 0, 2, 0], 24000, 1);
    private static readonly byte[] Mp3Bytes = [(byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0, 0xFF, 0xFB, 0x90, 0x00];

    private static SynthesisRequest Say(string text, string voice, int rate = 0, int pitch = 0, string style = "") =>
        new(text, voice) { Rate = rate, Pitch = pitch, Style = style };

    // ---------------------------------------------------------------- Azure

    [Fact]
    public async Task Azure_synthesizes_ssml_with_prosody_and_style()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Bytes(WavBytes));
        var engine = new AzureEngine(handler.Client(), () => new AzureSettings { Key = " key1 ", Region = "EastUS" });

        var audio = await engine.SynthesizeAsync(Say("Tom & <Jerry>", "en-US-JennyNeural", rate: 2, pitch: -2, style: "cheerful"), default);

        Assert.Equal(AudioFormat.Wav, audio.Format);
        var request = handler.Last;
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal("https://eastus.tts.speech.microsoft.com/cognitiveservices/v1", request.Uri.ToString());
        Assert.Equal("key1", request.Header("Ocp-Apim-Subscription-Key"));
        Assert.Equal(AzureEngine.OutputFormat, request.Header("X-Microsoft-OutputFormat"));
        Assert.StartsWith("application/ssml+xml", request.ContentType);
        Assert.Contains("xml:lang=\"en-US\"", request.Body);
        Assert.Contains("<voice name=\"en-US-JennyNeural\"><mstts:express-as style=\"cheerful\">"
                        + "<prosody rate=\"+20%\" pitch=\"-10%\">Tom &amp; &lt;Jerry&gt;</prosody></mstts:express-as></voice>", request.Body);
    }

    [Fact]
    public void Azure_plain_text_has_no_prosody_or_style() =>
        Assert.EndsWith("<voice name=\"de-DE-KatjaNeural\">hallo</voice></speak>", AzureEngine.BuildSsml(Say("hallo", "de-DE-KatjaNeural")));

    [Fact]
    public async Task Azure_lists_voices_with_tier_and_styles()
    {
        const string json = """
            [{"Name":"Microsoft Server Speech Text to Speech Voice (en-US, JennyNeural)","DisplayName":"Jenny","ShortName":"en-US-JennyNeural",
              "Gender":"Female","Locale":"en-US","VoiceType":"Neural","StyleList":["cheerful","sad"]},
             {"DisplayName":"No short name"}]
            """;
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(json));
        var engine = new AzureEngine(handler.Client(), () => new AzureSettings { Key = "k", Region = "westeurope" });

        var voice = Assert.Single(await engine.GetVoicesAsync(default));

        Assert.Equal("https://westeurope.tts.speech.microsoft.com/cognitiveservices/voices/list", handler.Last.Uri.ToString());
        Assert.Equal(("en-US-JennyNeural", "Jenny", "en-US", "Female", "Neural"), (voice.Id, voice.Name, voice.Language, voice.Gender, voice.Tier));
        Assert.Equal(["cheerful", "sad"], voice.Styles);
    }

    [Theory]
    [InlineData("", "eastus")]
    [InlineData("k", "")]
    [InlineData("k", "evil.com/x")]
    public void Azure_needs_key_and_valid_region(string key, string region)
    {
        var engine = new AzureEngine(new HttpClient(), () => new AzureSettings { Key = key, Region = region });

        Assert.False(engine.IsAvailable);
        Assert.NotNull(engine.UnavailableReason);
    }

    // ---------------------------------------------------------------- AWS SigV4 / Polly

    private const string ExampleAccessKey = "AKIDEXAMPLE";
    private const string ExampleSecret = "wJalrXUtnFEMI/K7MDENG+bPxRfiCYEXAMPLEKEY";
    private static readonly DateTimeOffset ExampleTime = new(2015, 8, 30, 12, 36, 0, TimeSpan.Zero);

    [Fact]
    public void SigV4_matches_the_aws_get_vanilla_test_vector()
    {
        var signature = AwsSigV4.Compute("GET", new Uri("https://example.amazonaws.com/"), new Dictionary<string, string>(), [],
            ExampleAccessKey, ExampleSecret, "us-east-1", "service", ExampleTime);

        Assert.Equal("GET\n/\n\nhost:example.amazonaws.com\nx-amz-date:20150830T123600Z\n\nhost;x-amz-date\n"
                     + "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", signature.CanonicalRequest);
        Assert.Equal("AWS4-HMAC-SHA256\n20150830T123600Z\n20150830/us-east-1/service/aws4_request\n"
                     + "bb579772317eb040ac9ed261061d46c1f17a8133879d6129b6e1c25292927e63", signature.StringToSign);
        Assert.Equal("5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31", signature.Value);
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIDEXAMPLE/20150830/us-east-1/service/aws4_request, "
                     + "SignedHeaders=host;x-amz-date, Signature=5fa00fa31553b73ebf1942676e86291e8372ff2a2260956d9b8aae1d763fbf31",
            signature.Authorization);
    }

    [Fact]
    public void SigV4_matches_the_aws_iam_list_users_example()
    {
        var signature = AwsSigV4.Compute("GET", new Uri("https://iam.amazonaws.com/?Action=ListUsers&Version=2010-05-08"),
            new Dictionary<string, string> { ["Content-Type"] = "application/x-www-form-urlencoded; charset=utf-8" }, [],
            ExampleAccessKey, ExampleSecret, "us-east-1", "iam", ExampleTime);

        Assert.Equal("content-type;host;x-amz-date", signature.SignedHeaders);
        Assert.EndsWith("f536975d06c0309214f805bb90ccff089219ecd68b2577efef23edd43b7e1a59", signature.StringToSign);
        Assert.Equal("5d672d79c15b13162d9279b0855cfba6789a8edb4c82c400e06b5924a6f2b5d7", signature.Value);
    }

    [Fact]
    public void SigV4_sorts_and_encodes_the_query()
    {
        var signature = AwsSigV4.Compute("GET", new Uri("https://example.amazonaws.com/?b=2&a=x%20y&a=1"),
            new Dictionary<string, string>(), [], "k", "s", "us-east-1", "service", ExampleTime);

        Assert.Equal("a=1&a=x%20y&b=2", signature.CanonicalRequest.Split('\n')[2]);
        Assert.Equal("%2A~-_.A%C3%A9", AwsSigV4.Encode("*~-_.Aé"));
    }

    [Fact]
    public async Task Polly_signs_requests_and_sends_ssml_for_rate()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Bytes([1, 0, 2, 0]));
        var engine = new PollyEngine(handler.Client(),
            () => new PollySettings { AccessKeyId = "AKID", SecretAccessKey = "secret", Region = "eu-west-1", Engine = "neural" },
            () => ExampleTime);

        var audio = await engine.SynthesizeAsync(Say("a < b", "Joanna", rate: 4, pitch: 5), default);

        var info = Wav.Parse(audio.Data);
        Assert.Equal(PollyEngine.SampleRate, info.Format.SampleRate);
        var request = handler.Last;
        Assert.Equal("https://polly.eu-west-1.amazonaws.com/v1/speech", request.Uri.ToString());
        Assert.Equal("20150830T123600Z", request.Header("X-Amz-Date"));
        Assert.StartsWith("AWS4-HMAC-SHA256 Credential=AKID/20150830/eu-west-1/polly/aws4_request, SignedHeaders=content-type;host;x-amz-date, Signature=",
            request.Header("Authorization"));
        Assert.DoesNotContain("secret", request.Header("Authorization"));
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("neural", body.RootElement.GetProperty("Engine").GetString());
        Assert.Equal("pcm", body.RootElement.GetProperty("OutputFormat").GetString());
        Assert.Equal("ssml", body.RootElement.GetProperty("TextType").GetString());
        Assert.Equal("Joanna", body.RootElement.GetProperty("VoiceId").GetString());
        // Neural voices do not support pitch, so only the rate is sent.
        Assert.Equal("<speak><prosody rate=\"+40%\">a &lt; b</prosody></speak>", body.RootElement.GetProperty("Text").GetString());
    }

    [Fact]
    public void Polly_sends_pitch_to_standard_voices_and_plain_text_when_normal()
    {
        var standard = PollyEngine.BuildBody(Say("hi", "Brian:standard", pitch: -2), "neural");
        Assert.Equal("standard", standard["Engine"]!.GetValue<string>());
        Assert.Equal("<speak><prosody pitch=\"-10%\">hi</prosody></speak>", standard["Text"]!.GetValue<string>());

        var plain = PollyEngine.BuildBody(Say("hi", "Ruth:generative"), "neural");
        Assert.Equal("generative", plain["Engine"]!.GetValue<string>());
        Assert.Equal("text", plain["TextType"]!.GetValue<string>());
        Assert.Equal("hi", plain["Text"]!.GetValue<string>());

        Assert.Equal(("Matthew", "standard"), PollyEngine.ParseVoiceId("Matthew:bogus", "nonsense"));
    }

    [Fact]
    public async Task Polly_lists_a_voice_per_engine_and_follows_next_token()
    {
        var pages = new Queue<string>(
        [
            """{"Voices":[{"Id":"Joanna","Name":"Joanna","LanguageCode":"en-US","Gender":"Female","SupportedEngines":["neural","standard"]}],"NextToken":"abc"}""",
            """{"Voices":[{"Id":"Hans","Name":"Hans","LanguageCode":"de-DE","Gender":"Male","SupportedEngines":["standard"]}]}""",
        ]);
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(pages.Dequeue()));
        var engine = new PollyEngine(handler.Client(), () => new PollySettings { AccessKeyId = "a", SecretAccessKey = "b" }, () => ExampleTime);

        var voices = await engine.GetVoicesAsync(default);

        Assert.Equal(["Joanna:neural", "Joanna:standard", "Hans:standard"], voices.Select(v => v.Id));
        Assert.Equal("Joanna (Neural)", voices[0].Name);
        Assert.Equal("Neural", voices[0].Tier);
        Assert.Equal("https://polly.us-east-1.amazonaws.com/v1/voices?NextToken=abc", handler.Requests[1].Uri.ToString());
    }

    [Fact]
    public void Polly_needs_both_keys()
    {
        Assert.False(new PollyEngine(new HttpClient(), () => new PollySettings { AccessKeyId = "a" }).IsAvailable);
        Assert.False(new PollyEngine(new HttpClient(), () => new PollySettings { AccessKeyId = "a", SecretAccessKey = "b", Region = "x/y" }).IsAvailable);
        Assert.True(new PollyEngine(new HttpClient(), () => new PollySettings { AccessKeyId = "a", SecretAccessKey = "b" }).IsAvailable);
    }

    // ---------------------------------------------------------------- Google

    [Fact]
    public async Task Google_uses_the_api_key_header_and_audio_config()
    {
        var response = JsonSerializer.Serialize(new { audioContent = Convert.ToBase64String(WavBytes) });
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(response));
        var engine = new GoogleEngine(handler.Client(), () => new GoogleSettings { ApiKey = "gkey" });

        var audio = await engine.SynthesizeAsync(Say("hello", "en-GB-Wavenet-B", rate: -4, pitch: 3), default);

        Assert.Equal(WavBytes, audio.Data);
        var request = handler.Last;
        Assert.Equal("https://texttospeech.googleapis.com/v1/text:synthesize", request.Uri.ToString());
        Assert.Equal("gkey", request.Header("X-Goog-Api-Key"));
        Assert.DoesNotContain("gkey", request.Uri.ToString());
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("hello", body.RootElement.GetProperty("input").GetProperty("text").GetString());
        Assert.Equal("en-GB", body.RootElement.GetProperty("voice").GetProperty("languageCode").GetString());
        Assert.Equal("en-GB-Wavenet-B", body.RootElement.GetProperty("voice").GetProperty("name").GetString());
        var config = body.RootElement.GetProperty("audioConfig");
        Assert.Equal("LINEAR16", config.GetProperty("audioEncoding").GetString());
        Assert.Equal(0.8, config.GetProperty("speakingRate").GetDouble());
        Assert.Equal(6, config.GetProperty("pitch").GetDouble());
    }

    [Fact]
    public void Google_does_not_send_pitch_to_chirp_voices()
    {
        var body = GoogleEngine.BuildBody(Say("x", "en-US-Chirp3-HD-Aoede", pitch: 5));
        Assert.False(body["audioConfig"]!.AsObject().ContainsKey("pitch"));
    }

    [Fact]
    public async Task Google_lists_voices_with_tiers()
    {
        const string json = """
            {"voices":[{"languageCodes":["en-US"],"name":"en-US-Wavenet-A","ssmlGender":"MALE"},
                       {"languageCodes":["de-DE"],"name":"de-DE-Standard-B","ssmlGender":"FEMALE"}]}
            """;
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(json));
        var engine = new GoogleEngine(handler.Client(), () => new GoogleSettings { ApiKey = "k" });

        var voices = await engine.GetVoicesAsync(default);

        Assert.Equal(["WaveNet", "Standard"], voices.Select(v => v.Tier));
        Assert.Equal(["Male", "Female"], voices.Select(v => v.Gender));
        Assert.Equal("https://texttospeech.googleapis.com/v1/voices", handler.Last.Uri.ToString());
    }

    [Fact]
    public async Task Google_service_account_signs_a_jwt_and_caches_the_token()
    {
        using var rsa = RSA.Create(2048);
        var file = Path.Combine(Path.GetTempPath(), $"cuebot-google-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, JsonSerializer.Serialize(new
        {
            type = "service_account",
            client_email = "bot@project.iam.gserviceaccount.com",
            private_key = rsa.ExportPkcs8PrivateKeyPem(),
            token_uri = "https://oauth2.googleapis.com/token",
        }));
        try
        {
            var handler = new FakeHttpHandler(request => request.RequestUri!.Host == "oauth2.googleapis.com"
                ? FakeHttpHandler.Json("""{"access_token":"ya29.token","expires_in":3600}""")
                : FakeHttpHandler.Json("""{"voices":[]}"""));
            var engine = new GoogleEngine(handler.Client(), () => new GoogleSettings { ServiceAccountFile = file }, () => ExampleTime);

            Assert.True(engine.IsAvailable);
            await engine.GetVoicesAsync(default);
            await engine.GetVoicesAsync(default);

            Assert.Equal(3, handler.Requests.Count); // one token exchange, two API calls
            var tokenRequest = handler.Requests[0];
            Assert.Contains("grant_type=urn%3Aietf%3Aparams%3Aoauth%3Agrant-type%3Ajwt-bearer", tokenRequest.Body);
            var assertion = Uri.UnescapeDataString(tokenRequest.Body!.Split("assertion=")[1]);
            var parts = assertion.Split('.');
            Assert.Equal(3, parts.Length);
            var claims = JsonDocument.Parse(Base64UrlDecode(parts[1])).RootElement;
            Assert.Equal("bot@project.iam.gserviceaccount.com", claims.GetProperty("iss").GetString());
            Assert.Equal(GoogleEngine.Scope, claims.GetProperty("scope").GetString());
            Assert.Equal(ExampleTime.ToUnixTimeSeconds() + 3600, claims.GetProperty("exp").GetInt64());
            Assert.True(rsa.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), Base64UrlDecode(parts[2]),
                HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
            Assert.Equal("Bearer ya29.token", handler.Requests[2].Header("Authorization"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Google_reports_a_missing_key_file()
    {
        var engine = new GoogleEngine(new HttpClient(), () => new GoogleSettings { ServiceAccountFile = "Z:/does/not/exist.json" });
        Assert.Contains("not found", engine.UnavailableReason);
    }

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '='));
    }

    // ---------------------------------------------------------------- IBM Watson

    [Fact]
    public async Task Watson_uses_basic_apikey_auth_and_percentages()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Bytes(WavBytes));
        var engine = new WatsonEngine(handler.Client(),
            () => new WatsonSettings { ApiKey = "wkey", Url = "https://api.us-south.text-to-speech.watson.cloud.ibm.com/instances/abc/" });

        await engine.SynthesizeAsync(Say("hi there", "en-US_AllisonV3Voice", rate: -2, pitch: 4), default);

        var request = handler.Last;
        Assert.Equal("https://api.us-south.text-to-speech.watson.cloud.ibm.com/instances/abc/v1/synthesize?voice=en-US_AllisonV3Voice&rate_percentage=-10&pitch_percentage=20",
            request.Uri.ToString());
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("apikey:wkey")), request.Header("Authorization"));
        Assert.Equal("audio/wav", request.Header("Accept"));
        Assert.Equal("""{"text":"hi there"}""", request.Body);
    }

    [Fact]
    public async Task Watson_reads_the_credentials_file_and_lists_voices()
    {
        var file = Path.Combine(Path.GetTempPath(), $"ibm-credentials-{Guid.NewGuid():N}.env");
        await File.WriteAllLinesAsync(file,
        [
            "# downloaded from IBM Cloud",
            "TEXT_TO_SPEECH_APIKEY=filekey",
            "TEXT_TO_SPEECH_IAM_APIKEY=ignored-second",
            "TEXT_TO_SPEECH_URL=https://api.eu-de.text-to-speech.watson.cloud.ibm.com/instances/x",
            "TEXT_TO_SPEECH_AUTH_TYPE=iam",
        ]);
        try
        {
            var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
                """{"voices":[{"name":"de-DE_BirgitV3Voice","language":"de-DE","gender":"female","description":"Birgit: German"}]}"""));
            var engine = new WatsonEngine(handler.Client(), () => new WatsonSettings { CredentialsFile = file });

            var voice = Assert.Single(await engine.GetVoicesAsync(default));

            Assert.Equal(("de-DE_BirgitV3Voice", "Birgit: German", "de-DE", "Female", "Neural"),
                (voice.Id, voice.Name, voice.Language, voice.Gender, voice.Tier));
            Assert.Equal("https://api.eu-de.text-to-speech.watson.cloud.ibm.com/instances/x/v1/voices", handler.Last.Uri.ToString());
            Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("apikey:filekey")), handler.Last.Header("Authorization"));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Watson_needs_an_https_url()
    {
        Assert.False(new WatsonEngine(new HttpClient(), () => new WatsonSettings { ApiKey = "k", Url = "http://plain" }).IsAvailable);
        Assert.Contains("not found", new WatsonEngine(new HttpClient(), () => new WatsonSettings { CredentialsFile = "Z:/nope.env" }).UnavailableReason);
    }

    // ---------------------------------------------------------------- UberDuck

    [Fact]
    public async Task UberDuck_uses_basic_auth_and_passes_mp3_through()
    {
        var handler = new FakeHttpHandler(request => request.Method == HttpMethod.Get
            ? FakeHttpHandler.Json("""[{"name":"zwf","display_name":"ZWF","category":"Rapper"}]""")
            : FakeHttpHandler.Bytes(Mp3Bytes));
        var engine = new UberDuckEngine(handler.Client(), () => new UberDuckSettings { ApiKey = "pub", ApiSecret = "sec" });

        var voices = await engine.GetVoicesAsync(default);
        var audio = await engine.SynthesizeAsync(Say("yo", "zwf"), default);

        Assert.Equal(new VoiceInfo("zwf", "ZWF", null) { Tier = "Rapper" }, Assert.Single(voices));
        Assert.Equal("https://api.uberduck.ai/voices?mode=tts-basic", handler.Requests[0].Uri.ToString());
        Assert.Equal(AudioFormat.Mp3, audio.Format);
        Assert.Equal("https://api.uberduck.ai/speak-synchronous", handler.Last.Uri.ToString());
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("pub:sec")), handler.Last.Header("Authorization"));
        Assert.Equal("""{"speech":"yo","voice":"zwf"}""", handler.Last.Body);
    }

    // ---------------------------------------------------------------- TTS Monster

    [Fact]
    public async Task TtsMonster_generates_then_downloads_the_audio()
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.Host switch
        {
            "api.console.tts.monster" => FakeHttpHandler.Json("""{"status":200,"url":"https://cdn.tts.monster/abc.wav","characterUsage":2}"""),
            _ => FakeHttpHandler.Bytes(WavBytes),
        });
        var engine = new TtsMonsterEngine(handler.Client(), () => new TtsMonsterSettings { ApiKey = "ttsm_key" });

        var audio = await engine.SynthesizeAsync(Say("hey", "voice-uuid"), default);

        Assert.Equal(AudioFormat.Wav, audio.Format);
        Assert.Equal("https://api.console.tts.monster/generate", handler.Requests[0].Uri.ToString());
        Assert.Equal("ttsm_key", handler.Requests[0].Header("Authorization"));
        Assert.Equal("""{"voice_id":"voice-uuid","message":"hey"}""", handler.Requests[0].Body);
        Assert.Equal("https://cdn.tts.monster/abc.wav", handler.Requests[1].Uri.ToString());
        Assert.Equal("", handler.Requests[1].Header("Authorization"));
    }

    [Fact]
    public async Task TtsMonster_lists_stock_and_custom_voices()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json(
            """{"voices":[{"voice_id":"a","name":"Axel"}],"customVoices":[{"voice_id":"c","name":"Mine"}]}"""));
        var engine = new TtsMonsterEngine(handler.Client(), () => new TtsMonsterSettings { ApiKey = "k" });

        var voices = await engine.GetVoicesAsync(default);

        Assert.Equal([new VoiceInfo("a", "Axel", null), new VoiceInfo("c", "Mine", null) { Tier = "Custom" }], voices);
        Assert.Equal(HttpMethod.Post, handler.Last.Method);
        Assert.Equal("https://api.console.tts.monster/voices", handler.Last.Uri.ToString());
    }

    [Theory]
    [InlineData("plainkey", "plainkey")]
    [InlineData("https://tts.monster/overlay?key=abc%2B1&x=y", "abc+1")]
    [InlineData("https://tts.monster/overlay/user/ttsm_xyz", "ttsm_xyz")]
    public void TtsMonster_extracts_the_key_from_an_overlay_url(string value, string expected) =>
        Assert.Equal(expected, TtsMonsterEngine.ExtractKey(value));

    [Fact]
    public async Task TtsMonster_rejects_non_https_audio_links()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"url":"file:///etc/passwd"}"""));
        var engine = new TtsMonsterEngine(handler.Client(), () => new TtsMonsterSettings { ApiKey = "k" });

        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.SynthesizeAsync(Say("x", "v"), default));
        Assert.Single(handler.Requests);
    }

    // ---------------------------------------------------------------- Acapela

    [Fact]
    public async Task Acapela_logs_in_once_and_renders_with_speed_and_shaping()
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/api/login/" => FakeHttpHandler.Json("""{"token":"tok123"}"""),
            "/api/voices/" => FakeHttpHandler.Json("""[{"id":"Ella22k_NT","name":"Ella","language":"en-US","gender":"female"}]"""),
            _ => FakeHttpHandler.Bytes(Mp3Bytes),
        });
        var engine = new AcapelaEngine(handler.Client(), () => new AcapelaSettings { Email = "me@x.com", Password = "pw" });

        var voices = await engine.GetVoicesAsync(default);
        var audio = await engine.SynthesizeAsync(Say("hi & bye", "Ella22k_NT", rate: 2, pitch: -4), default);

        Assert.Equal("Ella", Assert.Single(voices).Name);
        Assert.Equal(AudioFormat.Mp3, audio.Format);
        Assert.Equal(3, handler.Requests.Count);
        Assert.Equal("""{"email":"me@x.com","password":"pw"}""", handler.Requests[0].Body);
        Assert.Equal("Token tok123", handler.Last.Header("Authorization"));
        Assert.Equal("https://www.acapela-cloud.com/api/command/?voice=Ella22k_NT&text=hi%20%26%20bye&output=stream&type=wav&speed=120&shaping=80",
            handler.Last.Uri.AbsoluteUri);
    }

    [Fact]
    public async Task Acapela_logs_in_again_when_the_token_is_rejected()
    {
        var logins = 0;
        var rejected = false;
        var handler = new FakeHttpHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/api/login/")
                return FakeHttpHandler.Json($$"""{"token":"t{{++logins}}"}""");
            if (!rejected)
            {
                rejected = true;
                return FakeHttpHandler.Json("""{"detail":"expired"}""", HttpStatusCode.Unauthorized);
            }
            return FakeHttpHandler.Json("[]");
        });
        var engine = new AcapelaEngine(handler.Client(), () => new AcapelaSettings { Email = "e", Password = "p" });

        await engine.GetVoicesAsync(default);

        Assert.Equal(2, logins);
        Assert.Equal("Token t2", handler.Last.Header("Authorization"));
    }

    // ---------------------------------------------------------------- CereProc

    [Fact]
    public async Task CereProc_authenticates_and_posts_text_or_ssml()
    {
        var handler = new FakeHttpHandler(request => request.RequestUri!.AbsolutePath switch
        {
            "/v2/auth" => FakeHttpHandler.Json("""{"access_token":"cere"}"""),
            "/v2/voices" => FakeHttpHandler.Json("""{"voices":[{"name":"Heather-CereWave","language_ISO":"en","country_ISO":"GB","sex":"female"}]}"""),
            _ => FakeHttpHandler.Bytes(WavBytes),
        });
        var engine = new CereProcEngine(handler.Client(), () => new CereProcSettings { Email = "me@x.com", Password = "pw" });

        var voice = Assert.Single(await engine.GetVoicesAsync(default));
        await engine.SynthesizeAsync(Say("plain", "Heather-CereWave"), default);
        await engine.SynthesizeAsync(Say("fast", "Heather-CereWave", rate: 3), default);

        Assert.Equal(new VoiceInfo("Heather-CereWave", "Heather-CereWave", "en-GB") { Gender = "Female" }, voice);
        Assert.Equal("Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("me@x.com:pw")), handler.Requests[0].Header("Authorization"));
        Assert.Equal(4, handler.Requests.Count); // the token is reused
        var plain = handler.Requests[2];
        Assert.Equal("https://api.cerevoice.com/v2/speak?voice=Heather-CereWave&audio_format=wav", plain.Uri.ToString());
        Assert.Equal("Bearer cere", plain.Header("Authorization"));
        Assert.Equal("plain", plain.Body);
        Assert.Equal("<speak><prosody rate=\"+30%\">fast</prosody></speak>", handler.Last.Body);
    }

    // ---------------------------------------------------------------- shared

    public static TheoryData<string> EngineIds => ["azure", "polly", "google", "watson", "uberduck", "ttsmonster", "acapela", "cereproc"];

    [Theory]
    [MemberData(nameof(EngineIds))]
    public async Task Unconfigured_engines_are_unavailable_and_send_nothing(string id)
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("{}"));
        var engine = CreateUnconfigured(id, handler.Client());

        Assert.Equal(id, engine.Id);
        Assert.False(engine.IsAvailable);
        Assert.False(string.IsNullOrWhiteSpace(engine.UnavailableReason));
        await Assert.ThrowsAnyAsync<Exception>(() => engine.GetVoicesAsync(default));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Errors_name_the_engine_and_status_but_not_the_key()
    {
        var handler = new FakeHttpHandler(_ => FakeHttpHandler.Json("""{"error":"bad key"}""", HttpStatusCode.Forbidden));
        var engine = new AzureEngine(handler.Client(), () => new AzureSettings { Key = "supersecret", Region = "eastus" });

        var error = await Assert.ThrowsAsync<HttpRequestException>(() => engine.GetVoicesAsync(default));

        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
        Assert.Contains("Microsoft Azure returned 403", error.Message);
        Assert.Contains("bad key", error.Message);
        Assert.DoesNotContain("supersecret", error.Message);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(-10, -50)]
    [InlineData(10, 100)]
    [InlineData(3, 30)]
    [InlineData(-4, -20)]
    [InlineData(99, 100)]
    public void Rate_maps_to_ssml_percent(int rate, int percent) => Assert.Equal(percent, Prosody.RatePercent(rate));

    [Fact]
    public void Audio_containers_are_sniffed()
    {
        Assert.Equal(AudioFormat.Wav, AudioSniffer.Detect(WavBytes));
        Assert.Equal(AudioFormat.Ogg, AudioSniffer.Detect("OggS...."u8));
        Assert.Equal(AudioFormat.Mp3, AudioSniffer.Detect(Mp3Bytes));
    }

    private static ITtsEngine CreateUnconfigured(string id, HttpClient http) => id switch
    {
        "azure" => new AzureEngine(http, () => new AzureSettings()),
        "polly" => new PollyEngine(http, () => new PollySettings()),
        "google" => new GoogleEngine(http, () => new GoogleSettings()),
        "watson" => new WatsonEngine(http, () => new WatsonSettings()),
        "uberduck" => new UberDuckEngine(http, () => new UberDuckSettings()),
        "ttsmonster" => new TtsMonsterEngine(http, () => new TtsMonsterSettings()),
        "acapela" => new AcapelaEngine(http, () => new AcapelaSettings()),
        "cereproc" => new CereProcEngine(http, () => new CereProcSettings()),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };
}
