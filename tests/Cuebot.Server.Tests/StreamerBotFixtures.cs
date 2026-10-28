using System.Net;
using System.Text;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat.Emotes;
using Cuebot.Core.Chat.Pronouns;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;

namespace Cuebot.Server.Tests;

/// <summary>
/// Stand-ins for what the platform modules give the Streamer.bot server: account and broadcast script functions, an
/// alejo.io pronoun API and 7TV/BTTV/FFZ emote sets. All values are made up.
/// </summary>
internal static class StreamerBotFixtures
{
    public static TestBotOptions Options(params ITriggerType[] triggers) => new()
    {
        Functions = [new FakePlatformFunctions()],
        Triggers = triggers,
        Pronouns = new PronounService(new HttpClient(new FakePronounApi()), new AppLog()),
        Emotes = (bus, log) =>
        {
            var directory = Path.Combine(Path.GetTempPath(), "cuebot-server-tests", Guid.NewGuid().ToString("N"));
            var service = new EmoteService(bus, log, new EmoteDiskCache(directory, log), () => new EmoteSettings(),
                [
                    new FakeEmoteProvider(EmoteProviders.SevenTv, new Emote("catJAM", "7tv1", EmoteProviders.SevenTv, "https://cdn.7tv.app/emote/7tv1/1x.webp") { IsZeroWidth = true }),
                    new FakeEmoteProvider(EmoteProviders.Bttv, new Emote("monkaS", "bttv1", EmoteProviders.Bttv, "https://cdn.betterttv.net/emote/bttv1/1x")),
                    new FakeEmoteProvider(EmoteProviders.Ffz, new Emote("OMEGALUL", "ffz1", EmoteProviders.Ffz, "https://cdn.frankerfacez.com/emote/ffz1/1")),
                ]);
            service.RefreshAsync().GetAwaiter().GetResult();
            return service;
        },
    };

    /// <summary>Trigger types whose events GetEvents should list (the official client only subscribes to listed sources).</summary>
    public static ITriggerType[] Triggers(params string[] eventTypes) =>
        [.. eventTypes.Select(t => (ITriggerType)new EventTriggerType("test." + t, "Test", t, t))];
}

internal sealed class FakePlatformFunctions : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        Fn("twitch.getBroadcaster", new { userId = "1001", userName = "streamer", connected = true }),
        Fn("twitch.getBot", new { userId = "1002", userName = "HelperBot", connected = true }),
        Fn("twitch.getUser", new { Id = "1001", Login = "streamer", DisplayName = "Streamer", BroadcasterType = "affiliate", ProfileImageUrl = "https://example.com/streamer.png" }),
        Fn("youtube.getBroadcaster", new { userName = "Streamer Channel", userId = "UC1001", profileImageUrl = "https://example.com/yt.png", isSignedIn = true }),
        Fn("youtube.getBot", new { userName = "", userId = "", profileImageUrl = "", isSignedIn = false }),
        Fn("youtube.getMonitoredBroadcasts", new object[]
        {
            new
            {
                id = "vid1", title = "Live now", description = "desc", privacy = "public", status = "live", liveChatId = "chat1",
                url = "https://www.youtube.com/watch?v=vid1", actualStart = "2026-10-28T10:00:00.0000000+00:00", actualEnd = (string?)null,
                scheduledStart = "2026-10-28T09:55:00.0000000+00:00", channelId = "UC1001",
            },
        }),
        Fn("kick.getBroadcaster", new Dictionary<string, object?>
        {
            ["broadcastUser"] = "KickStreamer", ["broadcastUserName"] = "KickStreamer", ["broadcastUserId"] = "5001",
            ["broadcasterChannelSlug"] = "kickstreamer", ["botUser"] = "", ["botUserId"] = "",
        }),
        Fn("kick.getUserInfo", new Dictionary<string, object?> { ["User"] = "KickStreamer", ["UserId"] = "5001", ["ProfilePicture"] = "https://example.com/kick.png" }),
    ];

    private static ScriptFunction Fn(string name, object? result) => new(name, "fake", [], (_, _, _) => Task.FromResult(result));
}

internal sealed class FakeEmoteProvider(string name, params Emote[] emotes) : IEmoteProvider
{
    public string Name => name;
    public bool SupportsPlatform(string platform) => true;
    public Task<IReadOnlyList<Emote>> FetchGlobalAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<Emote>>(emotes);
    public Task<IReadOnlyList<Emote>> FetchChannelAsync(EmoteChannel channel, CancellationToken ct) => Task.FromResult<IReadOnlyList<Emote>>([]);
}

/// <summary>alejo.io's /pronouns catalogue and /users/{login}: "ann" is She/Her, "theo" They/Them, everyone else unknown.</summary>
internal sealed class FakePronounApi : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var path = request.RequestUri!.AbsolutePath;
        string? json = path.EndsWith("/pronouns", StringComparison.Ordinal)
            ? """{"sheher":{"name":"sheher","subject":"She","object":"Her","singular":false},"theythem":{"name":"theythem","subject":"They","object":"Them","singular":false}}"""
            : path.EndsWith("/users/ann", StringComparison.Ordinal) ? """{"channel_login":"ann","pronoun_id":"sheher","alt_pronoun_id":null}"""
            : path.EndsWith("/users/theo", StringComparison.Ordinal) ? """{"channel_login":"theo","pronoun_id":"theythem","alt_pronoun_id":null}"""
            : null;
        return Task.FromResult(json is null
            ? new HttpResponseMessage(HttpStatusCode.NotFound)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
