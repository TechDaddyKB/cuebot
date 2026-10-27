using System.Net;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Emotes;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Tests.ChatExtras;

public sealed class EmoteServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly FakeHttp _http = new();
    private readonly ManualClock _clock = new();
    private readonly EmoteSettings _settings = new() { Channels = { ["Twitch"] = "123" } };
    private readonly List<EmoteService> _services = [];

    public EmoteServiceTests()
    {
        _bus = new EventBus(_log);
        ProviderJson.AllProviders(_http);
    }

    private EmoteService Create(EventBus? bus = null)
    {
        var client = _http.Client();
        var service = new EmoteService(bus ?? _bus, _log, new EmoteDiskCache(_dir.Path, _log), () => _settings,
            [new SevenTvEmoteProvider(client), new BttvEmoteProvider(client), new FfzEmoteProvider(client)], null, _clock);
        _services.Add(service);
        return service;
    }

    [Fact]
    public async Task Refresh_loads_global_and_channel_sets_with_channel_overriding_global()
    {
        var emotes = Create();
        var count = await emotes.RefreshAsync();

        Assert.Equal(2 + 2 + 2 + 3 + 1 + 2, count);
        var twitch = emotes.GetThirdPartyEmotes("Twitch");
        Assert.Equal("7TV", twitch["KEKW"].Provider);       // 7TV channel beats BTTV shared on the same code
        Assert.Equal("7TV", twitch["catJAM"].Provider);
        Assert.Equal("BTTV", twitch["ourEmote"].Provider);
        Assert.Equal("FFZ", twitch["ffzChannel"].Provider);
        Assert.True(twitch.ContainsKey("CatBag"));
        Assert.False(twitch.ContainsKey("Hidden"));

        // YouTube has no channel configured: only 7TV + BTTV globals.
        var youtube = emotes.GetThirdPartyEmotes("YouTube");
        Assert.True(youtube.ContainsKey("peepoHappy"));
        Assert.False(youtube.ContainsKey("CatBag"));
        Assert.False(youtube.ContainsKey("KEKW"));
    }

    [Fact]
    public async Task Disabled_providers_are_not_fetched_or_used()
    {
        _settings.Ffz = false;
        var emotes = Create();
        await emotes.RefreshAsync();

        Assert.DoesNotContain(_http.Requests, r => r.Contains("frankerfacez", StringComparison.Ordinal));
        Assert.False(emotes.GetThirdPartyEmotes("Twitch").ContainsKey("CatBag"));
    }

    [Fact]
    public async Task Sets_are_cached_until_they_expire_and_force_bypasses_the_cache()
    {
        _settings.CacheHours = 6;
        var emotes = Create();
        const string global = "https://7tv.io/v3/emote-sets/global";

        await emotes.RefreshAsync();
        await emotes.RefreshAsync();
        Assert.Equal(1, _http.Count(global));

        _clock.Advance(TimeSpan.FromHours(5));
        await emotes.RefreshAsync();
        Assert.Equal(1, _http.Count(global));

        _clock.Advance(TimeSpan.FromHours(2));
        await emotes.RefreshAsync();
        Assert.Equal(2, _http.Count(global));

        await emotes.RefreshAsync(force: true);
        Assert.Equal(3, _http.Count(global));
    }

    [Fact]
    public async Task A_new_instance_uses_the_fresh_disk_cache_and_a_stale_one_when_offline()
    {
        await Create().RefreshAsync();
        var fetched = _http.Requests.Count;
        Assert.NotEmpty(Directory.GetFiles(_dir.Path, "*.json"));

        var second = Create();
        await second.RefreshAsync();
        Assert.Equal(fetched, _http.Requests.Count);   // all from disk
        Assert.True(second.GetThirdPartyEmotes("Twitch").ContainsKey("KEKW"));

        // Expired cache and every provider failing: the stale copy is still used.
        _clock.Advance(TimeSpan.FromDays(2));
        foreach (var url in _http.Routes.Keys.ToArray()) _http.Json(url, "down", HttpStatusCode.ServiceUnavailable);
        var third = Create();
        await third.RefreshAsync();
        Assert.True(third.GetThirdPartyEmotes("Twitch").ContainsKey("KEKW"));
        Assert.True(_http.Requests.Count > fetched);
    }

    [Fact]
    public async Task Changes_after_the_first_load_publish_added_and_removed_events()
    {
        var events = new List<BotEvent>();
        _bus.Subscribe(e => { if (e.Type.StartsWith("chat.emote", StringComparison.Ordinal)) lock (events) events.Add(e); });
        var changes = new List<EmoteSetChange>();
        var emotes = Create();
        emotes.EmotesChanged += c => { lock (changes) changes.Add(c); };

        await emotes.RefreshAsync();
        Assert.Empty(events);                                  // initial loads are not news
        Assert.Equal(6, changes.Count);
        Assert.All(changes, c => Assert.True(c.IsInitialLoad));

        _http.Json("https://7tv.io/v3/users/twitch/123", ProviderJson.SevenTvChannel.Replace("\"catJAM\"", "\"catDance\""));
        await emotes.RefreshAsync(force: true);

        var set = Assert.Single(events, e => e.Type == EmoteService.SetsChangedEvent);
        Assert.Equal("7TV", set.Args["provider"]);
        Assert.Equal("catDance", set.Args["added"]);
        Assert.Equal("catJAM", set.Args["removed"]);
        Assert.Equal("catDance", Assert.Single(events, e => e.Type == EmoteService.EmoteAddedEvent).Args["emote"]);
        Assert.Equal("catJAM", Assert.Single(events, e => e.Type == EmoteService.EmoteRemovedEvent).Args["emote"]);
        Assert.Equal("7TV", emotes.GetThirdPartyEmotes("Twitch")["catDance"].Provider);
        Assert.Equal("BTTV", emotes.GetThirdPartyEmotes("Twitch")["catJAM"].Provider);   // BTTV still has one
    }

    [Fact]
    public async Task Find_combines_native_ranges_and_third_party_words()
    {
        var emotes = Create();
        await emotes.RefreshAsync();
        var args = new Dictionary<string, object?> { ["emotes"] = "25:0-4" };

        var found = emotes.FindEmotes("Twitch", "Kappa KEKW and CatBag ffzHat", args);

        Assert.Equal(["Twitch", "7TV", "FFZ", "FFZ"], found.Select(f => f.Provider));
        Assert.Equal(["Kappa", "KEKW", "CatBag", "ffzHat"], found.Select(f => f.Code));
        Assert.True(found[3].Emote.IsZeroWidth);
        // Native Twitch emotes seen in chat join autocomplete.
        Assert.Contains(emotes.Search("Twitch", "kap"), e => e.Code == "Kappa");
    }

    [Fact]
    public async Task Chat_messages_are_matched_with_their_platform_event_arguments()
    {
        var emotes = Create();
        using var hub = new ChatHub(_bus, _log, [new TwitchLikeAdapter()], []);
        await emotes.RefreshAsync();

        _bus.Publish(new BotEvent("twitch.chatMessage", new Dictionary<string, object?>
        {
            ["messageId"] = "m1", ["user"] = "Ann", ["userName"] = "ann", ["message"] = "LUL KEKW", ["emotes"] = "425618:0-2",
        }));

        var message = Assert.Single(hub.RecentMessages());
        var found = emotes.FindEmotes(message);
        Assert.Equal(["LUL", "KEKW"], found.Select(f => f.Code));
        Assert.Equal("ann", emotes.ArgsFor("m1")!["userName"]);
    }

    [Fact]
    public async Task Channel_ids_are_discovered_from_event_arguments()
    {
        _settings.Channels.Clear();
        var emotes = Create();
        await emotes.RefreshAsync();
        Assert.False(emotes.GetThirdPartyEmotes("Twitch").ContainsKey("KEKW"));

        _bus.Publish(new BotEvent("twitch.streamOnline", new Dictionary<string, object?> { ["broadcasterUserId"] = "123" }));
        Assert.Equal(new EmoteChannel("Twitch", "123"), emotes.ChannelFor("twitch"));
        await emotes.RefreshAsync();
        Assert.True(emotes.GetThirdPartyEmotes("Twitch").ContainsKey("KEKW"));
    }

    [Fact]
    public async Task Stream_start_activity_forces_a_refresh()
    {
        var emotes = Create();
        await emotes.RefreshAsync();
        var before = _http.Count("https://7tv.io/v3/emote-sets/global");

        _bus.Publish(new BotEvent(ChatHub.ActivityEvent, new Dictionary<string, object?> { ["kind"] = "StreamOnline", ["platform"] = "Twitch" }));

        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (_http.Count("https://7tv.io/v3/emote-sets/global") == before && DateTime.UtcNow < deadline) await Task.Delay(10);
        Assert.Equal(before + 1, _http.Count("https://7tv.io/v3/emote-sets/global"));
    }

    [Fact]
    public async Task Search_prefers_prefix_matches_then_contains()
    {
        var emotes = Create();
        await emotes.RefreshAsync();

        var results = emotes.Search("Twitch", "cat");

        Assert.Equal(["CatBag", "catJAM"], results.Select(e => e.Code));
        Assert.Equal(["peepoHappy"], emotes.Search("Twitch", "happy").Select(e => e.Code));   // contains, not prefix
        Assert.Contains(emotes.Search(ChatHub.AllPlatforms, "jam"), e => e.Code == "catJAM");
    }

    [Fact]
    public async Task Refresh_sub_action_and_script_functions()
    {
        var emotes = Create();
        using var engine = new ActionEngine(_bus, new ProfileService(null, _log), new Registry([], []), new GlobalVariables(null, _log), _log);
        var context = new ActionRunContext(new ActionDefinition(), new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
            { ["platform"] = "Twitch", ["message"] = "hi KEKW" }, engine, new GlobalVariables(null, _log), _log, 0);

        var handler = new RefreshEmotesSubAction(emotes);
        await handler.ExecuteAsync(context, ParameterValues.From(handler.Parameters, new Dictionary<string, string>()), default);
        Assert.Equal(12, context.Args["emoteCount"]);

        var api = new Cuebot.Core.Scripting.ScriptApi([new EmoteScriptFunctions(emotes)]);
        var found = (Dictionary<string, object?>[])(await api.CallAsync(context, "emotes.find", new { }, default))!;
        Assert.Equal("KEKW", Assert.Single(found)["code"]);
        var names = (string[])(await api.CallAsync(context, "emotes.list", new { platform = "Twitch" }, default))!;
        Assert.Contains("CatBag", names);
    }

    [Fact]
    public void Emote_triggers_filter_by_provider()
    {
        var trigger = new EmoteTriggers().GetTriggers().Single(t => t.TypeId == "chat.emoteAdded");
        var e = new BotEvent(EmoteService.EmoteAddedEvent, new Dictionary<string, object?> { ["provider"] = "BTTV" });
        bool Matches(string provider) => trigger.Matches(e, new TriggerDefinition(),
            ParameterValues.From(trigger.Parameters, new Dictionary<string, string> { ["provider"] = provider }), new Dictionary<string, object?>());

        Assert.True(Matches("Any"));
        Assert.True(Matches("BTTV"));
        Assert.False(Matches("7TV"));
    }

    private sealed class TwitchLikeAdapter : IChatEventAdapter
    {
        public string Platform => "Twitch";

        public ChatMessage? ToChatMessage(BotEvent e) => e.Type == "twitch.chatMessage"
            ? new ChatMessage("Twitch", (string)e.Args["messageId"]!, (string)e.Args["user"]!, "1", (string)e.Args["message"]!, e.Time)
            : null;

        public FeedItem? ToFeedItem(BotEvent e) => null;
    }

    public void Dispose()
    {
        foreach (var s in _services) s.Dispose();
        _log.Dispose();
        _dir.Dispose();
    }
}
