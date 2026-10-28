using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Commands.Timers;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Quotes;
using Cuebot.Core.Users;
using Cuebot.Core.Variables;
using Cuebot.Tests.Commands;

namespace Cuebot.Tests.Variables;

/// <summary>Streamer.bot variable names on core triggers: globals, groups, quotes, cron; Pick Color; %runningActionId%.</summary>
public sealed class StreamerBotTriggerVariableTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly EventBus _bus;

    public StreamerBotTriggerVariableTests() => _bus = new EventBus(_log);

    [Fact]
    public void Global_variable_events_carry_global_names_and_filter_on_persisted()
    {
        var globals = new GlobalVariables(null, _log);
        using var users = new UserVariables(null, _log);
        using var triggers = new VariableTriggers(globals, users, _bus);
        var events = new EventRecorder(_bus);

        globals.SetValue("count", 1L, persist: true);
        globals.SetValue("count", 2L, persist: true);
        globals.SetValue("temp", "x", persist: false);
        globals.Remove("count");
        users.Set("twitch", "7", "Ann", "points", 5L, persist: true);

        var created = events.Events.First(e => e.Type == VariableEvents.GlobalCreated);
        Assert.Equal(("count", 1L, true), (created.Args["global.name"], created.Args["global.value"], created.Args["global.persisted"]));
        Assert.IsType<DateTime>(created.Args["global.lastWrite"]);
        var updated = events.Events.First(e => e.Type == VariableEvents.GlobalUpdated);
        Assert.Equal((1L, 2L), (updated.Args["global.oldValue"], updated.Args["global.newValue"]));
        var deleted = events.Events.First(e => e.Type == VariableEvents.GlobalDeleted);
        Assert.Equal("count", deleted.Args["global.name"]);
        var user = events.Events.First(e => e.Type == VariableEvents.UserUpdated);
        Assert.Equal(("7", "twitch", "Ann", 5L), (user.Args["global.userId"], user.Args["global.userType"], user.Args["global.userLogin"], user.Args["global.newValue"]));

        var trigger = triggers.GetTriggers().First(t => t.TypeId == "variables.globalCreated");
        bool Matches(BotEvent e, string persisted) => trigger.Matches(e, new TriggerDefinition(),
            ParameterValues.From(trigger.Parameters, new Dictionary<string, string> { ["persisted"] = persisted }), new Dictionary<string, object?>());
        var temp = events.Events.First(e => e.Type == VariableEvents.GlobalCreated && (string?)e.Args["global.name"] == "temp");
        Assert.True(Matches(temp, "Either"));
        Assert.True(Matches(temp, "No"));
        Assert.False(Matches(temp, "Yes"));
        Assert.True(Matches(created, "Yes"));

        var userTrigger = triggers.GetTriggers().First(t => t.TypeId == "variables.userUpdated");
        Assert.True(userTrigger.Matches(user, new TriggerDefinition(), ParameterValues.From(userTrigger.Parameters,
            new Dictionary<string, string> { ["userLogin"] = "@ann", ["platform"] = "Twitch" }), new Dictionary<string, object?>()));
        Assert.False(userTrigger.Matches(user, new TriggerDefinition(), ParameterValues.From(userTrigger.Parameters,
            new Dictionary<string, string> { ["userLogin"] = "bob" }), new Dictionary<string, object?>()));
    }

    [Fact]
    public void Group_events_carry_group_and_user_variables()
    {
        using var groups = new GroupStore(null, _log, _bus);
        var events = new EventRecorder(_bus);
        groups.AddUser("Regulars", "Twitch", "5", "Bob", createGroup: true);
        var added = events.Events.First(e => e.Type == GroupEvents.UsersAdded);
        Assert.Equal(("Regulars", "Regulars", false, 1), (added.Args["group.id"], added.Args["group.name"], added.Args["group.bots"], added.Args["group.user.count"]));
        Assert.Equal(("5", "Bob", "bob", "twitch"), (added.Args["group.user0.userId"], added.Args["group.user0.user"],
            added.Args["group.user0.userName"], added.Args["group.user0.userType"]));
        Assert.Equal("Regulars", events.Events.First(e => e.Type == GroupEvents.Added).Args["group.name"]);
    }

    [Fact]
    public async Task Quote_events_and_steps_set_quote_time_and_success()
    {
        var dir = Directory.CreateTempSubdirectory("cuebot-sbq-").FullName;
        try
        {
            using var quotes = new QuoteStore(new AppPaths(dir), _log, _bus);
            var events = new EventRecorder(_bus, QuoteEvents.Added);
            var add = await SubActionHarness.RunAsync(new AddQuoteSubAction(quotes), new() { ["text"] = "hello" });
            Assert.Equal(true, add.Args["success"]);
            Assert.IsType<DateTime>(add.Args["quoteTime"]);
            Assert.IsType<DateTime>(Assert.Single(events.Events).Args["quoteTime"]);

            var missing = await SubActionHarness.RunAsync(new GetQuoteSubAction(quotes), new() { ["id"] = "99" });
            Assert.Equal(false, missing.Args["success"]);
            var deleted = await SubActionHarness.RunAsync(new DeleteQuoteSubAction(quotes), new() { ["id"] = "1" });
            Assert.Equal(true, deleted.Args["success"]);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Cron_trigger_matches_its_own_expression_each_minute()
    {
        var events = new EventRecorder(_bus, CronClock.EventType);
        var clock = new CronClock(_bus);
        var start = new DateTimeOffset(2026, 10, 28, 12, 14, 30, TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 10, 28, 12, 14, 30)));
        clock.Tick(start);                       // started mid-minute: nothing yet
        clock.Tick(start.AddSeconds(31));        // 12:15:01
        clock.Tick(start.AddSeconds(40));        // same minute: nothing
        var e = Assert.Single(events.Events);
        var minute = Assert.IsType<DateTime>(e.Args["minute"]);
        Assert.Equal((12, 15), (minute.Hour, minute.Minute));

        var trigger = new CronTriggers().GetTriggers().Single();
        Assert.Equal(CronClock.EventType, trigger.EventTypes.Single());
        var args = new Dictionary<string, object?>();
        bool Matches(string expression) => trigger.Matches(e, new TriggerDefinition(),
            ParameterValues.From(trigger.Parameters, new Dictionary<string, string> { ["expression"] = expression }), args);
        Assert.True(Matches("*/15 * * * *"));
        Assert.Equal("*/15 * * * *", args["expression"]);
        Assert.Equal(minute, args["triggeredAt"]);
        Assert.False(Matches("0 * * * *"));
        Assert.False(Matches("not cron"));
    }

    [Fact]
    public void Cron_clock_publishes_only_when_a_cron_trigger_matches()
    {
        var events = new EventRecorder(_bus, CronClock.EventType);
        var profile = new ProfileService(null, _log);
        var clock = new CronClock(_bus, profile);
        var t0 = new DateTimeOffset(new DateTime(2026, 10, 28, 12, 14, 0, DateTimeKind.Local));
        clock.Tick(t0);
        Assert.Empty(events.Events);
        profile.Profile.Actions.Add(new ActionDefinition
        {
            Name = "Quarter",
            Triggers = [new TriggerDefinition { Type = CronTriggers.TypeId, Settings = { ["expression"] = "*/15 * * * *" } }],
        });
        profile.NotifyChanged();
        clock.Tick(t0.AddMinutes(1));
        Assert.Single(events.Events);
        clock.Tick(t0.AddMinutes(2));
        Assert.Single(events.Events);
    }

    [Theory]
    [InlineData("#FF8000", 255, 255, 128, 0, "#FF8000", "#FFFF8000", 4278223103L)]
    [InlineData("80102030", 128, 16, 32, 48, "#102030", "#80102030", 2150637584L)]
    public async Task Pick_color_fills_the_color_variables(string color, int a, int r, int g, int b, string html, string htmlAlpha, long obs)
    {
        var result = await SubActionHarness.RunAsync(new PickColorSubAction(), new() { ["color"] = color, ["name"] = "c" });
        Assert.Equal((a, r, g, b), (result.Args["c.color.a"], result.Args["c.color.r"], result.Args["c.color.g"], result.Args["c.color.b"]));
        Assert.Equal((html, htmlAlpha, obs), (result.Args["c.html"], result.Args["c.htmlalpha"], result.Args["c.obs"]));
        Assert.False((await SubActionHarness.RunAsync(new PickColorSubAction(), new() { ["color"] = "blue" })).Success);
    }

    [Fact]
    public async Task Runs_carry_running_action_id()
    {
        var result = await SubActionHarness.RunAsync(new PickColorSubAction(), []);
        // Streamer.bot types it as a Guid; Cuebot's runId is the same id as text.
        Assert.Equal(result.Args["runId"], Assert.IsType<Guid>(result.Args["runningActionId"]).ToString());
    }

    public void Dispose() => _log.Dispose();
}
