using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Points;
using Cuebot.Core.Storage;
using Cuebot.Core.Users;
using Cuebot.Core.Variables;
using Cuebot.Tests.Commands;

namespace Cuebot.Tests.Extras;

/// <summary>Multiple currencies, multipliers, and the version-1 points.json loading as the default currency.</summary>
public sealed class PointsCurrencyTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly TestClock _clock = new();
    private readonly UserVariables _vars;
    private readonly UserRegistry _users;
    private readonly GroupStore _groups;
    private readonly PointsService _points;

    public PointsCurrencyTests()
    {
        _bus = new EventBus(_log);
        _vars = new UserVariables(null, _log, _clock);
        _users = new UserRegistry(null, _bus, _log, _clock);
        _groups = new GroupStore(null, _log, _bus);
        _points = new PointsService(_vars, null, _bus, _log, _users, [new SubAdapter()], _clock, _groups);
    }

    private void GoLive() => _bus.Publish(CreditsTests.Activity(FeedKind.StreamOnline, "Streamer", "Stream went live", "Twitch"));

    [Fact]
    public void Version_1_points_json_loads_as_the_default_currency_with_its_balances()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"cuebot-pts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "points.json");
            File.WriteAllText(path, """
                { "enabled": true, "name": "cookies", "singularName": "cookie", "variableName": "cookies", "messageAmount": 3,
                  "thresholds": [ 100 ] }
                """);
            _vars.Set("twitch", "1", "alice", "cookies", 250L, persist: true);
            using var file = new JsonFile<PointsSettings>(path, _log);
            using var points = new PointsService(_vars, file, _bus, _log, _users);

            var only = Assert.Single(points.Currencies);
            Assert.Equal(("cookies", "cookies", 3L, 2), (only.Name, only.VariableName, only.MessageAmount, points.Settings.Version));
            Assert.Equal(250, points.Get("twitch", "1"));
            Assert.Equal(250, points.Get("twitch", "1", "cookies"));
            Assert.Equal(1m, points.Multiplier("twitch", "1"));   // multipliers default to 1

            points.AddCurrency("gems");
            file.Flush();
            var saved = File.ReadAllText(path);
            Assert.Contains("\"version\": 2", saved);
            Assert.Contains("\"currencies\"", saved);
            using var reloaded = new JsonFile<PointsSettings>(path, _log);
            Assert.Equal(["cookies", "gems"], reloaded.Value.Currencies.Prepend(reloaded.Value).Select(c => c.Name));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Currencies_keep_separate_balances_names_and_rules()
    {
        var gems = _points.AddCurrency("Gold coins");
        Assert.Equal(("Gold coins", "Gold coin", "goldCoins"), (gems.Name, gems.SingularName, gems.VariableName));
        Assert.Equal(["points", "Gold coins"], _points.CurrencyNames);

        _points.Set("twitch", "1", "alice", 10);
        _points.Set("twitch", "1", "alice", 500, "gold coins");
        Assert.Equal(10, _points.Get("twitch", "1"));
        Assert.Equal(500, _points.Get("twitch", "1", "goldCoins"));   // by variable name too
        Assert.Equal(500L, _vars.Get("twitch", "1", "goldCoins"));
        Assert.Equal("500 Gold coins", _points.Format(500, "Gold coins"));
        Assert.Equal("1 Gold coin", _points.Format(1, "Gold coins"));
        Assert.Equal(500, Assert.Single(_points.Leaderboard(currency: "Gold coins")).Points);

        Assert.Throws<InvalidOperationException>(() => _points.AddCurrency("POINTS"));          // name taken
        Assert.Throws<InvalidOperationException>(() => _points.AddCurrency("x", variableName: "points"));
        Assert.Throws<InvalidOperationException>(() => _points.Get("twitch", "1", "nope"));
        Assert.Throws<InvalidOperationException>(() => _points.UpdateCurrency("Gold coins", c => c.Name = "points"));
        Assert.Equal("Gold coins", _points.NameOf("goldcoins"));   // the rejected rename changed nothing

        _points.UpdateCurrency("Gold coins", c => { c.MessageAmount = 7; c.MessageCooldownSeconds = 0; });
        _points.UpdateSettings(s => s.MessageAmount = 1);
        GoLive();
        _bus.Publish(Chat.Message("hi", "alice", "1"));
        Assert.Equal((11L, 507L), (_points.Get("twitch", "1"), _points.Get("twitch", "1", "Gold coins")));

        Assert.False(_points.RemoveCurrency("points"));   // the default stays
        Assert.True(_points.RemoveCurrency("Gold coins"));
        Assert.Equal(507L, _vars.Get("twitch", "1", "goldCoins"));   // balances are kept as variables
        Assert.Single(_points.Currencies);
    }

    [Fact]
    public void Multipliers_scale_automatic_earnings_only()
    {
        _points.UpdateSettings(s =>
        {
            s.MessageAmount = 10;
            s.MessageCooldownSeconds = 0;
            s.VipMultiplier = 2;
            s.SubscriberMultiplier = 1.5m;
            s.Tier3Multiplier = 3;
            s.SubscriptionBonus = 100;
        });
        GoLive();
        _bus.Publish(Chat.Message("hi", "vee", "v", roles: ChatRoles.Vip | ChatRoles.Subscriber));   // highest wins: 2
        _bus.Publish(Chat.Message("hi", "sub", "s", roles: ChatRoles.Subscriber));
        _bus.Publish(Chat.Message("hi", "pleb", "p"));
        Assert.Equal((20L, 15L, 10L), (_points.Get("twitch", "v"), _points.Get("twitch", "s"), _points.Get("twitch", "p")));

        // A tier-3 subscription teaches the tier: the bonus and later chat use the tier-3 multiplier.
        _bus.Publish(new BotEvent("fake.sub", new Dictionary<string, object?> { ["user"] = "sub", ["userId"] = "s", ["tier"] = "3000" }));
        Assert.Equal(3, _points.SubscriberTier("twitch", "s"));
        Assert.Equal(15 + 300, _points.Get("twitch", "s"));

        // Groups and per-user multipliers.
        _groups.AddUser("Regulars", "twitch", "p", "pleb", createGroup: true);
        _points.SetGroupMultiplier("regulars", 1.25m);
        Assert.Equal(1.25m, _points.GetGroupMultiplier("Regulars"));
        _points.SetUserMultiplier("twitch", "p", 2);
        Assert.Equal(2.5m, _points.Multiplier("twitch", "p", "pleb"));
        _bus.Publish(Chat.Message("hi", "pleb", "p"));
        Assert.Equal(10 + 25, _points.Get("twitch", "p"));
        _points.SetUserMultiplier("twitch", "p", null);
        _points.SetGroupMultiplier("Regulars", null);
        Assert.Equal(1m, _points.Multiplier("twitch", "p", "pleb"));

        // Manual changes are never multiplied.
        Assert.Equal(20 + 5, _points.Add("twitch", "v", "vee", 5));
    }

    [Theory]
    [InlineData("1000", 1)]
    [InlineData("2000", 2)]
    [InlineData("3", 3)]
    [InlineData("Tier 2", 2)]
    [InlineData("Prime", 1)]
    [InlineData("", 0)]
    [InlineData("gold", 0)]
    [InlineData("9", 0)]
    public void Subscriber_tiers_parse(string text, int tier) => Assert.Equal(tier, PointsService.ParseTier(text));

    [Fact]
    public void Each_currency_pays_presence_on_its_own_interval_and_awards_reach_groups()
    {
        _points.UpdateSettings(s => { s.PresentAmount = 1; s.PresentIntervalMinutes = 1; s.MessageAmount = 0; });
        _points.AddCurrency("gems");
        _points.UpdateCurrency("gems", c => { c.PresentAmount = 5; c.PresentIntervalMinutes = 2; c.MessageAmount = 0; });
        GoLive();
        _bus.Publish(Chat.Message("hi", "alice", "1"));
        _points.Tick(_clock.Now);   // arms both
        for (var i = 0; i < 4; i++)
        {
            _clock.Advance(61);
            _points.Tick(_clock.Now);
        }
        Assert.Equal((4L, 10L), (_points.Get("twitch", "1"), _points.Get("twitch", "1", "gems")));

        _groups.AddUser("Mods", "twitch", "1", "alice", createGroup: true);
        _groups.AddUser("Mods", "twitch", "", "bob");   // known only by name: skipped (no id)
        Assert.Equal(1, _points.AwardGroup("mods", 100, currency: "gems"));
        Assert.Equal(110, _points.Get("twitch", "1", "gems"));
        Assert.Throws<InvalidOperationException>(() => _points.AwardGroup("nobody", 1));
        Assert.Equal(1, _points.AwardPresent(null, 3));
        Assert.Equal(7, _points.Get("twitch", "1"));
    }

    [Fact]
    public async Task Sub_actions_without_a_currency_use_the_default_and_others_pick_one()
    {
        _points.AddCurrency("gems");
        _bus.Publish(Chat.Message("hi", "alice", "1"));
        var caller = new Dictionary<string, object?> { ["platform"] = "Twitch", ["user"] = "alice", ["userId"] = "1" };

        // An action saved before currencies existed has no "currency" parameter.
        var old = await SubActionHarness.RunAsync(new AddPointsSubAction(_points), new() { ["amount"] = "5" }, caller);
        Assert.Equal(("5 points", "points"), (old.Args["pointsFormatted"], old.Args["pointsCurrency"]));

        var gems = await SubActionHarness.RunAsync(new AddPointsSubAction(_points), new() { ["amount"] = "40", ["currency"] = "Gems" }, caller);
        Assert.Equal((40L, "40 gems", "gems"), (gems.Args["points"], gems.Args["pointsFormatted"], gems.Args["pointsName"]));
        Assert.Equal(5, _points.Get("twitch", "1"));

        var spend = await SubActionHarness.RunAsync(new SpendPointsSubAction(_points), new() { ["amount"] = "30", ["currency"] = "gems" }, caller);
        Assert.Equal(SpendPointsSubAction.Paid, spend.Branch);
        Assert.Equal(10, _points.Get("twitch", "1", "gems"));

        var board = await SubActionHarness.RunAsync(new PointsLeaderboardSubAction(_points), new() { ["currency"] = "gems" });
        Assert.Equal("1. alice (10 gems)", board.Args["leaderboard"]);

        var unknown = await SubActionHarness.RunAsync(new AddPointsSubAction(_points), new() { ["amount"] = "1", ["currency"] = "rubies" }, caller);
        Assert.False(unknown.Success);
        Assert.Equal(5, _points.Get("twitch", "1"));

        _groups.AddUser("Regulars", "twitch", "1", "alice", createGroup: true);
        await SubActionHarness.RunAsync(new SetGroupMultiplierSubAction(_points, _groups), new() { ["group"] = "Regulars", ["multiplier"] = "1.5x" });
        var m = await SubActionHarness.RunAsync(new GetPointsMultiplierSubAction(_points), [], caller);
        Assert.Equal(1.5m, m.Args["pointsMultiplier"]);
        await SubActionHarness.RunAsync(new SetUserMultiplierSubAction(_points), new() { ["multiplier"] = "2" }, caller);
        m = await SubActionHarness.RunAsync(new GetPointsMultiplierSubAction(_points), [], caller);
        Assert.Equal((3m, 2m), (m.Args["pointsMultiplier"], m.Args["pointsUserMultiplier"]));

        var award = await SubActionHarness.RunAsync(new AwardPointsToGroupSubAction(_points, _groups),
            new() { ["group"] = "Regulars", ["amount"] = "10", ["applyMultipliers"] = "true", ["currency"] = "gems" });
        Assert.Equal(1, award.Args["pointsAwarded"]);
        Assert.Equal(10 + 10, _points.Get("twitch", "1", "gems"));   // gems has no multipliers of its own
    }

    [Fact]
    public void Threshold_events_name_the_currency_and_the_trigger_can_filter_on_it()
    {
        var recorder = new EventRecorder(_bus, PointsEvents.ThresholdCrossed);
        _points.AddCurrency("gems");
        _points.UpdateCurrency("gems", c => c.Thresholds = [50]);
        _points.UpdateSettings(s => s.Thresholds = [50]);
        _points.Add("twitch", "1", "alice", 60, currency: "gems");
        _points.Add("twitch", "1", "alice", 60);
        Assert.Equal(["gems", "points"], recorder.Events.Select(e => e.Args["currency"]));
        Assert.Equal("60 gems", recorder.Events[0].Args["pointsFormatted"]);

        var path = Path.Combine(Path.GetTempPath(), $"cuebot-pts-{Guid.NewGuid():N}.json");
        try
        {
            using var file = new JsonFile<PointsSettings>(path, _log);
            file.Value.Currencies.Add(new PointsCurrency { Name = "gems", VariableName = "gemVar" });
            var trigger = new PointsTriggers(file).GetTriggers().Single();
            bool Match(Core.Events.BotEvent e, string currency) => trigger.Matches(e, new Core.Actions.TriggerDefinition(),
                Core.Actions.ParameterValues.From(trigger.Parameters, new Dictionary<string, string> { ["currency"] = currency }),
                new Dictionary<string, object?>());
            Assert.True(Match(recorder.Events[0], ""));
            Assert.True(Match(recorder.Events[0], "Gems"));
            Assert.True(Match(recorder.Events[0], "gemVar"));
            Assert.False(Match(recorder.Events[1], "gems"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    public void Dispose()
    {
        _points.Dispose();
        _groups.Dispose();
        _users.Dispose();
        _vars.Dispose();
        _log.Dispose();
    }

    private sealed class SubAdapter : IChatEventAdapter
    {
        public string Platform => "twitch";
        public ChatMessage? ToChatMessage(BotEvent e) => null;

        public FeedItem? ToFeedItem(BotEvent e) => e.Type == "fake.sub"
            ? new FeedItem(Platform, FeedKind.Subscription, e.Args.GetString("user"), "subscribed", e.Time)
            : null;
    }
}
