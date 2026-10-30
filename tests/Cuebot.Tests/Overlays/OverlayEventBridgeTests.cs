using System.Text.Json.Nodes;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Emotes;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Events;

namespace Cuebot.Tests.Overlays;

public sealed class OverlayEventBridgeTests : IDisposable
{
    private readonly string _cache = Path.Combine(Path.GetTempPath(), "cuebot-overlay-emotes-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly OverlayFeed _feed;
    private readonly List<JsonObject> _sent = [];
    private readonly FakeAdapter _adapter = new();

    public OverlayEventBridgeTests()
    {
        _bus = new EventBus(_log);
        _feed = new OverlayFeed(_log);
        _feed.Subscribe(m => _sent.Add(JsonNode.Parse(m.Json)!.AsObject()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_cache)) Directory.Delete(_cache, recursive: true);
    }

    private OverlayEventBridge Bridge(bool emotes = true) => new(_bus, _feed, [_adapter], _log,
        emotes ? new EmoteService(_bus, _log, new EmoteDiskCache(_cache, _log), () => new EmoteSettings(), []) : null);

    private static BotEvent Chat(string platform, string message, Dictionary<string, object?> platformArgs, Action<Dictionary<string, object?>>? edit = null)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["platform"] = platform, ["user"] = "Kris", ["userId"] = "42", ["message"] = message, ["messageId"] = "m1",
            ["isBroadcaster"] = false, ["isModerator"] = true, ["isVip"] = false, ["isSubscriber"] = true,
            [ChatHub.PlatformArgsKey] = platformArgs,
        };
        edit?.Invoke(args);
        return new BotEvent(ChatHub.MessageEvent, args);
    }

    [Fact]
    public void Twitch_chat_becomes_a_streamelements_message_with_emotes_and_badges()
    {
        using var bridge = Bridge();
        _bus.Publish(Chat("Twitch", "hi Kappa <b>", new Dictionary<string, object?>
        {
            ["message"] = "hi Kappa <b>", ["userName"] = "KrisLogin", ["color"] = "#00FF00", ["emotes"] = "25:3-7",
            ["badges"] = "moderator/1,subscriber/12",
        }));

        var message = Assert.Single(_sent);
        Assert.Equal("event", message["type"]!.GetValue<string>());
        Assert.Equal("message", message["listener"]!.GetValue<string>());
        var e = message["event"]!;
        var data = e["data"]!;
        Assert.Equal("fake", e["service"]!.GetValue<string>());   // the adapter's service name
        Assert.Equal("krislogin", data["nick"]!.GetValue<string>());
        Assert.Equal("Kris", data["displayName"]!.GetValue<string>());
        Assert.Equal("#00FF00", data["displayColor"]!.GetValue<string>());
        Assert.Equal("m1", data["msgId"]!.GetValue<string>());
        Assert.Equal("1", data["tags"]!["mod"]!.GetValue<string>());
        Assert.Equal("1", data["tags"]!["subscriber"]!.GetValue<string>());
        Assert.Equal("25:3-7", data["tags"]!["emotes"]!.GetValue<string>());
        Assert.Equal("moderator/1,subscriber/12", data["tags"]!["badges"]!.GetValue<string>());

        var emote = Assert.Single(data["emotes"]!.AsArray())!;
        Assert.Equal(("Kappa", 3, 7), (emote["name"]!.GetValue<string>(), emote["start"]!.GetValue<int>(), emote["end"]!.GetValue<int>()));
        Assert.Equal("twitch", emote["type"]!.GetValue<string>());
        Assert.Contains("/25/", emote["urls"]!["1"]!.GetValue<string>());

        Assert.Equal(2, data["badges"]!.AsArray().Count);
        Assert.Equal("https://badges.test/subscriber/12.png", data["badges"]![1]!["url"]!.GetValue<string>());

        var html = e["renderedText"]!.GetValue<string>();
        Assert.StartsWith("hi <img src=\"", html);
        Assert.EndsWith("class=\"emote\"> &lt;b&gt;", html);
    }

    [Fact]
    public void Kick_emote_tokens_become_names_in_text_ranges_and_html()
    {
        using var bridge = Bridge();
        _bus.Publish(Chat("Kick", "gg KEKW nice", new Dictionary<string, object?>
        {
            ["message"] = "gg KEKW nice", ["rawMessage"] = "gg [emote:37226:KEKW] nice", ["userName"] = "kris",
        }));

        var data = _sent.Single()["event"]!["data"]!;
        Assert.Equal("gg KEKW nice", data["text"]!.GetValue<string>());
        var emote = Assert.Single(data["emotes"]!.AsArray())!;
        Assert.Equal(("KEKW", "37226", 3, 6), (emote["name"]!.GetValue<string>(), emote["id"]!.GetValue<string>(),
            emote["start"]!.GetValue<int>(), emote["end"]!.GetValue<int>()));
        Assert.DoesNotContain("[emote:", _sent.Single()["event"]!["renderedText"]!.GetValue<string>());
        Assert.Equal("", data["tags"]!["emotes"]!.GetValue<string>());   // Twitch-only field
    }

    [Fact]
    public void A_missing_colour_gets_a_stable_default()
    {
        using var bridge = Bridge(emotes: false);
        _bus.Publish(Chat("Rumble", "hello", new Dictionary<string, object?> { ["color"] = "" }));
        _bus.Publish(Chat("Rumble", "again", new Dictionary<string, object?> { ["color"] = "not-a-colour" }));

        var first = _sent[0]["event"]!["data"]!["displayColor"]!.GetValue<string>();
        Assert.Matches("^#[0-9A-F]{6}$", first);
        Assert.Equal(first, _sent[1]["event"]!["data"]!["displayColor"]!.GetValue<string>());
        Assert.Equal("hello", _sent[0]["event"]!["renderedText"]!.GetValue<string>());
    }

    [Fact]
    public void Deletions_and_bans_clear_messages()
    {
        using var bridge = Bridge(emotes: false);
        _bus.Publish(new BotEvent(OverlayEventBridge.MessageDeleted, new Dictionary<string, object?> { ["messageId"] = "m9" }));
        _bus.Publish(new BotEvent(OverlayEventBridge.UserBanned, new Dictionary<string, object?> { ["userId"] = "u1" }));
        _bus.Publish(new BotEvent(OverlayEventBridge.UserTimedOut, new Dictionary<string, object?> { ["userId"] = "u2" }));
        _bus.Publish(new BotEvent(OverlayEventBridge.MessageDeleted, new Dictionary<string, object?>()));   // no id: nothing to delete

        Assert.Equal(3, _sent.Count);
        Assert.Equal(("delete-message", "m9"), (_sent[0]["listener"]!.GetValue<string>(), _sent[0]["event"]!["msgId"]!.GetValue<string>()));
        Assert.Equal(("delete-messages", "u1"), (_sent[1]["listener"]!.GetValue<string>(), _sent[1]["event"]!["userId"]!.GetValue<string>()));
        Assert.Equal("u2", _sent[2]["event"]!["userId"]!.GetValue<string>());
    }

    [Fact]
    public void Alerts_are_followed_by_the_raw_event_listener()
    {
        using var bridge = Bridge(emotes: false);
        _bus.Publish(new BotEvent("fake.follow", new Dictionary<string, object?> { ["user"] = "Kris", ["userId"] = "42" }));
        _bus.Publish(new BotEvent("fake.unknown", new Dictionary<string, object?>()));

        Assert.Equal(2, _sent.Count);
        var alert = _sent[0];
        Assert.Equal("follower-latest", alert["listener"]!.GetValue<string>());
        Assert.Equal(("Kris", 1, "follower", "42"), (alert["event"]!["name"]!.GetValue<string>(), alert["event"]!["amount"]!.GetValue<int>(),
            alert["event"]!["type"]!.GetValue<string>(), alert["event"]!["providerId"]!.GetValue<string>()));
        var raw = _sent[1];
        Assert.Equal("event", raw["listener"]!.GetValue<string>());
        Assert.Equal(("follower", "fake"), (raw["event"]!["type"]!.GetValue<string>(), raw["event"]!["provider"]!.GetValue<string>()));
        Assert.Equal("Kris", raw["event"]!["data"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Convert_returns_the_messages_without_sending_them()
    {
        using var bridge = Bridge(emotes: false);
        var messages = bridge.Convert(new BotEvent("fake.follow", new Dictionary<string, object?> { ["user"] = "Kris" }));
        Assert.Equal(["follower-latest", "event"], messages.Select(m => m.Listener));
        Assert.Empty(_sent);
    }

    [Fact]
    public void A_failing_adapter_is_logged_not_thrown()
    {
        using var bridge = Bridge(emotes: false);
        _adapter.Throw = true;
        _bus.Publish(new BotEvent("fake.follow", new Dictionary<string, object?>()));
        Assert.Empty(_sent);
        Assert.Contains(_log.Recent(), e => e.Message.Contains("fake.follow", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("1", "1000")]
    [InlineData("1000", "1000")]
    [InlineData("2", "2000")]
    [InlineData("3000", "3000")]
    [InlineData("Prime", "prime")]
    [InlineData("", "1000")]
    public void Tiers_use_streamelements_values(string tier, string expected) => Assert.Equal(expected, OverlayAlerts.Tier(tier));

    [Fact]
    public void Whole_amounts_stay_integers()
    {
        Assert.Equal("5", OverlayAlerts.ToNode(5.0)!.ToJsonString());
        Assert.Equal("5.5", OverlayAlerts.ToNode(5.5)!.ToJsonString());
    }

    [Fact]
    public void Overlapping_emote_matches_keep_the_first()
    {
        var a = new EmoteMatch(0, 4, "KEKW", new Emote("KEKW", "1", "7TV", "a"));
        var b = new EmoteMatch(2, 4, "KWxx", new Emote("KWxx", "2", "7TV", "b"));
        var (text, ranges) = OverlayChatMessage.Render("KEKWxx", [b, a]);
        Assert.Equal("KEKWxx", text);
        Assert.Equal((0, 3), (Assert.Single(ranges).Start, ranges[0].End));
    }

    private sealed class FakeAdapter : IOverlayEventAdapter
    {
        public bool Throw { get; set; }
        public string Platform => "Twitch";
        public string Service => "fake";

        public IReadOnlyList<OverlayBadge> Badges(IReadOnlyDictionary<string, object?> platformArgs) =>
            OverlayAlerts.Str(platformArgs, "badges").Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(b => new OverlayBadge(b.Split('/')[0], b.Split('/')[1], $"https://badges.test/{b}.png", b)).ToArray();

        public OverlayAlert? ToAlert(BotEvent e)
        {
            if (Throw) throw new InvalidOperationException("broken adapter");
            return e.Type == "fake.follow" ? new OverlayAlert(OverlayAlerts.Follower, OverlayAlerts.Event(e, "follower")) : null;
        }
    }
}
