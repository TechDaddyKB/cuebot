using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Points;
using Cuebot.Core.Users;
using Cuebot.Core.Variables;
using Cuebot.Tests.Commands;

namespace Cuebot.Tests.Extras;

/// <summary>Streamer.bot's Check/Delete/Reset User Points, Get Points Definition, Award to All, leaderboard offset, multipliers.</summary>
public sealed class PointsStreamerBotTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly TestClock _clock = new();
    private readonly UserVariables _vars;
    private readonly UserRegistry _users;
    private readonly GroupStore _groups;
    private readonly PointsService _points;
    private readonly Dictionary<string, object?> _alice = new() { ["platform"] = "Twitch", ["user"] = "alice", ["userId"] = "1" };

    public PointsStreamerBotTests()
    {
        _bus = new EventBus(_log);
        _vars = new UserVariables(null, _log, _clock);
        _users = new UserRegistry(null, _bus, _log, _clock);
        _groups = new GroupStore(null, _log, _bus);
        _points = new PointsService(_vars, null, _bus, _log, _users, [], _clock, _groups);
        _points.UpdateSettings(s => s.MessageAmount = 0);
        _bus.Publish(Chat.Message("hi", "alice", "1"));
        _bus.Publish(Chat.Message("hi", "bob", "2"));
    }

    [Fact]
    public async Task Check_points_branches_without_spending_and_breaks_by_default()
    {
        _points.Set("twitch", "1", "alice", 50);
        var ok = await SubActionHarness.RunAsync(new CheckPointsSubAction(_points), new() { ["amount"] = "40" }, _alice);
        Assert.Equal((CheckPointsSubAction.Enough, 50L, true), (ok.Branch, ok.Args["userPoints"], ok.Args["pointsEnough"]));
        Assert.Equal(50, _points.Get("twitch", "1"));

        var stop = await SubActionHarness.RunAsync(new CheckPointsSubAction(_points), new() { ["amount"] = "60" }, _alice);
        Assert.True(stop.Success);
        Assert.Null(stop.Branch);
        Assert.Empty(stop.Args); // the action stopped before the final capture

        var branch = await SubActionHarness.RunAsync(new CheckPointsSubAction(_points),
            new() { ["amount"] = "60", ["breakOnFailure"] = "false", ["destination"] = "mine" }, _alice);
        Assert.Equal((CheckPointsSubAction.NotEnough, 10L, 50L), (branch.Branch, branch.Args["pointsMissing"], branch.Args["mine"]));
    }

    [Fact]
    public async Task Delete_and_reset_user_points()
    {
        _points.Set("twitch", "1", "alice", 50);
        var reset = await SubActionHarness.RunAsync(new ResetUserPointsSubAction(_points), [], _alice);
        Assert.Equal(0L, reset.Args["userPoints"]);
        Assert.True(_vars.TryGet("twitch", "1", "points", out _));

        var deleted = await SubActionHarness.RunAsync(new DeletePointsSubAction(_points), [], _alice);
        Assert.Equal(true, deleted.Args["pointsDeleted"]);
        Assert.False(_vars.TryGet("twitch", "1", "points", out _));
        var again = await SubActionHarness.RunAsync(new DeletePointsSubAction(_points), [], _alice);
        Assert.Equal(false, again.Args["pointsDeleted"]);
    }

    [Fact]
    public async Task Get_points_definition_flattens_the_currency()
    {
        _points.UpdateSettings(s => { s.Name = "cookies"; s.SingularName = "cookie"; s.Symbol = "C"; });
        var r = await SubActionHarness.RunAsync(new GetPointsDefinitionSubAction(_points), new() { ["variable"] = "def" });
        Assert.Equal(("cookies", "cookie", "C", true), (r.Args["def.name"], r.Args["def.singularName"], r.Args["def.symbol"], r.Args["def.enabled"]));
        Assert.Contains("\"cookies\"", (string)r.Args["def"]!);
        Assert.False((await SubActionHarness.RunAsync(new GetPointsDefinitionSubAction(_points), new() { ["currency"] = "gems" })).Success);
    }

    [Fact]
    public async Task Leaderboard_offset_skips_places()
    {
        _points.Set("twitch", "1", "alice", 30);
        _points.Set("twitch", "2", "bob", 20);
        _points.Set("twitch", "3", "cat", 10);
        var r = await SubActionHarness.RunAsync(new PointsLeaderboardSubAction(_points), new() { ["count"] = "1", ["offset"] = "1" });
        Assert.Equal("2. bob (20 points)", r.Args["leaderboard"]);
        Assert.Equal((1, 2), (r.Args["leaderboardCount"], r.Args["leaderboard1Rank"]));
    }

    [Fact]
    public async Task Award_points_to_one_user_can_apply_multipliers()
    {
        _points.SetUserMultiplier("twitch", "1", 2);
        var plain = await SubActionHarness.RunAsync(new Cuebot.Core.Points.AddPointsSubAction(_points), new() { ["amount"] = "10" }, _alice);
        Assert.Equal(10L, plain.Args["points"]);
        var multiplied = await SubActionHarness.RunAsync(new Cuebot.Core.Points.AddPointsSubAction(_points),
            new() { ["amount"] = "10", ["applyMultipliers"] = "true", ["destination"] = "userPoints" }, _alice);
        Assert.Equal((20L, 30L), (multiplied.Args["pointsAdded"], multiplied.Args["userPoints"]));
    }

    [Fact]
    public async Task Award_points_to_all_pays_every_known_user_or_only_active_ones()
    {
        _vars.Set("youtube", "y1", "yuri", "points", 5L, persist: true); // a balance holder never seen in chat
        var all = await SubActionHarness.RunAsync(new AwardPointsToAllSubAction(_points), new() { ["amount"] = "3" });
        Assert.Equal(3, all.Args["pointsAwarded"]);
        Assert.Equal((3L, 3L, 8L), (_points.Get("twitch", "1"), _points.Get("twitch", "2"), _points.Get("youtube", "y1")));

        var twitch = await SubActionHarness.RunAsync(new AwardPointsToAllSubAction(_points), new() { ["amount"] = "1", ["platform"] = "twitch" });
        Assert.Equal(2, twitch.Args["pointsAwarded"]);
        Assert.Equal(8L, _points.Get("youtube", "y1"));
    }

    public void Dispose()
    {
        _points.Dispose();
        _users.Dispose();
        _vars.Dispose();
        _log.Dispose();
    }
}
