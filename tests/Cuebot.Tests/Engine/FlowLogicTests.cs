using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using static Cuebot.Tests.Engine.EngineHarness;

namespace Cuebot.Tests.Engine;

public class GroupTests : IDisposable
{
    private readonly EngineHarness _h = new(new SequenceRandom(0.8));

    [Fact]
    public async Task Group_runs_children_in_order_then_continues()
    {
        var disabled = Record("disabled");
        disabled.Enabled = false;
        var action = _h.Add("G", Record("before"), Group(Record("1"), disabled, Group(Record("2a"), Record("2b")), Record("3")), Record("after"));
        Assert.True(await _h.Run(action));
        Assert.Equal(["before", "1", "2a", "2b", "3", "after"], _h.Calls);
    }

    [Fact]
    public async Task Random_group_runs_one_weighted_child()
    {
        // 0.8 * (1 + 0 + 4) = 4.0 → "b" (weights 1, 0, 4).
        var action = _h.Add("G", Group(random: true, Record("a", 1), Record("zero", 0), Record("b", 4)), Record("after"));
        await _h.Run(action);
        Assert.Equal(["b", "after"], _h.Calls);
    }

    [Fact]
    public async Task Stop_inside_a_nested_group_stops_the_action()
    {
        var action = _h.Add("G", Group(Group(Record("in"), Stop(), Record("never"))), Record("never either"));
        Assert.True(await _h.Run(action));
        Assert.Equal(["in"], _h.Calls);
    }

    [Fact]
    public async Task Failure_inside_groups_is_reported_once_with_the_failing_step()
    {
        var action = _h.Add("G", Group(Group(Throw())));
        Assert.False(await _h.Run(action));
        Assert.Equal("Throw: boom", _h.History.Entries.Single().Error);
    }

    [Fact]
    public void Group_declares_its_steps_branch_and_colour_parameter()
    {
        var group = new GroupSubAction();
        Assert.Equal([GroupSubAction.Steps], group.Branches);
        Assert.Contains(group.Parameters, p => p.Key == "color");
        Assert.Contains(group.Parameters, p => p.Key == "random" && p.Kind == ParameterKind.Boolean);
    }

    public void Dispose() => _h.Dispose();
}

public class SwitchTests : IDisposable
{
    private readonly EngineHarness _h = new();

    private ActionDefinition AddSwitch(string cases, string op = "equals")
    {
        var step = Step(SwitchSubAction.Id, ("input", "%value%"), ("cases", cases), ("operator", op));
        foreach (var branch in new SwitchSubAction().GetBranches(step.Parameters))
        {
            step.Branch(branch).Add(Record($"{branch}"));
        }
        return _h.Add("Switch", step, Record("after %switchCase%"));
    }

    [Fact]
    public void Branches_are_dynamic_per_case_line_plus_default()
    {
        var handler = new SwitchSubAction();
        var step = Step(SwitchSubAction.Id, ("cases", "hi | hello\r\n\r\n  bye \nhi | hello\ndefault"));
        Assert.Equal(["hi | hello", "bye", SwitchSubAction.Default], SubActionBranches.For(handler, step));
        Assert.Equal([SwitchSubAction.Default], SubActionBranches.For(handler, new SubActionDefinition { Type = SwitchSubAction.Id }));
        Assert.Equal([IfSubAction.True, IfSubAction.False], SubActionBranches.For(new IfSubAction(), step));
    }

    [Theory]
    [InlineData("HELLO", "hi | hello")]
    [InlineData("hi", "hi | hello")]
    [InlineData("bye", "bye")]
    [InlineData("3.0", "3")]
    [InlineData("other", "Default")]
    public async Task First_matching_case_wins_else_default(string value, string expected)
    {
        var action = AddSwitch("hi | hello\nbye\n3\nhi");
        await _h.Run(action, new Dictionary<string, object?> { ["value"] = value });
        Assert.Equal([expected, $"after {expected}"], _h.Calls);
    }

    [Fact]
    public async Task Case_values_can_use_variables_and_other_operators()
    {
        var action = AddSwitch("%needle%\nzzz", op: "contains");
        await _h.Run(action, new Dictionary<string, object?> { ["value"] = "a haystack", ["needle"] = "hay" });
        // The branch is named by the raw case line; the recorder resolves its own text.
        Assert.Equal(["hay", "after %needle%"], _h.Calls);
    }

    [Fact]
    public async Task Break_inside_a_case_reaches_the_enclosing_loop()
    {
        var sw = Step(SwitchSubAction.Id, ("input", "%loopIteration%"), ("cases", "2"));
        sw.Branch("2").Add(Break());
        var action = _h.Add("Loop", While("1", "equals", "1", sw, Record("i%loopIteration%")), Record("done"));
        await _h.Run(action);
        Assert.Equal(["i0", "i1", "done"], _h.Calls);
    }

    public void Dispose() => _h.Dispose();
}

public class WhileTests : IDisposable
{
    private readonly EngineHarness _h = new();

    [Fact]
    public async Task Loops_while_the_condition_holds_with_fresh_variables()
    {
        var action = _h.Add("W", While("%loopIteration%", "less than", "3", Record("i=%loopIteration%")), Record("after"));
        Assert.True(await _h.Run(action));
        Assert.Equal(["i=0", "i=1", "i=2", "after"], _h.Calls);
    }

    [Fact]
    public async Task Condition_false_at_start_skips_the_body()
    {
        var action = _h.Add("W", While("a", "equals", "b", Record("never")), Record("after"));
        await _h.Run(action);
        Assert.Equal(["after"], _h.Calls);
    }

    [Fact]
    public async Task Break_inside_group_inside_if_leaves_the_loop()
    {
        var body = new[]
        {
            Record("top %loopIteration%"),
            Group(If("%loopIteration%", "equals", "2", whenTrue: [Group(Break())])),
            Record("bottom %loopIteration%"),
        };
        var action = _h.Add("W", While("1", "equals", "1", body), Record("after"));
        await _h.Run(action);
        Assert.Equal(["top 0", "bottom 0", "top 1", "bottom 1", "top 2", "after"], _h.Calls);
    }

    [Fact]
    public async Task Continue_skips_the_rest_of_the_body()
    {
        var body = new[]
        {
            If("%loopIteration%", "equals", "1", whenTrue: [Continue()]),
            Group(Record("body %loopIteration%")),
        };
        var action = _h.Add("W", While("%loopIteration%", "less than", "3", body));
        await _h.Run(action);
        Assert.Equal(["body 0", "body 2"], _h.Calls);
    }

    [Fact]
    public async Task Break_only_leaves_the_innermost_loop()
    {
        var inner = While("%loopIteration%", "less than", "5",
            If("%loopIteration%", "equals", "1", whenTrue: [Break()]),
            Record("inner %o%.%loopIteration%"));
        var outer = While("%loopIteration%", "less than", "2",
            SetArg("o", "%loopIteration%"),
            Group(inner),
            Record("outer %o% sees %loopIteration%"));
        var action = _h.Add("W", outer, Record("after %loopIteration%"));
        await _h.Run(action, new Dictionary<string, object?> { ["loopIteration"] = "outside" });
        Assert.Equal(["inner 0.0", "outer 0 sees 0", "inner 1.0", "outer 1 sees 1", "after outside"], _h.Calls);
    }

    [Fact]
    public async Task Safety_limit_ends_a_runaway_loop()
    {
        var loop = While("1", "equals", "1", Record("x"));
        loop.Parameters["maxIterations"] = "25";
        var action = _h.Add("W", loop, Record("after"));
        Assert.True(await _h.Run(action).WaitAsync(TestTimeout));
        Assert.Equal(26, _h.Calls.Count);
        Assert.Contains(_h.Log.Recent(), e => e.Message.Contains("safety limit"));
    }

    [Fact]
    public async Task Stop_inside_the_body_stops_the_action()
    {
        var action = _h.Add("W", While("1", "equals", "1", Record("once"), Stop()), Record("never"));
        Assert.True(await _h.Run(action));
        Assert.Equal(["once"], _h.Calls);
    }

    [Fact]
    public async Task Loop_signals_outside_a_loop_end_the_action()
    {
        var a = _h.Add("A", Group(Break()), Record("never"));
        var b = _h.Add("B", If("1", "equals", "1", whenTrue: [Continue()]), Record("never"));
        Assert.True(await _h.Run(a));
        Assert.True(await _h.Run(b));
        Assert.Empty(_h.Calls);
    }

    [Fact]
    public async Task Cancelling_a_running_loop_cancels_the_run()
    {
        var action = _h.Add("W", While("1", "equals", "1", Step("core.delay", ("milliseconds", "5"))));
        var run = _h.Run(action);
        await Until(() => _h.History.Entries.Any(e => e.State == ActionRunState.Running));
        Assert.True(_h.Engine.Cancel(_h.History.Entries.Single().RunId));
        Assert.False(await run.WaitAsync(TestTimeout));
        Assert.Equal(ActionRunState.Cancelled, _h.History.Entries.Single().State);
        Assert.False(_h.Engine.Cancel(_h.History.Entries.Single().RunId));
    }

    public void Dispose() => _h.Dispose();
}

public class LogicStepTests : IDisposable
{
    private readonly EngineHarness _h = new();

    [Fact]
    public async Task Get_input_splits_raw_input_into_numbered_arguments()
    {
        var action = _h.Add("Input",
            Step("core.getInput"), Record("%inputCount%: %input0%/%input1%/%input2%"),
            Step("core.getInput", ("input", "a, b"), ("separator", ",")), Record("%inputCount%: %input0%/%input1%/%input2%"),
            Step("core.getInput", ("input", "%missing%x")), Record("%inputCount%"));

        await _h.Run(action, new Dictionary<string, object?> { ["rawInput"] = "  one two   three " });
        Assert.Equal(["3: one/two/three", "2: a/b/%input2%", "1"], _h.Calls);
    }

    [Fact]
    public async Task Get_input_without_raw_input_yields_no_inputs()
    {
        var action = _h.Add("Input", Step("core.getInput"), Record("%inputCount% %input0%"));
        await _h.Run(action);
        Assert.Equal(["0 %input0%"], _h.Calls);
    }

    [Fact]
    public async Task Comment_does_nothing()
    {
        var action = _h.Add("C", Step("core.comment", ("text", "remember %this%")), Record("after"));
        Assert.True(await _h.Run(action));
        Assert.Equal(["after"], _h.Calls);
    }

    [Fact]
    public async Task Wait_for_signal_resumes_when_another_action_sends_it()
    {
        var waiter = _h.Add("Waiter", x => x.Concurrent = true,
            Step("core.waitForSignal", ("name", "go"), ("timeoutMs", "10000")),
            Record("received=%signalReceived% from %signalSender%"));
        var sender = _h.Add("Sender", Step("core.sendSignal", ("name", "GO")), Record("waiters=%signalWaiters%"));

        var wait = _h.Run(waiter);
        await Until(() => _h.Signals.WaitingCount("go") == 1);
        await _h.Run(sender);
        Assert.True(await wait.WaitAsync(TestTimeout));
        Assert.Equal(["received=true from Sender", "waiters=1"], _h.Calls.Order());
    }

    [Fact]
    public async Task Wait_for_signal_times_out_and_optionally_stops()
    {
        var continues = _h.Add("Continues",
            Step("core.waitForSignal", ("name", "never"), ("timeoutMs", "20")), Record("received=%signalReceived%"));
        var stops = _h.Add("Stops",
            Step("core.waitForSignal", ("name", "never"), ("timeoutMs", "20"), ("stopOnTimeout", "true")), Record("never"));

        Assert.True(await _h.Run(continues));
        Assert.True(await _h.Run(stops));
        Assert.Equal(["received=false"], _h.Calls);
        Assert.Equal(0, _h.Signals.WaitingCount("never"));
    }

    [Fact]
    public async Task Signal_service_only_releases_current_waiters()
    {
        Assert.Equal(0, _h.Signals.Send("early"));
        var wait = _h.Signals.WaitAsync("early", TimeSpan.FromMilliseconds(30), CancellationToken.None);
        Assert.Null(await wait);

        var a = _h.Signals.WaitAsync("x", null, CancellationToken.None);
        var b = _h.Signals.WaitAsync("X", null, CancellationToken.None);
        Assert.Equal(2, _h.Signals.Send("x", new Dictionary<string, object?> { ["k"] = 1 }));
        Assert.Equal(1, (await a)!["k"]);
        Assert.NotNull(await b);
    }

    [Fact]
    public async Task Set_and_get_action_state_by_name()
    {
        var target = _h.Add("Target", Record("target ran"));
        var action = _h.Add("Toggler",
            Step("core.setActionState", ("action", "target"), ("state", "Disable")),
            Step("core.getActionState", ("action", "Target")), Record("after disable %actionEnabled%"),
            Step("core.setActionState", ("action", "Target"), ("state", "Toggle")),
            Step("core.getActionState", ("action", "Target"), ("name", "state")), Record("after toggle %state%"));

        var dispatched = 0;
        _h.Profile.EditDispatcher = edit =>
        {
            dispatched++;
            edit();
            return Task.CompletedTask;
        };
        Assert.True(await _h.Run(action));
        Assert.Equal(["after disable false", "after toggle true"], _h.Calls);
        Assert.Equal(2, dispatched);
        Assert.True(_h.Profile.Profile.Actions.Single(a => a.Id == target.Id).Enabled);
    }

    [Fact]
    public async Task Disabled_action_no_longer_fires_from_triggers()
    {
        _h.Add("Target", Record("target ran"));
        var action = _h.Add("Disabler", Step("core.setActionState", ("action", "Target"), ("state", "Disable")));
        await _h.Run(action);
        var started = _h.Engine.Dispatch(new Core.Events.BotEvent("test.event"));
        await Task.WhenAll(started);
        _ = Assert.Single(started); // only the disabler itself
        Assert.DoesNotContain("target ran", _h.Calls);
    }

    [Fact]
    public async Task Set_action_state_for_an_unknown_action_fails()
    {
        var action = _h.Add("Bad", Step("core.setActionState", ("action", "Nope")));
        Assert.False(await _h.Run(action));
        Assert.Contains("Nope", _h.History.Entries.Single().Error);
    }

    public void Dispose() => _h.Dispose();
}
