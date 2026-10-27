using Cuebot.Core.Actions;
using static Cuebot.Tests.Engine.EngineHarness;

namespace Cuebot.Tests.Engine;

public class WeightedRandomTests
{
    private static readonly string[] Items = ["a", "b", "c"];

    [Theory]
    [InlineData(0.0, "a")]
    [InlineData(0.16, "a")]
    [InlineData(0.17, "b")]
    [InlineData(0.49, "b")]
    [InlineData(0.5, "c")]
    [InlineData(0.999, "c")]
    [InlineData(1.0, "c")]
    public void Picks_proportionally_to_weight(double roll, string expected)
    {
        // Weights 1, 2, 3 out of 6.
        var weights = new Dictionary<string, double> { ["a"] = 1, ["b"] = 2, ["c"] = 3 };
        Assert.Equal(expected, WeightedRandom.Pick(Items, i => weights[i], new SequenceRandom(roll)));
    }

    [Fact]
    public void Zero_weight_is_never_picked_unless_all_are_zero()
    {
        var weights = new Dictionary<string, double> { ["a"] = 0, ["b"] = 1, ["c"] = double.NaN };
        foreach (var roll in new[] { 0.0, 0.5, 0.99 })
        {
            Assert.Equal("b", WeightedRandom.Pick(Items, i => weights[i], new SequenceRandom(roll)));
        }
        Assert.Equal("a", WeightedRandom.Pick(Items, _ => 0, new SequenceRandom(0.1)));
        Assert.Equal("c", WeightedRandom.Pick(Items, _ => -1, new SequenceRandom(0.9)));
        Assert.Null(WeightedRandom.Pick(Array.Empty<string>(), _ => 1, new SequenceRandom(0.5)));
    }

    [Fact]
    public void System_source_spreads_over_all_candidates()
    {
        var seen = Enumerable.Range(0, 300).Select(_ => WeightedRandom.Pick(Items, _ => 1, SystemRandomSource.Instance)).ToHashSet();
        Assert.Equal(3, seen.Count);
    }
}

public class ActionOptionsTests : IDisposable
{
    private readonly EngineHarness _h = new(new SequenceRandom(0.1, 0.9, 0.5));

    [Fact]
    public async Task Random_action_runs_one_weighted_top_level_step_per_run()
    {
        var disabled = Record("disabled", weight: 100);
        disabled.Enabled = false;
        var action = _h.Add("Random", x => x.RandomAction = true,
            Record("light", weight: 1), disabled, Record("heavy", weight: 3), Record("never", weight: 0));

        await _h.Run(action); // 0.1 * 4 = 0.4 → light (0..1)
        await _h.Run(action); // 0.9 * 4 = 3.6 → heavy (1..4)
        await _h.Run(action); // 0.5 * 4 = 2.0 → heavy
        Assert.Equal(["light", "heavy", "heavy"], _h.Calls);
    }

    [Fact]
    public async Task Random_action_runs_a_picked_group_entirely()
    {
        var action = _h.Add("Random", x => x.RandomAction = true,
            Group(Record("g1"), Record("g2")), Record("single", weight: 0));
        await _h.Run(action);
        Assert.Equal(["g1", "g2"], _h.Calls);
    }

    [Fact]
    public async Task Concurrent_sub_actions_start_together_and_merge_arguments_in_order()
    {
        var action = _h.Add("Parallel", x => x.ConcurrentSubActions = true,
            Group(Gate("one"), SetArg("shared", "first"), SetArg("fromOne", "1")),
            Group(Gate("two"), SetArg("shared", "second"), SetArg("fromTwo", "2")),
            Record("third"));
        var run = _h.Run(action);
        await Until(() => _h.Gates.Inside == 2 && _h.Calls.Count == 1, "all three steps started");
        Assert.Equal(["third"], _h.Calls);
        _h.Gates.Open("two");
        await Task.Delay(20);
        _h.Gates.Open("one");
        Assert.True(await run);

        var entry = _h.History.Entries.Single();
        Assert.Equal("second", entry.ArgsAfterRun!["shared"]);
        Assert.Equal("1", entry.ArgsAfterRun["fromOne"]);
        Assert.Equal("2", entry.ArgsAfterRun["fromTwo"]);
    }

    [Fact]
    public async Task Concurrent_stop_cancels_running_siblings_without_failing()
    {
        var action = _h.Add("Parallel", x => x.ConcurrentSubActions = true,
            Group(Gate("forever"), Record("never")),
            Group(Stop()));

        Assert.True(await _h.Run(action).WaitAsync(TestTimeout));
        Assert.Empty(_h.Calls);
        Assert.Equal(ActionRunState.Completed, _h.History.Entries.Single().State);
    }

    [Fact]
    public async Task Concurrent_failure_fails_the_run()
    {
        var action = _h.Add("Parallel", x => x.ConcurrentSubActions = true, Gate("forever"), Throw());

        Assert.False(await _h.Run(action).WaitAsync(TestTimeout));
        var entry = _h.History.Entries.Single();
        Assert.Equal(ActionRunState.Failed, entry.State);
        Assert.Equal("Throw: boom", entry.Error);
    }

    [Fact]
    public async Task Excluded_actions_skip_history_but_still_report_completion()
    {
        var action = _h.Add("Quiet", x => x.ExcludeFromHistory = true, Record("x"));
        Assert.True(await _h.Run(action));
        Assert.Empty(_h.History.Entries);
        var record = Assert.Single(_h.Completed);
        Assert.True(record.ExcludedFromHistory);
        Assert.True(record.Success);
    }

    [Fact]
    public async Task Test_trigger_runs_with_custom_args_as_if_the_trigger_fired()
    {
        var action = _h.Add("Tested", Record("%user% via %triggerName% (%eventType%) test=%isTest%"));
        action.Triggers[0].Settings["match"] = "never matches";

        Assert.True(await _h.Engine.TestTriggerAsync(action, action.Triggers[0], new Dictionary<string, object?> { ["user"] = "Bob" }));
        Assert.True(await _h.Engine.TestTriggerAsync(action, null, new Dictionary<string, object?> { ["user"] = "Cy" }));

        Assert.Equal(["Bob via Test event (test.event) test=true", "Cy via %triggerName% (%eventType%) test=true"], _h.Calls);
        Assert.Equal("Test: Test event", _h.History.Entries.Last().Source);
    }

    public void Dispose() => _h.Dispose();
}
