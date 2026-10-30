using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Events;
using Cuebot.Core.Overlays.Session;
using Cuebot.Tests.ChatExtras;

namespace Cuebot.Tests.Overlays;

public sealed class SessionDataStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuebot-session-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly OverlayFeed _feed;
    private readonly ManualClock _clock = new() { Now = new DateTimeOffset(2026, 10, 14, 18, 0, 0, TimeSpan.Zero) };   // a Wednesday
    private readonly List<JsonObject> _published = [];

    public SessionDataStoreTests()
    {
        _bus = new EventBus(_log);
        _feed = new OverlayFeed(_log);
        _feed.Subscribe(m => _published.Add(JsonNode.Parse(m.Json)!.AsObject()));
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private SessionDataStore Store(AppPaths? paths = null) => new(paths, _log, _bus, _feed, null, _clock);

    private void Alert(SessionDataStore store, string listener, string name, double amount = 1, int count = 1, string service = "twitch",
        Action<JsonObject>? edit = null)
    {
        var e = new JsonObject { ["name"] = name, ["amount"] = OverlayAlerts.ToNode(amount), ["type"] = listener.Replace("-latest", "") };
        edit?.Invoke(e);
        store.Record(new OverlayAlert(listener, e, count), service, _clock.GetUtcNow());
    }

    private void Stream(string platform, FeedKind kind) =>
        _bus.Publish(new BotEvent(ChatHub.ActivityEvent, new Dictionary<string, object?> { ["platform"] = platform, ["kind"] = kind.ToString() }));

    private static long Count(JsonObject data, string key) => data[key]!["count"]!.GetValue<long>();
    private static double Amount(JsonObject data, string key) => OverlayAlerts.NumberOf(data[key]!["amount"]);

    [Fact]
    public void Follows_from_every_platform_add_up()
    {
        using var store = Store();
        Alert(store, "follower-latest", "Alice", service: "twitch");
        Alert(store, "follower-latest", "Bob", service: "kick");
        Alert(store, "follower-latest", "Cara", service: "rumble");

        var data = store.Snapshot();
        foreach (var period in new[] { "session", "week", "month", "total" }) Assert.Equal(3, Count(data, $"follower-{period}"));
        Assert.Equal(3, Count(data, "follower-count"));
        Assert.Equal(("Cara", "rumble"), (data["follower-latest"]!["name"]!.GetValue<string>(), data["follower-latest"]!["service"]!.GetValue<string>()));
        Assert.Equal(["Cara", "Bob", "Alice"], data["follower-recent"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()));
        Assert.Equal(3, store.Recents().Count);
        Assert.Equal(0, data["follower-goal"]!["amount"]!.GetValue<int>());
        Assert.Null(data["follower-session-top-donation"]);   // leaderboards are for amounts only
    }

    [Fact]
    public void Amounts_sum_and_build_leaderboards()
    {
        using var store = Store();
        Alert(store, "tip-latest", "Alice", 5);
        Alert(store, "tip-latest", "Bob", 20);
        Alert(store, "tip-latest", "Alice", 18);

        var data = store.Snapshot();
        Assert.Equal((3, 43.0), (Count(data, "tip-session"), Amount(data, "tip-session")));
        Assert.Equal(("Bob", 20.0), (data["tip-session-top-donation"]!["name"]!.GetValue<string>(), OverlayAlerts.NumberOf(data["tip-session-top-donation"]!["amount"])));
        Assert.Equal(("Alice", 23.0), (data["tip-alltime-top-donator"]!["name"]!.GetValue<string>(), OverlayAlerts.NumberOf(data["tip-alltime-top-donator"]!["amount"])));
        Assert.Equal("", data["cheer-session-top-donation"]?["name"]?.GetValue<string>() ?? "");
    }

    [Fact]
    public void Gift_bundles_are_not_counted_twice()
    {
        using var store = Store();
        Alert(store, "subscriber-latest", "Gifter", 2, count: 0, edit: e => e["bulkGifted"] = true);
        Alert(store, "subscriber-latest", "Lucky1", edit: e => e["gifted"] = true);
        Alert(store, "subscriber-latest", "Lucky2", edit: e => e["gifted"] = true);
        Alert(store, "subscriber-latest", "Rumbler", 3, count: 3, service: "rumble", edit: e => e["bulkGifted"] = true);
        Alert(store, "subscriber-latest", "Newbie");
        Alert(store, "subscriber-latest", "Veteran", 14);

        var data = store.Snapshot();
        Assert.Equal(7, Count(data, "subscriber-session"));
        Assert.Equal(7.0, Amount(data, "subscriber-session"));   // not an amount type: the amount is the count
        Assert.Equal((1, 1, 5), (Count(data, "subscriber-new-session"), Count(data, "subscriber-resub-session"), Count(data, "subscriber-gifted-session")));
        Assert.Equal("Veteran", data["subscriber-latest"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void The_session_resets_fifteen_minutes_after_the_last_platform_goes_offline()
    {
        using var store = Store();
        Stream("Twitch", FeedKind.StreamOnline);
        Stream("Kick", FeedKind.StreamOnline);
        Alert(store, "follower-latest", "Alice");

        Stream("Twitch", FeedKind.StreamOffline);
        _clock.Advance(TimeSpan.FromMinutes(30));
        store.Tick(_clock.GetUtcNow());
        Assert.Equal(1, Count(store.Snapshot(), "follower-session"));   // Kick is still live

        Stream("Kick", FeedKind.StreamOffline);
        _clock.Advance(TimeSpan.FromMinutes(14));
        store.Tick(_clock.GetUtcNow());
        Assert.Equal(1, Count(store.Snapshot(), "follower-session"));

        _clock.Advance(TimeSpan.FromMinutes(1));
        store.Tick(_clock.GetUtcNow());
        var data = store.Snapshot();
        Assert.Equal(0, Count(data, "follower-session"));
        Assert.Equal(1, Count(data, "follower-total"));
        Assert.Equal("Alice", data["follower-latest"]!["name"]!.GetValue<string>());   // latest is kept
    }

    [Fact]
    public void Coming_back_online_quickly_keeps_the_session()
    {
        using var store = Store();
        Stream("Twitch", FeedKind.StreamOnline);
        Alert(store, "tip-latest", "Alice", 5);
        Stream("Twitch", FeedKind.StreamOffline);
        _clock.Advance(TimeSpan.FromMinutes(5));
        Stream("Twitch", FeedKind.StreamOnline);   // a dropped connection, not a new stream
        _clock.Advance(TimeSpan.FromHours(1));
        store.Tick(_clock.GetUtcNow());

        Assert.Equal(5.0, Amount(store.Snapshot(), "tip-session"));
    }

    [Fact]
    public void Going_live_after_the_delay_without_a_tick_resets_too()
    {
        using var store = Store();
        Stream("Twitch", FeedKind.StreamOnline);
        Alert(store, "tip-latest", "Alice", 5);
        Stream("Twitch", FeedKind.StreamOffline);
        _clock.Advance(TimeSpan.FromMinutes(20));
        Stream("Twitch", FeedKind.StreamOnline);

        var data = store.Snapshot();
        Assert.Equal(0.0, Amount(data, "tip-session"));
        Assert.Equal("", data["tip-session-top-donation"]!["name"]!.GetValue<string>());
        Assert.Equal("Alice", data["tip-alltime-top-donation"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Weeks_and_months_roll_over_by_the_calendar()
    {
        using var store = Store();
        Alert(store, "cheer-latest", "Alice", 100);
        _clock.Advance(TimeSpan.FromDays(7));   // next week, same month
        var data = store.Snapshot();
        Assert.Equal((0.0, 100.0, 100.0), (Amount(data, "cheer-week"), Amount(data, "cheer-month"), Amount(data, "cheer-total")));
        Assert.Equal("", data["cheer-weekly-top-donator"]!["name"]!.GetValue<string>());

        _clock.Advance(TimeSpan.FromDays(21));   // November
        data = store.Snapshot();
        Assert.Equal((0.0, 100.0), (Amount(data, "cheer-month"), Amount(data, "cheer-total")));
        Assert.Equal("Alice", data["cheer-alltime-top-donator"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public void Totals_and_goals_survive_a_restart()
    {
        var paths = new AppPaths(_dir);
        using (var store = Store(paths))
        {
            Alert(store, "subscriber-latest", "Alice");
            store.SetGoal("subscriber", 50);
        }

        using var again = Store(paths);
        var data = again.Snapshot();
        Assert.Equal(1, Count(data, "subscriber-total"));
        Assert.Equal(50, data["subscriber-goal"]!["amount"]!.GetValue<int>());
        Assert.Single(again.Recents());
    }

    [Fact]
    public void Changes_are_sent_to_overlay_pages()
    {
        using var store = Store();
        Alert(store, "raid-latest", "Friend", 30);
        store.ResetSession();

        Assert.Equal(2, _published.Count);
        Assert.All(_published, m => Assert.Equal("session", m["type"]!.GetValue<string>()));
        Assert.Equal(1, _published[0]["session"]!["raid-session"]!["count"]!.GetValue<long>());
        Assert.Equal(0, _published[1]["session"]!["raid-session"]!["count"]!.GetValue<long>());
        Assert.Equal(1.0, Amount(store.Snapshot(), "raid-total"));   // a raid's amount is its count, not its viewers
    }

    [Fact]
    public void Only_latest_listeners_are_counted()
    {
        using var store = Store();
        store.Record(new OverlayAlert("message", new JsonObject()), "twitch", _clock.GetUtcNow());
        Assert.Empty(store.Snapshot());
        Assert.Empty(_published);
    }
}
