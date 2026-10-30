using System.Net;
using System.Text.Json;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Twitch.Auth;
using Cuebot.Twitch.EventSub;
using Cuebot.Twitch.Helix;

namespace Cuebot.Twitch.Tests.Support;

/// <summary>
/// A <see cref="TwitchApi"/> over a fake Helix: the broadcaster "1337" is signed in with every scope (unless
/// changed); routes answer by "METHOD path", everything else gets 204.
/// </summary>
public sealed class ApiHarness : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-twitch-api-").FullName;
    private readonly Dictionary<string, Func<RecordedRequest, HttpResponseMessage>> _routes = new(StringComparer.Ordinal);

    public ApiHarness(bool connected = true)
    {
        var log = new AppLog();
        Settings = new JsonFile<TwitchSettings>(Path.Combine(_dir, "twitch.json"), log);
        var s = Settings.Value;
        s.ClientId = "client";
        s.AccessToken = "broadcaster-token";
        s.UserId = "1337";
        s.Login = "streamer";
        s.Scopes = [.. TwitchScopes.All];
        Http = new FakeHttpHandler(Respond);
        var client = new HttpClient(Http);
        var auth = new TwitchAuthClient(client, new Uri("https://id.test/oauth2/"), TimeProvider.System);
        var tokens = new TwitchTokenManager(Settings, auth, log, () => Settings.Value.ClientId.Trim());
        var botTokens = new TwitchTokenManager(Settings, auth, log, () => Settings.Value.ClientId.Trim(), x => new BotAccount(x), "Twitch bot");
        var helixBase = new Uri("https://api.test/helix/");
        Sent = new SentMessageTracker();
        Api = new TwitchApi(new HelixClient(client, helixBase, tokens), tokens, new HelixClient(client, helixBase, botTokens), botTokens,
            () => Settings.Value, () => connected, Published.Add, Sent);
    }

    public JsonFile<TwitchSettings> Settings { get; }
    public FakeHttpHandler Http { get; }
    public TwitchApi Api { get; }
    internal SentMessageTracker Sent { get; }
    public List<BotEvent> Published { get; } = [];

    public RecordedRequest Last => Http.Requests[^1];

    public JsonElement LastBody => JsonSerializer.Deserialize<JsonElement>(Last.Body);

    /// <summary>Query parameters of the last request.</summary>
    public IReadOnlyDictionary<string, string> LastQuery =>
        Last.Query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2))
            .ToDictionary(p => p[0], p => p.Length > 1 ? p[1] : "");

    public ApiHarness On(string methodAndPath, string json, HttpStatusCode status = HttpStatusCode.OK)
    {
        _routes[methodAndPath] = _ => FakeHttpHandler.Json(status, json);
        return this;
    }

    public void SignInBot(params string[] scopes)
    {
        var bot = Settings.Value.Bot;
        bot.AccessToken = "bot-token";
        bot.UserId = "777";
        bot.Login = "mybot";
        bot.Scopes = scopes.Length > 0 ? [.. scopes] : [.. TwitchScopes.Bot];
    }

    private HttpResponseMessage Respond(RecordedRequest r)
    {
        if (_routes.TryGetValue($"{r.Method.Method} {r.Path}", out var route)) return route(r);
        return r.Path switch
        {
            "/helix/chat/messages" => FakeHttpHandler.Json(HttpStatusCode.OK, """{"data":[{"message_id":"sent-1","is_sent":true}]}"""),
            "/helix/users" => FakeHttpHandler.Json(HttpStatusCode.OK,
                """{"data":[{"id":"4242","login":"target","display_name":"Target","type":"","broadcaster_type":"affiliate","description":"hi","profile_image_url":"p.png","offline_image_url":"","created_at":"2020-05-01T10:00:00Z"}]}"""),
            _ => new HttpResponseMessage(HttpStatusCode.NoContent),
        };
    }

    public void Dispose()
    {
        Settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
