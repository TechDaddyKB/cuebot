using System.Text.Json.Nodes;
using Cuebot.App.Diagnostics;

namespace Cuebot.UiTests.Support;

/// <summary>
/// The scrubber against realistic samples of every secret Cuebot stores (twitch.json, youtube.json, kick.json,
/// obs.json, servers.json, donations.json, speech.json) and against secrets in free text (log lines).
/// </summary>
public sealed class SecretScrubberTests
{
    // Realistic-looking fake values (none of these are real credentials).
    private const string TwitchAccess = "g5xk2m9q7w1r4t8y0u3i6o2p5a7s9d";
    private const string TwitchRefresh = "rfr8zx7c6v5b4n3m2l1k0j9h8g7f6d5s4a3q2w1e0r9t8y7u6";
    private const string BotAccess = "botacc0ss9tok8en7val6ue5xyz4abc";
    private const string YtAccess = "ya29.a0AfB_byC8xYzQ1w2E3r4T5y6U7i8O9p0AsDfGhJkL";
    private const string YtRefresh = "1//0gLkJhGfDsA-L9Ir8Ux7Yz6Wv5Ts4Rq3Po2Nm1Lk0";
    private const string YtSecret = "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";
    private const string KickSecret = "f1e2d3c4b5a6978877665544332211aabbccddeeff00112233445566778899aa";
    private const string KickAccess = "NZG3MZY0MTITYZE1NI0ZMJLLLWEXMDKTOGQ2OTJHNZE5ZDE5";
    private const string ObsPassword = "My0bsP@ss!";
    private const string ObsPassword2 = "second-obs-pw-42";
    private const string WsPassword = "sb-ws-secret-pass";
    private const string SocketToken = "eyJ0eXAiOiJKV1QiLCJhbGciOiJIUzI1NiJ9.eyJ0b2tlbiI6IjEyMzQ1Njc4OTAifQ.Sl0ck3tSignAtur3";
    private const string SeJwt = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJjaGFubmVsIjoiNWY4YTAwIn0.c2lnbmF0dXJlLWJ5dGVz";
    private const string ElevenKey = "sk_3f9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d3e2f1a0b9c8d7e";
    private const string AzureKey = "0123456789abcdef0123456789abcdef";
    private const string PollyId = "AKIAIOSFODNN7EXAMPLE";
    private const string PollySecret = "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY";
    private const string GoogleKey = "AIzaSyD-9tSrke72PouQMnMX-a7eZSW0jkFMBWY";
    private const string WatsonKey = "wAtSoNkEy-1234567890abcdefGHIJKLMNOP";
    private const string UberKey = "pub_abcdefghijk12345";
    private const string UberSecret = "pk_live_uberduck_secret_998877";
    private const string MonsterKey = "ttsm_0a1b2c3d4e5f6a7b8c9d";
    private const string AcapelaPassword = "acapela-pw-Secret1";
    private const string CerePassword = "cereproc pw with spaces";

    private static readonly string[] AllSecrets =
    [
        TwitchAccess, TwitchRefresh, BotAccess, YtAccess, YtRefresh, YtSecret, KickSecret, KickAccess, ObsPassword, ObsPassword2,
        WsPassword, SocketToken, SeJwt, ElevenKey, AzureKey, PollyId, PollySecret, GoogleKey, WatsonKey, UberKey, UberSecret,
        MonsterKey, AcapelaPassword, CerePassword,
    ];

    public static string TwitchJson => $$"""
        {
          "clientId": "abc123clientid",
          "accessToken": "{{TwitchAccess}}",
          "refreshToken": "{{TwitchRefresh}}",
          "userId": "123456789",
          "login": "techdaddykb",
          "autoConnect": true,
          "scopes": ["chat:read", "chat:edit"],
          "bot": { "accessToken": "{{BotAccess}}", "refreshToken": "", "userId": "987654321", "login": "cuebot_helper", "scopes": [] },
          "sendChatAsBot": true
        }
        """;

    public static string YouTubeJson => $$"""
        {
          "clientId": "1234-abc.apps.googleusercontent.com",
          "clientSecret": "{{YtSecret}}",
          "autoConnect": true,
          "broadcaster": {
            "accessToken": "{{YtAccess}}", "refreshToken": "{{YtRefresh}}", "accessTokenExpiresAt": "2026-10-27T12:00:00+00:00",
            "channelId": "UCabcdefghijklmnopqrstuv", "channelTitle": "Tech Daddy Streams", "customUrl": "@techdaddystreams",
            "profileImageUrl": "https://yt3.ggpht.com/abc"
          },
          "dailyQuota": 10000
        }
        """;

    public static string KickJson => $$"""
        {
          "clientId": "01JABCDEF", "clientSecret": "{{KickSecret}}", "redirectPort": 53682,
          "broadcaster": { "accessToken": "{{KickAccess}}", "refreshToken": "", "userId": 4242, "userName": "kickstreamer" },
          "channelSlug": "kickstreamer", "channelId": 4343, "useWebhookRelay": false
        }
        """;

    public static string ObsJson => $$"""
        { "name": "Main", "host": "127.0.0.1", "port": 4455, "password": "{{ObsPassword}}", "autoConnect": true,
          "connections": [ { "name": "Second", "host": "192.168.1.20", "port": 4455, "password": "{{ObsPassword2}}" } ] }
        """;

    public static string ServersJson => $$"""
        { "version": 1, "allowLan": false,
          "webSocket": { "enabled": true, "port": 8080, "authenticationEnabled": true, "password": "{{WsPassword}}", "enforceAuthentication": false } }
        """;

    public static string DonationsJson => $$"""
        { "streamlabs": { "socketToken": "{{SocketToken}}", "autoConnect": true },
          "streamElements": { "jwtToken": "{{SeJwt}}", "channelId": "5f8a00aa11bb22cc33dd44ee", "channelName": "techdaddykb" } }
        """;

    public static string SpeechJson => $$"""
        { "elevenLabsApiKey": "{{ElevenKey}}",
          "engines": {
            "azure": { "key": "{{AzureKey}}", "region": "eastus" },
            "polly": { "accessKeyId": "{{PollyId}}", "secretAccessKey": "{{PollySecret}}", "region": "us-east-1", "engine": "neural" },
            "google": { "apiKey": "{{GoogleKey}}", "serviceAccountFile": "" },
            "watson": { "apiKey": "{{WatsonKey}}", "url": "https://api.us-south.text-to-speech.watson.cloud.ibm.com/instances/abc" },
            "uberDuck": { "apiKey": "{{UberKey}}", "apiSecret": "{{UberSecret}}" },
            "ttsMonster": { "apiKey": "{{MonsterKey}}" },
            "acapela": { "email": "me@example.com", "password": "{{AcapelaPassword}}" },
            "cereProc": { "email": "other@example.org", "password": "{{CerePassword}}" }
          } }
        """;

    public static IEnumerable<TheoryDataRow<string, string>> SettingsFiles() =>
    [
        new("twitch", TwitchJson), new("youtube", YouTubeJson), new("kick", KickJson), new("obs", ObsJson),
        new("servers", ServersJson), new("donations", DonationsJson), new("speech", SpeechJson),
    ];

    [Theory]
    [MemberData(nameof(SettingsFiles))]
    public void Json_settings_lose_every_secret_and_stay_valid_json(string file, string json)
    {
        var scrubbed = SecretScrubber.ScrubJson(json);
        Assert.NotNull(JsonNode.Parse(scrubbed));
        foreach (var secret in AllSecrets) Assert.DoesNotContain(secret, scrubbed, StringComparison.Ordinal);
        Assert.Contains(SecretScrubber.Redacted, scrubbed);
        Assert.False(string.IsNullOrEmpty(file));
    }

    [Fact]
    public void Json_keeps_flags_numbers_and_empty_values_and_replaces_accounts()
    {
        var node = JsonNode.Parse(SecretScrubber.ScrubJson(TwitchJson))!;
        Assert.Equal("[redacted]", (string?)node["accessToken"]);
        Assert.Equal("", (string?)node["bot"]!["refreshToken"]);   // "not set" stays visible
        Assert.True((bool)node["autoConnect"]!);
        Assert.Equal("[account]", (string?)node["login"]);
        Assert.Equal("[account]", (string?)node["userId"]);
        Assert.Equal("[account]", (string?)node["bot"]!["login"]);
        Assert.Equal("chat:read", (string?)node["scopes"]![0]);

        var servers = JsonNode.Parse(SecretScrubber.ScrubJson(ServersJson))!;
        Assert.True((bool)servers["webSocket"]!["authenticationEnabled"]!);
        Assert.Equal(8080, (int)servers["webSocket"]!["port"]!);

        var kick = JsonNode.Parse(SecretScrubber.ScrubJson(KickJson))!;
        Assert.Equal("[account]", (string?)kick["broadcaster"]!["userId"]);   // numeric ids too
        Assert.Equal(53682, (int)kick["redirectPort"]!);

        var speech = SecretScrubber.ScrubJson(SpeechJson);
        Assert.DoesNotContain("example.com", speech);
        Assert.Contains("[email]", speech);
        Assert.Contains("eastus", speech);
    }

    [Fact]
    public void Account_names_are_kept_only_when_asked()
    {
        var with = SecretScrubber.ScrubJson(TwitchJson, new ScrubOptions { IncludeAccountNames = true });
        Assert.Contains("techdaddykb", with);
        Assert.DoesNotContain(TwitchAccess, with);

        var names = SecretScrubber.CollectAccountNames(TwitchJson).Concat(SecretScrubber.CollectAccountNames(YouTubeJson)).ToArray();
        Assert.Contains("techdaddykb", names);
        Assert.Contains("cuebot_helper", names);
        Assert.Contains("Tech Daddy Streams", names);
        Assert.Contains("@techdaddystreams", names);

        var line = "12:00:01 [Info] Twitch: Connected as techdaddykb (bot cuebot_helper) to Tech Daddy Streams";
        var scrubbed = SecretScrubber.ScrubText(line, new ScrubOptions { KnownAccountNames = names });
        Assert.Equal("12:00:01 [Info] Twitch: Connected as [account] (bot [account]) to [account]", scrubbed);
        Assert.Equal(line, SecretScrubber.ScrubText(line, new ScrubOptions { KnownAccountNames = names, IncludeAccountNames = true }));
    }

    public static IEnumerable<TheoryDataRow<string, string>> LogLines() =>
    [
        new("Twitch: token refreshed, access_token=" + TwitchAccess + "&scope=chat", TwitchAccess),
        new("HTTP GET https://localhost:53682/callback?code=Xy12AbCd34EfGh56&state=zz9", "Xy12AbCd34EfGh56"),
        new("Redirect to http://localhost/#access_token=" + TwitchAccess + "&token_type=bearer", TwitchAccess),
        new("Request headers: Authorization: Bearer " + YtAccess, YtAccess),
        new("Authorization=OAuth " + TwitchAccess, TwitchAccess),
        new("IRC PASS oauth:" + TwitchAccess, TwitchAccess),
        new("Streamlabs: connecting with token " + SocketToken, SocketToken),
        new("StreamElements: jwt " + SeJwt, SeJwt),
        new("{\"op\":6,\"password\":\"" + ObsPassword + "\"}", ObsPassword),
        new("OBS: password: " + ObsPassword2 + " rejected", ObsPassword2),
        new("Settings changed: clientSecret=" + YtSecret, YtSecret),
        new("Speech: ElevenLabsApiKey = " + ElevenKey, ElevenKey),
        new("Polly AccessKeyId " + PollyId, PollyId),
        new("Google request ?key=" + GoogleKey + "&alt=json", GoogleKey),
        new("Discord webhook https://discord.com/api/webhooks/123456789012345678/AbCdEfGhIjKlMnOpQrStUvWxYz-0123456789", "AbCdEfGhIjKlMnOpQrStUvWxYz-0123456789"),
        new("Slack https://hooks.slack.com/services/T000/B000/XXXXXXXXXXXXXXXX", "XXXXXXXXXXXXXXXX"),
        new("gh token ghp_abcdefghijklmnopqrstuvwxyz0123456789", "ghp_abcdefghijklmnopqrstuvwxyz0123456789"),
        new("Kick token " + KickAccess + " expired", KickAccess),
        new("stream_key: live_123456_abcdefABCDEF", "live_123456_abcdefABCDEF"),
        new("Login failed for me@example.com", "me@example.com"),
        new("Sent x-amz-signature=abcdef0123456789 to Polly", "abcdef0123456789"),
    ];

    [Theory]
    [MemberData(nameof(LogLines))]
    public void Free_text_secrets_are_removed(string line, string secret)
    {
        var scrubbed = SecretScrubber.ScrubText(line);
        Assert.DoesNotContain(secret, scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("12:00:00 [Info] Cuebot: Starting (display backend: Wayland)")]
    [InlineData("12:00:00 [Warning] OBS: Connection lost, retrying in 5 s")]
    [InlineData("12:00:00 [Error] Kick: Failed to start: The operation was canceled.")]
    [InlineData("12:00:00 [Info] Servers: WebSocket server listening on 127.0.0.1:8080")]
    [InlineData("   at Cuebot.Obs.ObsService.ConnectAsync(CancellationToken ct) in ObsService.cs:line 120")]
    [InlineData("authenticationEnabled: true")]
    [InlineData("password: \"\"")]
    public void Ordinary_log_lines_are_left_alone(string line) =>
        Assert.Equal(line, SecretScrubber.ScrubText(line, new ScrubOptions { HomeFolder = null }));

    [Fact]
    public void Home_folder_is_replaced_with_tilde()
    {
        var options = new ScrubOptions { HomeFolder = @"C:\Users\kris" };
        Assert.Equal(@"Data folder ~\AppData\Roaming\Cuebot", SecretScrubber.ScrubText(@"Data folder C:\Users\kris\AppData\Roaming\Cuebot", options));
        Assert.Equal("Log at ~/.config/Cuebot", SecretScrubber.ScrubText("Log at /home/kris/.config/Cuebot", new ScrubOptions { HomeFolder = "/home/kris" }));
    }

    [Fact]
    public void Not_json_falls_back_to_text_scrubbing()
    {
        var scrubbed = SecretScrubber.ScrubJson("password=" + ObsPassword + " {broken");
        Assert.DoesNotContain(ObsPassword, scrubbed);
    }

    [Theory]
    [InlineData("accessToken", true)]
    [InlineData("refresh_token", true)]
    [InlineData("ClientSecret", true)]
    [InlineData("webSocketPassword", true)]
    [InlineData("apiKey", true)]
    [InlineData("secretAccessKey", true)]
    [InlineData("key", true)]
    [InlineData("jwtToken", true)]
    [InlineData("socketToken", true)]
    [InlineData("webhookUrl", true)]
    [InlineData("hotkey", false)]
    [InlineData("keywords", false)]
    [InlineData("autoConnect", false)]
    [InlineData("region", false)]
    [InlineData("clientId", false)]
    public void Secret_names(string name, bool secret) => Assert.Equal(secret, SecretScrubber.IsSecretName(name));
}
