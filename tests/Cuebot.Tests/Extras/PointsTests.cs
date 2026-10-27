using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Points;
using Cuebot.Core.Users;
using Cuebot.Core.Variables;
using Cuebot.Tests.Commands;

namespace Cuebot.Tests.Extras;

public sealed class PointsTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly TestClock _clock = new();
    private readonly UserVariables _vars;
    private readonly UserRegistry _users;
    private readonly PointsService _points;
    private readonly EventRecorder _thresholds;

    public PointsTests()
    {
        _bus = new EventBus(_log);
        _vars = new UserVariables(null, _log, _clock);
        _users = new UserRegistry(null, _bus, _log, _clock);
        _points = new PointsService(_vars, null, _bus, _log, _users, [new FakeAdapter()], _clock);
        _thresholds = new EventRecorder(_bus, PointsEvents.ThresholdCrossed);
    }

    private void GoLive(string platform = "Twitch") =>
        _bus.Publish(CreditsTests.Activity(FeedKind.StreamOnline, "Streamer", "Stream went live", platform));

    [Fact]
    public void Defaults_name_points_without_a_dollar_symbol()
    {
        var s = new PointsSettings();
        Assert.Equal(("points", "", "points"), (s.Name, s.Symbol, s.VariableName));
        Assert.Equal("1,250 points", _points.Format(1250));
        Assert.Equal("1 point", _points.Format(1));
        _points.UpdateSettings(x => x.Symbol = "🍪");
        Assert.Equal("🍪1,250", _points.Format(1250));
        _points.UpdateSettings(x => { x.Symbol = ""; x.Name = "cookies"; x.SingularName = "cookie"; });
        Assert.Equal("2 cookies", _points.Format(2));
    }

    [Fact]
    public void Chat_earns_only_while_live_and_respects_the_cooldown()
    {
        _points.UpdateSettings(s => { s.MessageAmount = 5; s.MessageCooldownSeconds = 60; });
        _bus.Publish(Chat.Message("hi", "alice", "1"));
        Assert.Equal(0, _points.Get("Twitch", "1"));   // offline

        GoLive();
        Assert.True(_points.IsLive("twitch"));
        _bus.Publish(Chat.Message("hi", "alice", "1"));
        _bus.Publish(Chat.Message("spam", "alice", "1"));
        Assert.Equal(5, _points.Get("twitch", "1"));
        _clock.Advance(61);
        _bus.Publish(Chat.Message("later", "alice", "1"));
        Assert.Equal(10, _points.Get("twitch", "1"));
        // Stored as the "points" user variable, visible to the variable sub-actions.
        Assert.Equal(10L, _vars.Get("twitch", "1", "points"));

        _bus.Publish(CreditsTests.Activity(FeedKind.StreamOffline, "Streamer", "Stream ended"));
        _clock.Advance(61);
        _bus.Publish(Chat.Message("after", "alice", "1"));
        Assert.Equal(10, _points.Get("twitch", "1"));

        _points.UpdateSettings(s => s.OnlyWhileLive = false);
        _bus.Publish(Chat.Message("offline ok", "alice", "1"));
        Assert.Equal(15, _points.Get("twitch", "1"));
    }

    [Fact]
    public void Present_viewers_earn_each_interval_while_live()
    {
        _points.UpdateSettings(s => { s.PresentAmount = 2; s.PresentIntervalMinutes = 1; s.MessageAmount = 0; s.IgnoredUsers.Add("Nightbot"); });
        GoLive();
        _bus.Publish(Chat.Message("hi", "alice", "1"));
        _bus.Publish(Chat.Message("hi", "Nightbot", "9"));
        _bus.Publish(Chat.Message("hi", "rumbler", "r1", "Rumble"));   // Rumble is not live

        _points.Tick(_clock.Now);                 // arms the interval
        _clock.Advance(30);
        _points.Tick(_clock.Now);
        Assert.Equal(0, _points.Get("twitch", "1"));
        _clock.Advance(31);
        _points.Tick(_clock.Now);
        Assert.Equal(2, _points.Get("twitch", "1"));
        Assert.Equal(0, _points.Get("twitch", "9"));
        Assert.Equal(0, _points.Get("rumble", "r1"));
        _clock.Advance(60);
        _points.Tick(_clock.Now);
        Assert.Equal(4, _points.Get("twitch", "1"));

        _clock.Advance(11 * 60);                  // alice is no longer present (10 minute window)
        _points.Tick(_clock.Now);
        Assert.Equal(4, _points.Get("twitch", "1"));
    }

    [Fact]
    public void Follow_sub_and_gift_bonuses_use_the_module_event_user_id()
    {
        _points.UpdateSettings(s => { s.FollowBonus = 10; s.SubscriptionBonus = 100; s.GiftBonusPerSub = 50; });
        _bus.Publish(new BotEvent("fake.follow", new Dictionary<string, object?> { ["user"] = "Fran", ["userId"] = "77" }));
        _bus.Publish(new BotEvent("fake.gift", new Dictionary<string, object?> { ["user"] = "Gus", ["userId"] = "88", ["total"] = 3 }));
        _bus.Publish(new BotEvent("fake.gift", new Dictionary<string, object?> { ["user"] = "", ["isAnonymous"] = true, ["total"] = 5 }));
        Assert.Equal(10, _points.Get("fake", "77"));
        Assert.Equal(150, _points.Get("fake", "88"));
        Assert.Equal("Fran", _vars.Snapshot("fake").Single(v => v.UserId == "77").UserName);

        _points.UpdateSettings(s => s.Enabled = false);
        _bus.Publish(new BotEvent("fake.follow", new Dictionary<string, object?> { ["user"] = "Fran", ["userId"] = "77" }));
        Assert.Equal(10, _points.Get("fake", "77"));
    }

    [Fact]
    public void Balances_clamp_spend_transfer_and_leaderboard()
    {
        _points.UpdateSettings(s => s.MaxBalance = 1000);
        Assert.Equal(1000, _points.Add("twitch", "1", "alice", 5000));
        Assert.Equal(0, _points.Remove("twitch", "1", "alice", 5000));
        _points.Set("twitch", "1", "alice", 300);
        _points.Set("twitch", "2", "bob", 300);
        _points.Set("youtube", "y1", "yuki", 900);
        _points.Set("twitch", "3", "cleo", 50);

        Assert.False(_points.TrySpend("twitch", "3", "cleo", 51, out var balance));
        Assert.Equal(50, balance);
        Assert.True(_points.TrySpend("twitch", "3", "cleo", 50, out balance));
        Assert.Equal(0, balance);

        Assert.Equal((200L, 400L), _points.Transfer("twitch", "1", "alice", "twitch", "2", "bob", 100));
        Assert.Throws<InvalidOperationException>(() => _points.Transfer("twitch", "1", "alice", "twitch", "2", "bob", 201));
        Assert.Throws<InvalidOperationException>(() => _points.Transfer("twitch", "1", "alice", "twitch", "1", "alice", 1));
        Assert.Throws<InvalidOperationException>(() => _points.Transfer("twitch", "1", "alice", "twitch", "2", "bob", 0));
        Assert.Throws<InvalidOperationException>(() => _points.Transfer("youtube", "y1", "yuki", "twitch", "2", "bob", 700));   // above max

        var board = _points.Leaderboard();
        Assert.Equal(["yuki", "bob", "alice", "cleo"], board.Select(e => e.UserName));
        Assert.Equal([1, 2, 3, 4], board.Select(e => e.Rank));
        Assert.Equal(["bob", "alice"], _points.Leaderboard("twitch", 2).Select(e => e.UserName));
        Assert.Equal(2, _points.Rank("Twitch", "2"));
        Assert.Equal(0, _points.Rank("twitch", "nobody"));

        _points.Set("twitch", "1", "alice", 400);
        Assert.Equal([2, 2], _points.Leaderboard().Where(e => e.Points == 400).Select(e => e.Rank));

        Assert.Equal(1, _points.ResetAll("youtube"));
        Assert.Equal(0, _points.Get("youtube", "y1"));
    }

    [Fact]
    public void Threshold_trigger_fires_when_crossing_upwards()
    {
        _points.UpdateSettings(s => s.Thresholds = [100, 500]);
        _points.Add("twitch", "1", "alice", 99);
        Assert.Empty(_thresholds.Events);
        _points.Add("twitch", "1", "alice", 450);   // 549: crosses 100 and 500
        Assert.Equal([100L, 500L], _thresholds.Events.Select(e => (long)e.Args["threshold"]!));
        var e = _thresholds.Events[1];
        Assert.Equal(("alice", 549L, 99L), (e.Args["user"], e.Args["points"], e.Args["previousPoints"]));
        _points.Remove("twitch", "1", "alice", 100);
        _points.Add("twitch", "1", "alice", 60);
        Assert.Equal(3, _thresholds.Events.Count);   // crossed 500 again after dropping below

        using var file = new Core.Storage.JsonFile<PointsSettings>(Path.Combine(Path.GetTempPath(), $"cuebot-pts-{Guid.NewGuid():N}.json"), _log);
        var trigger = new PointsTriggers(file).GetTriggers().Single();
        bool Match(string threshold) => trigger.Matches(e, new Core.Actions.TriggerDefinition(),
            Core.Actions.ParameterValues.From(trigger.Parameters, new Dictionary<string, string> { ["threshold"] = threshold }), new Dictionary<string, object?>());
        Assert.True(Match(""));
        Assert.True(Match("500"));
        Assert.False(Match("100"));
    }

    [Fact]
    public async Task Command_style_sub_actions()
    {
        _bus.Publish(Chat.Message("hi", "alice", "1"));
        _bus.Publish(Chat.Message("hi", "bob", "2"));
        var caller = new Dictionary<string, object?> { ["platform"] = "Twitch", ["user"] = "alice", ["userId"] = "1" };

        var added = await SubActionHarness.RunAsync(new AddPointsSubAction(_points), new() { ["amount"] = "250" }, caller);
        Assert.Equal(250L, added.Args["points"]);
        Assert.Equal("250 points", added.Args["pointsFormatted"]);
        Assert.Equal("alice", added.Args["pointsUser"]);

        // "!points bob" → %input0% = bob
        var bob = await SubActionHarness.RunAsync(new GetPointsSubAction(_points), [], new(caller) { ["input0"] = "@bob" });
        Assert.Equal(("bob", 0L), (bob.Args["pointsUser"], bob.Args["points"]));
        // "!points" alone → the caller, with rank
        var mine = await SubActionHarness.RunAsync(new GetPointsSubAction(_points), [], caller);
        Assert.Equal((250L, 1), (mine.Args["points"], mine.Args["pointsRank"]));

        // "!give bob 100"
        var give = await SubActionHarness.RunAsync(new TransferPointsSubAction(_points), [], new(caller) { ["input0"] = "bob", ["input1"] = "100" });
        Assert.Equal(TransferPointsSubAction.Transferred, give.Branch);
        Assert.Equal((150L, 100L), (give.Args["points"], give.Args["targetPoints"]));
        var tooMuch = await SubActionHarness.RunAsync(new TransferPointsSubAction(_points), [], new(caller) { ["input0"] = "bob", ["input1"] = "9999" });
        Assert.Equal(TransferPointsSubAction.Failed, tooMuch.Branch);
        Assert.Contains("Not enough points", (string)tooMuch.Args["pointsError"]!);
        var unknown = await SubActionHarness.RunAsync(new TransferPointsSubAction(_points), [], new(caller) { ["input0"] = "ghost", ["input1"] = "1" });
        Assert.Equal("Unknown user 'ghost'", unknown.Args["pointsError"]);
        var all = await SubActionHarness.RunAsync(new TransferPointsSubAction(_points), [], new(caller) { ["input0"] = "bob", ["input1"] = "all" });
        Assert.Equal((0L, 250L), (all.Args["points"], all.Args["targetPoints"]));

        var spend = await SubActionHarness.RunAsync(new SpendPointsSubAction(_points), new() { ["amount"] = "100" }, caller);
        Assert.Equal(SpendPointsSubAction.NotEnough, spend.Branch);
        Assert.Equal(100L, spend.Args["pointsMissing"]);
        var bobSpends = await SubActionHarness.RunAsync(new SpendPointsSubAction(_points), new() { ["amount"] = "100" },
            new() { ["platform"] = "twitch", ["user"] = "bob", ["userId"] = "2" });
        Assert.Equal(SpendPointsSubAction.Paid, bobSpends.Branch);
        Assert.Equal(150L, bobSpends.Args["points"]);

        await SubActionHarness.RunAsync(new SetPointsSubAction(_points), new() { ["userName"] = "alice", ["amount"] = "75" }, caller);
        var removed = await SubActionHarness.RunAsync(new RemovePointsSubAction(_points), new() { ["amount"] = "all" }, caller);
        Assert.Equal((0L, 75L), (removed.Args["points"], removed.Args["pointsRemoved"]));

        var board = await SubActionHarness.RunAsync(new PointsLeaderboardSubAction(_points), new() { ["count"] = "3" });
        Assert.Equal("1. bob (150 points), 2. alice (0 points)", board.Args["leaderboard"]);
        Assert.Equal(("bob", 150L), (board.Args["leaderboard1User"], board.Args["leaderboard1Points"]));
        Assert.Equal(2, board.Args["leaderboardCount"]);

        var badAmount = await SubActionHarness.RunAsync(new AddPointsSubAction(_points), new() { ["amount"] = "lots" }, caller);
        Assert.False(badAmount.Success);
        var noUser = await SubActionHarness.RunAsync(new AddPointsSubAction(_points), new() { ["amount"] = "1" });
        Assert.False(noUser.Success);

        var reset = await SubActionHarness.RunAsync(new ResetAllPointsSubAction(_points), []);
        Assert.Equal(2, reset.Args["pointsReset"]);
    }

    public void Dispose()
    {
        _points.Dispose();
        _users.Dispose();
        _vars.Dispose();
        _log.Dispose();
    }

    /// <summary>A module adapter for "fake.*" events, like the Twitch/Rumble ones.</summary>
    private sealed class FakeAdapter : IChatEventAdapter
    {
        public string Platform => "fake";
        public ChatMessage? ToChatMessage(BotEvent e) => null;

        public FeedItem? ToFeedItem(BotEvent e)
        {
            var user = e.Args.GetString("user");
            return e.Type switch
            {
                "fake.follow" => new FeedItem(Platform, FeedKind.Follow, user, $"{user} followed", e.Time),
                "fake.gift" => new FeedItem(Platform, FeedKind.GiftSubscription, user, $"{user} gifted {e.Args.GetString("total")} subs", e.Time),
                _ => null,
            };
        }
    }
}
