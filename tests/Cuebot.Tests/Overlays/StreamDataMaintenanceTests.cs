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

/// <summary>Stream Data (#44): stored events behind the totals can be listed, edited and removed; test events stay out of lasting totals.</summary>
public sealed class StreamDataMaintenanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuebot-streamdata-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly OverlayFeed _feed;
    private readonly ManualClock _clock = new() { Now = new DateTimeOffset(2026, 10, 14, 18, 0, 0, TimeSpan.Zero) };   // a Wednesday

    public StreamDataMaintenanceTests()
    {
        _bus = new EventBus(_log);
        _feed = new OverlayFeed(_log);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private SessionDataStore Store(AppPaths? paths = null) => new(paths, _log, _bus, _feed, null, _clock);

    private void Alert(SessionDataStore store, string listener, string name, double amount = 1, bool test = false, string service = "twitch")
    {
        var e = new JsonObject { ["name"] = name, ["amount"] = OverlayAlerts.ToNode(amount), ["type"] = listener.Replace("-latest", "") };
        store.Record(new OverlayAlert(listener, e) { IsTest = test }, service, _clock.GetUtcNow());
        _clock.Advance(TimeSpan.FromMinutes(1));
    }

    private void Stream(FeedKind kind) =>
        _bus.Publish(new BotEvent(ChatHub.ActivityEvent, new Dictionary<string, object?> { ["platform"] = "twitch", ["kind"] = kind.ToString() }));

    private static long Count(JsonObject data, string key) => data[key]!["count"]!.GetValue<long>();
    private static double Amount(JsonObject data, string key) => OverlayAlerts.NumberOf(data[key]!["amount"]);
    private static string Name(JsonObject data, string key) => data[key]?["name"]?.GetValue<string>() ?? "";

    [Fact]
    public void Test_events_count_for_this_stream_only_and_never_become_a_top_donation()
    {
        using var store = Store();
        Alert(store, "tip-latest", "Real", 5);
        Alert(store, "tip-latest", "Tester", 500, test: true);

        var data = store.Snapshot();
        Assert.Equal("Tester", Name(data, "tip-latest"));                // overlays can still be tried out
        Assert.True(data["tip-latest"]!["isTest"]!.GetValue<bool>());
        Assert.Equal((2, 505.0), (Count(data, "tip-session"), Amount(data, "tip-session")));
        Assert.Equal((1, 5.0), (Count(data, "tip-week"), Amount(data, "tip-week")));
        Assert.Equal((1, 5.0), (Count(data, "tip-total"), Amount(data, "tip-total")));
        Assert.Equal("Real", Name(data, "tip-alltime-top-donation"));
        Assert.Equal("Real", Name(data, "tip-session-top-donation"));
        Assert.Equal("Real", Name(data, "tip-alltime-top-donator"));
    }

    [Fact]
    public void The_next_stream_clears_test_events_from_latest_and_recent_lists()
    {
        using var store = Store();
        Alert(store, "cheer-latest", "Real", 100);
        Alert(store, "cheer-latest", "Sample", 250, test: true);
        Assert.Equal(2, store.Snapshot()["cheer-recent"]!.AsArray().Count);

        store.ResetSession();

        var data = store.Snapshot();
        Assert.Equal("Real", Name(data, "cheer-latest"));
        Assert.Single(data["cheer-recent"]!.AsArray());
        Assert.Single(store.Recents());
        Assert.Empty(store.Events(new StreamEventFilter(TestOnly: true)));
        Assert.Equal(100.0, Amount(data, "cheer-total"));
    }

    [Fact]
    public void Counting_test_events_is_a_setting()
    {
        using var store = Store();
        store.SetCountTestEvents(true);
        Alert(store, "tip-latest", "Tester", 50, test: true);
        var data = store.Snapshot();
        Assert.Equal(50.0, Amount(data, "tip-total"));
        Assert.Equal("Tester", Name(data, "tip-alltime-top-donation"));
    }

    [Fact]
    public void Removing_an_event_recalculates_everything_it_counted_in()
    {
        using var store = Store();
        Alert(store, "tip-latest", "Alice", 5);
        Alert(store, "tip-latest", "Bob", 20);
        Alert(store, "tip-latest", "Cara", 999);    // say this was a mistake

        var cara = store.Events(new StreamEventFilter(Name: "Cara")).Single();
        Assert.True(store.RemoveEvent(cara.Id));

        var data = store.Snapshot();
        foreach (var period in new[] { "session", "week", "month", "total" })
            Assert.Equal((2, 25.0), (Count(data, $"tip-{period}"), Amount(data, $"tip-{period}")));
        Assert.Equal("Bob", Name(data, "tip-latest"));
        Assert.Equal(["Bob", "Alice"], data["tip-recent"]!.AsArray().Select(r => r!["name"]!.GetValue<string>()));
        Assert.DoesNotContain(store.Recents(), r => r!["name"]!.GetValue<string>() == "Cara");
        foreach (var period in SessionDataStore.Periods)
        {
            Assert.Equal(("Bob", 20.0), (Name(data, $"tip-{period}-top-donation"), Amount(data, $"tip-{period}-top-donation")));
            Assert.Equal("Bob", Name(data, $"tip-{period}-top-donator"));
        }
        Assert.DoesNotContain("Cara", data["tip-session-users"]!.AsArray().Select(u => u!["name"]!.GetValue<string>()));
        Assert.False(store.RemoveEvent(cara.Id));
    }

    [Fact]
    public void Removing_the_only_event_leaves_empty_values()
    {
        using var store = Store();
        Alert(store, "superchat-latest", "Solo", 10);
        store.RemoveEvent(store.Events().Single().Id);
        var data = store.Snapshot();
        Assert.Equal("", Name(data, "superchat-latest"));
        Assert.Equal("", Name(data, "superchat-alltime-top-donation"));
        Assert.Equal("", Name(data, "superchat-alltime-top-donator"));
        Assert.Equal(0, Count(data, "superchat-total"));
    }

    [Fact]
    public void Editing_an_event_moves_the_totals_with_it()
    {
        using var store = Store();
        Alert(store, "tip-latest", "Alice", 5);
        Alert(store, "tip-latest", "Bob", 20);
        var bob = store.Events(new StreamEventFilter(Name: "Bob")).Single();

        var edited = store.EditEvent(bob.Id, new StreamEventEdit(Name: "Robert", Amount: 2));
        Assert.NotNull(edited);

        var data = store.Snapshot();
        Assert.Equal(7.0, Amount(data, "tip-total"));
        Assert.Equal(("Robert", 2.0), (Name(data, "tip-latest"), Amount(data, "tip-latest")));
        Assert.Equal("Alice", Name(data, "tip-alltime-top-donation"));
        Assert.Equal("Robert", data["tip-recent"]![0]!["name"]!.GetValue<string>());

        // Marking it as a test takes it out of the lasting totals, but not out of this stream.
        store.EditEvent(bob.Id, new StreamEventEdit(IsTest: true));
        data = store.Snapshot();
        Assert.Equal((5.0, 7.0), (Amount(data, "tip-total"), Amount(data, "tip-session")));
        Assert.Null(store.EditEvent("nope", new StreamEventEdit(Name: "x")));
    }

    [Fact]
    public void Test_events_and_date_ranges_can_be_removed_in_bulk()
    {
        using var store = Store();
        Alert(store, "follower-latest", "Early");
        var middle = _clock.GetUtcNow();
        Alert(store, "follower-latest", "Middle");
        Alert(store, "follower-latest", "Sample", test: true);
        Alert(store, "cheer-latest", "Sample", 100, test: true);
        var late = _clock.GetUtcNow();
        Alert(store, "follower-latest", "Late");

        Assert.Equal(2, store.RemoveEvents(new StreamEventFilter(TestOnly: true)));
        Assert.Equal(1, store.RemoveEvents(new StreamEventFilter(From: middle, To: late.AddSeconds(-1))));

        var data = store.Snapshot();
        Assert.Equal(2, Count(data, "follower-session"));
        Assert.Equal(0, Count(data, "cheer-session"));
        Assert.Equal(["Late", "Early"], store.Events().Select(e => e.Name));
    }

    [Fact]
    public void Manual_reset_mode_keeps_the_session_after_going_offline()
    {
        using var store = Store();
        store.SetResetMode(SessionResetMode.Manual);
        Alert(store, "follower-latest", "Alice");
        Stream(FeedKind.StreamOnline);
        Stream(FeedKind.StreamOffline);
        _clock.Advance(TimeSpan.FromHours(2));
        store.Tick(_clock.GetUtcNow());
        Stream(FeedKind.StreamOnline);

        Assert.Equal(1, Count(store.Snapshot(), "follower-session"));
        Assert.Equal("manual", store.Info()["resetMode"]!.GetValue<string>());
        store.ResetSession();
        Assert.Equal(0, Count(store.Snapshot(), "follower-session"));
    }

    [Fact]
    public void Totals_latest_entries_and_leaderboards_can_be_overridden()
    {
        using var store = Store();
        Alert(store, "tip-latest", "Old test", 1000);   // as if counted before Cuebot kept events

        store.SetTally("tip", "total", 3, 42.5);
        store.SetTally("follower", "session", 7, null);
        store.SetLatest("tip", null);
        store.SetTopDonation("tip", "alltime", "Generous", 50);
        store.SetDonator("tip", "alltime", "Old test", 0);
        store.SetDonator("tip", "alltime", "Generous", 75);

        var data = store.Snapshot();
        Assert.Equal((3, 42.5), (Count(data, "tip-total"), Amount(data, "tip-total")));
        Assert.Equal((7, 7.0), (Count(data, "follower-session"), Amount(data, "follower-session")));
        Assert.Equal("", Name(data, "tip-latest"));
        Assert.Equal(("Generous", 50.0), (Name(data, "tip-alltime-top-donation"), Amount(data, "tip-alltime-top-donation")));
        Assert.Equal(("Generous", 75.0), (Name(data, "tip-alltime-top-donator"), Amount(data, "tip-alltime-top-donator")));

        Assert.True(store.RemoveRecent("tip", 0));
        Assert.Empty(store.Snapshot()["tip-recent"]!.AsArray());
        Assert.Empty(store.Recents());
        Assert.Throws<ArgumentException>(() => store.SetTally("tip", "decade", 1, 1));
        store.SetTopDonation("tip", "alltime", "");
        Assert.Equal("", Name(store.Snapshot(), "tip-alltime-top-donation"));
    }

    [Fact]
    public void Stored_events_and_settings_survive_a_restart_and_an_export()
    {
        var paths = new AppPaths(_dir);
        JsonObject backup;
        using (var store = Store(paths))
        {
            Alert(store, "tip-latest", "Alice", 5);
            store.SetResetMode(SessionResetMode.Manual);
            backup = store.Export();
            Alert(store, "tip-latest", "Bob", 7);
        }

        using var again = Store(paths);
        Assert.Equal(2, again.Events().Count);
        Assert.Equal(SessionResetMode.Manual, again.ResetMode);

        again.Import(backup);
        Assert.Equal(["Alice"], again.Events().Select(e => e.Name));
        Assert.Equal(5.0, Amount(again.Snapshot(), "tip-total"));
        Assert.Throws<ArgumentException>(() => again.Import(new JsonObject { ["types"] = new JsonObject() }));
    }
}
