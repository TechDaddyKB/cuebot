using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Users;
using static Cuebot.Tests.Engine.EngineHarness;

namespace Cuebot.Tests.Engine;

/// <summary>"Only while live", the Cancel running actions step, and If's at least / at most and AND/OR.</summary>
public sealed class ActionExtensionsTests : IDisposable
{
    private readonly FakeLive _twitch = new("Twitch");
    private readonly FakeLive _kick = new("Kick");
    private readonly EngineHarness _h;

    public ActionExtensionsTests() => _h = new EngineHarness(live: new StreamLiveState(() => [_twitch, _kick]));

    public void Dispose() => _h.Dispose();

    // ---------- Only while live

    [Theory]
    [InlineData(null, null, false)]    // not known yet counts as offline
    [InlineData(false, false, false)]
    [InlineData(true, false, true)]
    [InlineData(false, true, true)]
    public void Any_live_platform_counts(bool? twitch, bool? kick, bool expected)
    {
        _twitch.IsLive = twitch;
        _kick.IsLive = kick;
        Assert.Equal(expected, new StreamLiveState(() => [_twitch, _kick]).IsLive());
    }

    [Fact]
    public void A_platform_choice_narrows_it_and_ignores_case()
    {
        _kick.IsLive = true;
        var live = new StreamLiveState(() => [_twitch, _kick]);
        Assert.False(live.IsLive(["Twitch"]));
        Assert.True(live.IsLive(["twitch", "KICK"]));
        Assert.True(live.Allows(onlyWhileLive: false, ["Twitch"]));
        Assert.False(live.Allows(onlyWhileLive: true, ["Twitch"]));
    }

    [Fact]
    public async Task A_live_only_trigger_fires_only_while_live_but_can_still_be_tested()
    {
        var trigger = new TriggerDefinition { Type = "test.event", OnlyWhileLive = true, LivePlatforms = ["Twitch"] };
        var action = _h.Add("Gated", a => a.Triggers = [trigger], Record("ran"));

        Assert.Empty(_h.Engine.Dispatch(new BotEvent("test.event", new Dictionary<string, object?>())));
        _kick.IsLive = true; // the wrong platform
        Assert.Empty(_h.Engine.Dispatch(new BotEvent("test.event", new Dictionary<string, object?>())));

        Assert.True(await _h.Engine.TestTriggerAsync(action, trigger));
        Assert.Equal(["ran"], _h.Calls);

        _twitch.IsLive = true;
        var runs = _h.Engine.Dispatch(new BotEvent("test.event", new Dictionary<string, object?>()));
        Assert.True(await Assert.Single(runs));
        Assert.Equal(["ran", "ran"], _h.Calls);
    }

    [Fact]
    public void A_live_only_timer_skips_offline_rounds_and_keeps_counting_chat()
    {
        using var log = new AppLog();
        var profile = new ProfileService(null, log);
        profile.Profile.Timers.Add(new TimerDefinition { Name = "T", IntervalSeconds = 10, MinChatLines = 2, OnlyWhileLive = true });
        profile.NotifyChanged();
        var bus = new EventBus(log);
        var fired = new List<BotEvent>();
        bus.Subscribe(e => { if (e.Type == TimerService.EventType) fired.Add(e); });
        var clock = new Cuebot.Tests.Commands.TestClock();
        using var timers = new TimerService(profile, bus, clock, live: new StreamLiveState(() => [_twitch]));

        void Run(int seconds)
        {
            for (var i = 0; i < seconds; i++)
            {
                clock.Advance(1);
                timers.Tick();
            }
        }

        timers.Tick(); // arm
        for (var i = 0; i < 3; i++) bus.Publish(new BotEvent(ChatHub.MessageEvent, new Dictionary<string, object?> { ["message"] = "hi" }));
        Run(25);
        Assert.Empty(fired);

        _twitch.IsLive = true;
        Run(10);
        Assert.Equal(3, Assert.Single(fired).Args["chatLines"]);
    }

    [Fact]
    public void A_live_only_command_stays_silent_offline_without_using_its_cooldown()
    {
        using var log = new AppLog();
        var bus = new EventBus(log);
        var store = new CommandStore(null, log);
        store.Save(new CommandDefinition { Name = "Hi", Commands = ["!hi"], GlobalCooldownSeconds = 60, OnlyWhileLive = true });
        using var service = new CommandService(store, new GroupStore(null, log, bus), bus, log, live: new StreamLiveState(() => [_twitch]));
        var events = new List<string>();
        bus.Subscribe(e => { if (e.Type is CommandService.TriggeredEvent or CommandService.CooldownEvent) events.Add(e.Type); });

        bus.Publish(Cuebot.Tests.Commands.Chat.Message("!hi"));
        Assert.Empty(events);

        _twitch.IsLive = true;
        bus.Publish(Cuebot.Tests.Commands.Chat.Message("!hi"));
        Assert.Equal([CommandService.TriggeredEvent], events);
    }

    [Fact]
    public void Saved_files_only_mention_the_option_when_it_is_used()
    {
        Assert.DoesNotContain("live", JsonSerializer.Serialize(new TimerDefinition(), Json.Options), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("live", JsonSerializer.Serialize(new TriggerDefinition(), Json.Options), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("live", JsonSerializer.Serialize(new CommandDefinition { Name = "x" }, Json.Options), StringComparison.OrdinalIgnoreCase);

        var back = JsonSerializer.Deserialize<TimerDefinition>(
            JsonSerializer.Serialize(new TimerDefinition { OnlyWhileLive = true, LivePlatforms = ["Kick"] }, Json.Options), Json.Options)!;
        Assert.True(back.OnlyWhileLive);
        Assert.Equal(["Kick"], back.LivePlatforms);
    }

    // ---------- Cancel running actions

    private static SubActionDefinition CancelStep(string? action = null, bool pending = true, bool keep = true) =>
        Step("core.cancelRuns", ("target", action is null ? CancelRunsSubAction.ThisAction : CancelRunsSubAction.ChosenAction),
            ("action", action ?? ""), ("includePending", pending ? "true" : "false"), ("keepThisRun", keep ? "true" : "false"));

    [Fact]
    public async Task Cancels_a_chosen_actions_running_and_queued_runs()
    {
        _h.AddQueue("Alerts", blocking: true);
        var target = _h.Add("Target", a => a.Queue = "Alerts", Gate("hold"), Record("not reached"));
        var running = _h.Run(target);
        await Until(() => _h.Gates.Inside == 1, "the run to start");
        var queued = _h.Run(target);
        await Until(() => _h.Queues.GetPendingCount("Alerts") == 1, "a queued run");

        Assert.True(await _h.Run(_h.Add("Stopper", CancelStep("Target"), Record("cancelled %runsCancelled%"))));

        Assert.False(await running);
        Assert.False(await queued);
        Assert.Equal(["cancelled 2"], _h.Calls);
    }

    [Fact]
    public async Task Can_leave_queued_runs_waiting()
    {
        _h.AddQueue("Alerts", blocking: true);
        var target = _h.Add("Target", a => a.Queue = "Alerts", Gate("hold"), Record("finished"));
        var running = _h.Run(target);
        await Until(() => _h.Gates.Inside == 1, "the run to start");
        var queued = _h.Run(target);
        await Until(() => _h.Queues.GetPendingCount("Alerts") == 1, "a queued run");

        Assert.True(await _h.Run(_h.Add("Stopper", CancelStep("Target", pending: false), Record("cancelled %runsCancelled%"))));
        Assert.False(await running);

        _h.Gates.Open("hold");
        Assert.True(await queued);
        Assert.Equal(["cancelled 1", "finished"], _h.Calls);
    }

    [Fact]
    public async Task This_action_cancels_its_other_runs_and_keeps_going()
    {
        var action = _h.Add("Self", a => a.Concurrent = true,
            If("%first%", "equals", "yes", [Gate("hold"), Record("not reached")], [CancelStep(), Record("kept %runsCancelled%")]));
        var first = _h.Run(action, new Dictionary<string, object?> { ["first"] = "yes" });
        await Until(() => _h.Gates.Inside == 1, "the first run");

        Assert.True(await _h.Run(action));
        Assert.False(await first);
        Assert.Equal(["kept 1"], _h.Calls);
    }

    [Fact]
    public async Task Can_cancel_its_own_run_too()
    {
        var action = _h.Add("Self", CancelStep(keep: false), Record("not reached"));
        Assert.False(await _h.Run(action));
        Assert.Empty(_h.Calls);
        Assert.Equal(ActionRunState.Cancelled, _h.Completed.Single().State);
    }

    [Fact]
    public async Task A_nested_step_does_not_cancel_the_action_that_started_it()
    {
        var outer = _h.Add("Outer", Step("core.runAction", ("action", "Inner"), ("wait", "true")), Record("outer went on"));
        _h.Add("Inner", CancelStep("Outer"), Record("inner %runsCancelled%"));

        Assert.True(await _h.Run(outer));
        Assert.Equal(["inner 0", "outer went on"], _h.Calls);
    }

    [Fact]
    public async Task An_unknown_action_fails_the_step()
    {
        Assert.False(await _h.Run(_h.Add("Stopper", CancelStep("Nope"))));
    }

    // ---------- If: at least, at most, AND/OR

    [Theory]
    [InlineData("10", "at least", "10", true)]
    [InlineData("11", "at least", "10", true)]
    [InlineData("9", "at least", "10", false)]
    [InlineData("10", "at most", "10", true)]
    [InlineData("9", "at most", "10", true)]
    [InlineData("11", "at most", "10", false)]
    [InlineData("9", "at most", "10.5", true)]    // numbers, not text
    [InlineData("b", "at least", "A", true)]      // text falls back, ignoring case
    public void At_least_and_at_most(string left, string op, string right, bool expected) =>
        Assert.Equal(expected, Conditions.Evaluate(left, op, right));

    private static SubActionDefinition Combined(string combine, params (string Left, string Op, string Right)[] rows)
    {
        var step = If(rows[0].Left, rows[0].Op, rows[0].Right, [Record("yes")], [Record("no")]);
        step.Parameters["combine"] = combine;
        for (var i = 1; i < rows.Length; i++)
        {
            step.Parameters[$"left{i + 1}"] = rows[i].Left;
            step.Parameters[$"operator{i + 1}"] = rows[i].Op;
            step.Parameters[$"right{i + 1}"] = rows[i].Right;
        }
        return step;
    }

    [Theory]
    [InlineData("All", "1", "1", "1", "yes")]
    [InlineData("All", "1", "0", "1", "no")]
    [InlineData("Any", "0", "0", "1", "yes")]
    [InlineData("Any", "0", "0", "0", "no")]
    [InlineData("any", "1", "0", "0", "yes")]
    public async Task Conditions_combine(string combine, string a, string b, string c, string expected)
    {
        var action = _h.Add("If", Combined(combine, (a, "equals", "1"), (b, "equals", "1"), (c, "equals", "1")));
        Assert.True(await _h.Run(action));
        Assert.Equal([expected], _h.Calls);
    }

    [Fact]
    public async Task Unused_rows_are_skipped()
    {
        var all = Combined("All", ("1", "equals", "1"), ("0", IfSubAction.NotUsed, "1"));
        Assert.True(await _h.Run(_h.Add("All", all)));
        var any = Combined("Any", ("0", "equals", "1"), ("1", IfSubAction.NotUsed, "1"));
        Assert.True(await _h.Run(_h.Add("Any", any)));
        Assert.Equal(["yes", "no"], _h.Calls);
    }

    [Fact]
    public async Task A_step_saved_before_combining_existed_keeps_its_one_condition()
    {
        var old = Step("core.if", ("left", "5"), ("operator", "greater than"), ("right", "3"));
        old.Branch(IfSubAction.True).Add(Record("yes"));
        old.Branch(IfSubAction.False).Add(Record("no"));
        old.Parameters["left2"] = "0"; // ignored while Combine is off
        old.Parameters["operator2"] = "equals";
        old.Parameters["right2"] = "1";
        Assert.True(await _h.Run(_h.Add("Old", old)));
        Assert.Equal(["yes"], _h.Calls);
    }

    [Fact]
    public async Task Regex_captures_and_exists_work_in_any_row()
    {
        var step = Combined("All", ("1", "equals", "1"), ("hello world", "matches regex", "(wor)ld"), ("match.count", IfSubAction.Exists, ""));
        step.Branch(IfSubAction.True).Clear();
        step.Branch(IfSubAction.True).Add(Record("%match[1]%"));
        Assert.True(await _h.Run(_h.Add("Regex", step)));
        Assert.Equal(["wor"], _h.Calls);
    }

    [Fact]
    public async Task While_and_switch_get_the_new_operators()
    {
        var loop = While("%loopIteration%", "at most", "2", Record("round %loopIteration%"));
        Assert.True(await _h.Run(_h.Add("Loop", loop)));
        Assert.Equal(["round 0", "round 1", "round 2"], _h.Calls);
    }

    private sealed class FakeLive(string platform) : IStreamLiveStatus
    {
        public string Platform => platform;
        public bool? IsLive { get; set; }
    }
}
