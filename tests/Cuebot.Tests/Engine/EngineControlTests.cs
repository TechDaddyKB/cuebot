using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Events;
using static Cuebot.Tests.Engine.EngineHarness;

namespace Cuebot.Tests.Engine;

/// <summary>Streamer.bot's queue, trigger, sub-action, group and custom-event control sub-actions.</summary>
public class EngineControlTests : IDisposable
{
    private readonly EngineHarness _h = new();

    [Fact]
    public async Task Clear_action_queue_cancels_pending_runs_of_one_or_all_queues()
    {
        _h.AddQueue("Alerts", blocking: true);
        var blocker = _h.Add("Blocker", a => a.Queue = "Alerts", Gate("hold"));
        var first = _h.Run(blocker);
        await Until(() => _h.Gates.Inside == 1, "the blocker to start");
        var pending = _h.Run(blocker);
        await Until(() => _h.Queues.GetPendingCount("Alerts") == 1, "a pending run");

        var clear = _h.Add("Clear", Step("core.clearActionQueue", ("queue", "All")), Record("cleared %queueCleared%"));
        Assert.True(await _h.Run(clear));
        Assert.Equal(["cleared 1"], _h.Calls);
        Assert.False(await pending);
        _h.Gates.Open("hold");
        Assert.True(await first);
    }

    [Fact]
    public async Task Set_action_queue_blocking_state_saves_the_queue_option()
    {
        _h.AddQueue("Alerts", blocking: false);
        var action = _h.Add("Block", Step("core.setActionQueueBlocking", ("queue", "Alerts"), ("state", "Enable")));
        Assert.True(await _h.Run(action));
        Assert.True(_h.Profile.Snapshot.FindQueue("Alerts")!.Blocking);

        var toggle = _h.Add("Toggle", Step("core.setActionQueueBlocking", ("queue", "alerts"), ("state", "Toggle")));
        Assert.True(await _h.Run(toggle));
        Assert.False(_h.Queues.IsBlocking("Alerts"));

        // The Default queue never blocks: asking for it fails the action instead of silently doing nothing.
        Assert.False(await _h.Run(_h.Add("Default", Step("core.setActionQueueBlocking", ("queue", "Default")))));
    }

    [Fact]
    public async Task Set_action_queue_pause_state_pauses_resumes_and_clears()
    {
        _h.AddQueue("Alerts", blocking: false);
        var pause = _h.Add("Pause", Step("core.setActionQueuePause", ("queue", "Alerts"), ("state", "Pause")));
        Assert.True(await _h.Run(pause));
        Assert.True(_h.Queues.IsPaused("Alerts"));
        Assert.False(_h.Queues.IsPaused("Default"));

        var held = _h.Run(_h.Add("Held", a => a.Queue = "Alerts", Record("held ran")));
        await Until(() => _h.Queues.GetPendingCount("Alerts") == 1, "a held run");
        var resumeAndClear = _h.Add("Resume", Step("core.setActionQueuePause", ("queue", "All"), ("state", "Resume"), ("clear", "true")));
        Assert.True(await _h.Run(resumeAndClear));
        Assert.False(await held);
        Assert.False(_h.Queues.IsPaused("Alerts"));
        Assert.DoesNotContain("held ran", _h.Calls);

        var toggle = _h.Add("Toggle", Step("core.setActionQueuePause", ("queue", "Alerts"), ("state", "Toggle")));
        Assert.True(await _h.Run(toggle));
        Assert.True(_h.Queues.IsPaused("Alerts"));
    }

    [Fact]
    public async Task Set_trigger_state_finds_the_trigger_by_position_type_or_id()
    {
        var target = _h.Add("Target", Record("x"));
        _h.Profile.Profile.Actions.First(a => a.Id == target.Id).Triggers.Add(new TriggerDefinition { Type = "test.range" });
        _h.Profile.NotifyChanged();

        Assert.True(await _h.Run(_h.Add("ByNumber", Step("core.setTriggerState", ("action", "Target"), ("trigger", "2. Range event"), ("state", "Disable")))));
        var live = _h.Profile.Snapshot.FindAction("Target")!;
        Assert.True(live.Triggers[0].Enabled);
        Assert.False(live.Triggers[1].Enabled);

        Assert.True(await _h.Run(_h.Add("ByType", Step("core.setTriggerState", ("action", "Target"), ("trigger", "test.event"), ("state", "Toggle")))));
        Assert.False(_h.Profile.Snapshot.FindAction("Target")!.Triggers[0].Enabled);

        var id = live.Triggers[1].Id.ToString();
        Assert.True(await _h.Run(_h.Add("ById", Step("core.setTriggerState", ("action", "Target"), ("trigger", id), ("state", "Enable")))));
        Assert.True(_h.Profile.Snapshot.FindAction("Target")!.Triggers[1].Enabled);

        Assert.False(await _h.Run(_h.Add("Missing", Step("core.setTriggerState", ("action", "Target"), ("trigger", "9")))));
    }

    [Fact]
    public async Task Set_sub_action_state_counts_steps_inside_groups()
    {
        _h.Add("Target", Record("one"), Group(Record("two"), Record("three")));
        // Depth first: 1 = "one", 2 = the group, 3 = "two", 4 = "three".
        Assert.True(await _h.Run(_h.Add("Off", Step("core.setSubActionState", ("action", "Target"), ("subAction", "3"), ("state", "Disable")))));
        Assert.True(await _h.Run(_h.Profile.Snapshot.FindAction("Target")!));
        Assert.Equal(["one", "three"], _h.Calls);

        Assert.True(await _h.Run(_h.Add("Toggle", Step("core.setSubActionState", ("action", "Target"), ("subAction", "3. Record"), ("state", "Toggle")))));
        var steps = ActionPaths.Flatten(_h.Profile.Snapshot.FindAction("Target")!.SubActions).ToArray();
        Assert.True(steps[2].Enabled);
    }

    [Fact]
    public async Task Set_sub_action_weight_changes_which_random_step_runs()
    {
        var target = _h.Add("Target", a => a.RandomAction = true, Record("a"), Record("b"));
        Assert.True(await _h.Run(_h.Add("W", Step("core.setSubActionWeight", ("action", "Target"), ("subAction", "1"), ("weight", "0")))));
        Assert.Equal(0, _h.Profile.Snapshot.FindAction("Target")!.SubActions[0].Weight);
        Assert.True(await _h.Run(_h.Profile.Snapshot.FindAction(target.Id)!));
        Assert.Equal(["b"], _h.Calls);

        Assert.False(await _h.Run(_h.Add("Bad", Step("core.setSubActionWeight", ("action", "Target"), ("subAction", "1"), ("weight", "lots")))));
    }

    [Fact]
    public async Task Action_group_state_is_read_and_set()
    {
        var a = _h.Add("A", x => x.Group = "Alerts", Record("a"));
        _h.Add("B", x => { x.Group = "Alerts"; x.Enabled = false; }, Record("b"));
        _h.Add("C", x => x.Group = "Other", Record("c"));

        var get = _h.Add("Get", Step("core.getActionGroupState", ("group", "alerts")),
            Record("state %actionGroupState%"));
        Assert.True(await _h.Run(get));
        Assert.Equal("state 2", _h.Calls[^1]);
        var history = _h.History.Entries.First(e => e.ActionName == "Get");
        Assert.Equal([a.Id.ToString()], Assert.IsType<List<string>>(history.ArgsAfterRun!["actionsEnabled"]));

        Assert.True(await _h.Run(_h.Add("Set", Step("core.setActionGroupState", ("group", "Alerts"), ("state", "Enable")))));
        Assert.All(_h.Profile.Snapshot.Actions.Where(x => x.Group == "Alerts"), x => Assert.True(x.Enabled));
        Assert.True(_h.Profile.Snapshot.FindAction("C")!.Enabled);

        Assert.True(await _h.Run(_h.Add("Toggle", Step("core.setActionGroupState", ("group", "Alerts"), ("state", "Toggle")))));
        Assert.All(_h.Profile.Snapshot.Actions.Where(x => x.Group == "Alerts"), x => Assert.False(x.Enabled));
        Assert.True(await _h.Run(_h.Profile.Snapshot.FindAction("Get")!));
        Assert.Equal("state 0", _h.Calls[^1]);
    }

    [Fact]
    public async Task Trigger_custom_event_runs_actions_with_a_matching_custom_trigger()
    {
        var events = new List<BotEvent>();
        using var sub = _h.Bus.Subscribe(events.Add);
        var listener = _h.AddWithTrigger("Listener",
            new TriggerDefinition { Type = "custom.codeEvent", Settings = { ["eventName"] = "boom" } }, Record("got %eventName% %extra%"));
        var fire = _h.Add("Fire", SetArg("extra", "yes"), Step("core.triggerCustomEvent", ("eventName", "boom"), ("useArgs", "true")));
        Assert.True(await _h.Run(fire));

        var e = Assert.Single(events);
        Assert.Equal("custom.codeEvent", e.Type);
        Assert.Equal("yes", e.Args["extra"]);
        Assert.False(e.Args.ContainsKey("runId"));
        Assert.False(e.Args.ContainsKey("triggerId"));   // the firing run's trigger arguments stay behind
        Assert.Equal("boom", e.Args["customEvent.event"]);
        await Task.WhenAll(_h.Engine.Dispatch(e));
        Assert.Contains("got boom yes", _h.Calls);

        events.Clear();
        Assert.True(await _h.Run(_h.Add("NoArgs", SetArg("extra", "no"), Step("core.triggerCustomEvent", ("eventName", "boom")))));
        Assert.False(Assert.Single(events).Args.ContainsKey("extra"));
        Assert.NotNull(listener);
    }

    [Fact]
    public async Task Get_action_state_sets_streamer_bot_action_state()
    {
        _h.Add("Target", x => x.Enabled = false, Record("x"));
        var get = _h.Add("Get", Step("core.getActionState", ("action", "Target"), ("name", "mine")), Record("%actionState% %mine%"));
        Assert.True(await _h.Run(get));
        Assert.Equal(["false false"], _h.Calls);
    }

    [Fact]
    public void Leading_numbers_parse_with_and_without_labels()
    {
        Assert.Equal(3, ActionPaths.LeadingNumber("3"));
        Assert.Equal(3, ActionPaths.LeadingNumber(" 3. Write to log"));
        Assert.Equal(12, ActionPaths.LeadingNumber("12: x"));
        Assert.Null(ActionPaths.LeadingNumber("3rd"));
        Assert.Null(ActionPaths.LeadingNumber("Record"));
    }

    public void Dispose() => _h.Dispose();
}

/// <summary>Get Input dialog, Random Number "Next Float", Delay random, If regex captures, signals, Always Run.</summary>
public class StreamerBotLogicTests : IDisposable
{
    private readonly EngineHarness _h = new();

    [Fact]
    public async Task Get_input_dialog_stores_the_answer_and_success()
    {
        UserPromptRequest? asked = null;
        _h.Prompts.Handler = (r, _) =>
        {
            asked = r;
            return Task.FromResult<string?>("42");
        };
        var action = _h.Add("Ask", Step("core.getInputDialog", ("title", "Q for %user%"), ("prompt", "How many?"), ("originalText", "1"),
            ("variable", "answer"), ("autoType", "true")), Record("%success% %answer%"));
        Assert.True(await _h.Run(action, new Dictionary<string, object?> { ["user"] = "Ann" }));
        Assert.Equal(new UserPromptRequest("Q for Ann", "How many?", "1"), asked);
        Assert.Equal(["true 42"], _h.Calls);
        Assert.Equal(42L, Convert.ToInt64(_h.History.Entries.Single().ArgsAfterRun!["answer"]));
        Assert.IsNotType<string>(_h.History.Entries.Single().ArgsAfterRun!["answer"]);
    }

    [Fact]
    public async Task Get_input_dialog_cancelled_or_headless_sets_success_false()
    {
        var action = _h.Add("Ask", Step("core.getInputDialog", ("variable", "answer")), Record("%success% [%answer%]"));
        Assert.True(await _h.Run(action));
        _h.Prompts.Handler = (_, _) => Task.FromResult<string?>(null);
        Assert.True(await _h.Run(action));
        Assert.Equal(["false [%answer%]", "false [%answer%]"], _h.Calls);
    }

    [Fact]
    public void The_text_splitter_keeps_its_type_id_under_a_clearer_name()
    {
        Assert.Equal("core.getInput", new GetInputSubAction().TypeId);
        Assert.Equal("Split text into inputs", new GetInputSubAction().DisplayName);
        Assert.Equal("Ask me for input (popup)", new GetInputDialogSubAction(new UserPromptService()).DisplayName);
    }

    [Fact]
    public async Task Random_number_next_float_sets_float_and_percent()
    {
        var action = _h.Add("R", Step("core.randomNumber", ("type", "Next Float")), Step("core.randomNumber", ("min", "5"), ("max", "5")));
        Assert.True(await _h.Run(action));
        var args = _h.History.Entries.Single().ArgsAfterRun!;
        var f = Assert.IsType<double>(args["randomFloat"]);
        Assert.InRange(f, 0, 1);
        Assert.Equal((int)Math.Round(f * 100, MidpointRounding.AwayFromZero), args["randomPercent"]);
        Assert.Equal(5, args["random"]);
    }

    [Theory]
    [InlineData("false", 0.99, 100)]
    [InlineData("true", 0.0, 100)]
    [InlineData("true", 0.999, 200)]
    [InlineData("true", 0.5, 150)]
    public void Delay_picks_a_random_time_between_the_values(string random, double roll, int expected)
    {
        var p = new ParameterValues(new Dictionary<string, string> { ["milliseconds"] = "100", ["random"] = random, ["to"] = "200" });
        Assert.Equal(expected, DelaySubAction.Pick(p, new SequenceRandom(roll)));
    }

    [Fact]
    public async Task If_regex_match_sets_capture_arguments()
    {
        var action = _h.Add("Re", If("give 25 points", "matches regex", @"(\w+) (?<points>\d+)",
            [Record("%match.count%|%match[0]%|%match[1]%|%points%")], [Record("no")]));
        Assert.True(await _h.Run(action));
        Assert.Equal(["3|give 25|give|25"], _h.Calls);
    }

    [Fact]
    public async Task If_does_not_exist_tests_the_argument_name()
    {
        var action = _h.Add("Ex", If("missing", "does not exist", "", [Record("missing")]), If("user", "exists", "", [Record("user")]));
        Assert.True(await _h.Run(action, new Dictionary<string, object?> { ["user"] = "x" }));
        Assert.Equal(["missing", "user"], _h.Calls);
    }

    [Fact]
    public async Task Send_signal_carries_arguments_and_wait_reports_its_outputs()
    {
        var waiter = _h.Add("Wait", SetArg("color", "red"),
            Step("core.waitForSignal", ("name", "go"), ("timeoutMs", "5000"), ("overwrite", "false")),
            Record("%signalName% %signalTimeout% %hasResult% %color% %size% %signalSender%"));
        var waiting = _h.Run(waiter);
        await Until(() => _h.Signals.WaitingCount("go") == 1, "the wait");
        var sender = _h.Add("Send", SetArg("color", "blue"), SetArg("size", "L"),
            Step("core.sendSignal", ("name", "go"), ("includeArgs", "true"), ("arguments", "extra=1")));
        Assert.True(await _h.Run(sender));
        Assert.True(await waiting);
        // Overwrite off keeps the waiter's own color; new arguments arrive.
        Assert.Equal(["go false true red L Send"], _h.Calls);
    }

    [Fact]
    public async Task Queued_signal_is_picked_up_by_a_later_wait_and_timeouts_are_reported()
    {
        Assert.True(await _h.Run(_h.Add("Send", Step("core.sendSignal", ("name", "later"), ("queue", "true"), ("arguments", "n=5")))));
        Assert.Equal(1, _h.Signals.QueuedCount("later"));
        var wait = _h.Add("Wait", Step("core.waitForSignal", ("name", "later"), ("timeoutMs", "50")), Record("%signalTimeout% %n%"),
            Step("core.waitForSignal", ("name", "later"), ("timeoutMs", "20")), Record("%signalTimeout%"));
        Assert.True(await _h.Run(wait));
        Assert.Equal(["false 5", "true"], _h.Calls);
    }

    [Fact]
    public async Task Range_triggers_run_only_the_narrowest_match_unless_always_run()
    {
        TriggerDefinition Range(string min, string max, bool alwaysRun = false) => new()
        {
            Type = "test.range", AlwaysRun = alwaysRun, Settings = { ["minAmount"] = min, ["maxAmount"] = max },
        };
        _h.AddWithTrigger("Any", Range("", ""), Record("any"));
        _h.AddWithTrigger("Hundred", Range("100", "100"), Record("hundred"));
        _h.AddWithTrigger("Big", Range("50", "500"), Record("big"));

        await Task.WhenAll(_h.Engine.Dispatch(new BotEvent("test.range", new Dictionary<string, object?> { ["amount"] = 100 })));
        Assert.Equal(["hundred"], _h.Calls);

        await Task.WhenAll(_h.Engine.Dispatch(new BotEvent("test.range", new Dictionary<string, object?> { ["amount"] = 60 })));
        Assert.Equal(["hundred", "big"], _h.Calls);

        var any = _h.Profile.Profile.Actions.First(a => a.Name == "Any");
        any.Triggers[0].AlwaysRun = true;
        _h.Profile.NotifyChanged();
        await Task.WhenAll(_h.Engine.Dispatch(new BotEvent("test.range", new Dictionary<string, object?> { ["amount"] = 100 })));
        Assert.Equal(4, _h.Calls.Count);
        Assert.Contains("any", _h.Calls);
    }

    [Fact]
    public void Range_width_treats_blank_and_zero_maximum_as_unbounded()
    {
        var type = new EventTriggerType("t", "T", "T", "t",
            [new ParameterDescriptor("minBits", "Min", ParameterKind.Number), new ParameterDescriptor("maxBits", "Max", ParameterKind.Number)]);
        ParameterValues S(string min, string max) => new(new Dictionary<string, string> { ["minBits"] = min, ["maxBits"] = max });
        Assert.Equal(double.PositiveInfinity, RangeTriggers.Width(type, S("1", "0")));
        Assert.Equal(0, RangeTriggers.Width(type, S("100", "100")));
        Assert.Equal(400, RangeTriggers.Width(type, S("100", "500")));
        Assert.Null(RangeTriggers.Width(new EventTriggerType("u", "U", "U", "u"), S("", "")));
    }

    [Fact]
    public async Task Runs_get_escaped_and_url_encoded_input_copies()
    {
        var action = _h.Add("Esc", Record("%rawInputEscaped%|%rawInputUrlEncoded%|%inputEscaped0%|%input0Escaped%|%inputUrlEncoded1%"));
        Assert.True(await _h.Engine.TestTriggerAsync(action, null, new Dictionary<string, object?>
        {
            ["rawInput"] = "https://streamer.bot/Test $$$",
            ["input0"] = "a.b",
            ["input1"] = "c d",
        }));
        Assert.Equal([@"https://streamer\.bot/Test \$\$\$|https%3A%2F%2Fstreamer.bot%2FTest%20%24%24%24|a\.b|a\.b|c%20d"], _h.Calls);
    }

    public void Dispose() => _h.Dispose();
}
