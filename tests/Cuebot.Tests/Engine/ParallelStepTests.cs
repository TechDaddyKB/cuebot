using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using static Cuebot.Tests.Engine.EngineHarness;

namespace Cuebot.Tests.Engine;

public class ParallelStepTests : IDisposable
{
    private readonly EngineHarness _h = new();

    [Fact]
    public async Task Lanes_run_together_in_order_and_the_next_step_waits_for_all()
    {
        var action = _h.Add("Fork",
            Record("before"),
            Parallel(
                [Gate("one"), Record("one done"), SetArg("shared", "first"), SetArg("fromOne", "1")],
                [Gate("two"), Record("two done"), SetArg("shared", "second"), SetArg("fromTwo", "2")]),
            Record("after %shared% %fromOne% %fromTwo%"));
        var run = _h.Run(action);

        await Until(() => _h.Gates.Inside == 2, "both lanes started");
        _h.Gates.Open("two");
        await Until(() => _h.Calls.Contains("two done"), "lane two finished");
        Assert.DoesNotContain(_h.Calls, c => c.StartsWith("after", StringComparison.Ordinal));
        _h.Gates.Open("one");
        Assert.True(await run.WaitAsync(TestTimeout));

        // Lane two finished first, but merging goes by lane order: lane 2 wins for "shared".
        Assert.Equal(["before", "two done", "one done", "after second 1 2"], _h.Calls);
    }

    [Fact]
    public async Task Stop_in_one_lane_cancels_the_others_and_ends_the_action()
    {
        var action = _h.Add("Fork",
            Parallel([Gate("forever"), Record("never")], [Stop()]),
            Record("after"));

        Assert.True(await _h.Run(action).WaitAsync(TestTimeout));
        Assert.Empty(_h.Calls);
        Assert.Equal(ActionRunState.Completed, _h.History.Entries.Single().State);
    }

    [Fact]
    public async Task A_failing_lane_fails_the_run()
    {
        var action = _h.Add("Fork", Parallel([Gate("forever")], [Throw()]));

        Assert.False(await _h.Run(action).WaitAsync(TestTimeout));
        Assert.Equal("Throw: boom", _h.History.Entries.Single().Error);
    }

    [Fact]
    public async Task Break_in_a_lane_leaves_the_enclosing_loop_after_the_lanes_finish()
    {
        var action = _h.Add("Loop",
            While("%loopIteration%", "less than", "5",
                Record("round %loopIteration%"),
                Parallel([Record("lane one")], [If("%loopIteration%", "equals", "1", [Break()], [])])),
            Record("after"));

        Assert.True(await _h.Run(action).WaitAsync(TestTimeout));
        Assert.Equal(["round 0", "lane one", "round 1", "lane one", "after"], _h.Calls);
    }

    [Fact]
    public async Task Empty_and_disabled_lanes_are_skipped()
    {
        var off = Record("off");
        off.Enabled = false;
        var action = _h.Add("Fork", Parallel([], [off], [Record("on")]), Record("after"));

        Assert.True(await _h.Run(action).WaitAsync(TestTimeout));
        Assert.Equal(["on", "after"], _h.Calls);
    }

    [Theory]
    [InlineData(null, 2)]
    [InlineData("", 2)]
    [InlineData("x", 2)]
    [InlineData("1", 2)]
    [InlineData(" 3 ", 3)]
    [InlineData("10", 10)]
    [InlineData("99", 10)]
    public void Lane_count_is_kept_between_two_and_ten(string? raw, int expected)
    {
        var handler = new ParallelSubAction();
        var parameters = raw is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["lanes"] = raw };
        var branches = handler.GetBranches(parameters);
        Assert.Equal(expected, branches.Count);
        Assert.Equal("Lane 1", branches[0]);
        Assert.Equal($"Lane {expected}", branches[^1]);
        Assert.Equal(["Lane 1", "Lane 2"], handler.Branches);
        Assert.Equal(ParallelSubAction.Id, handler.TypeId);
    }

    [Fact]
    public async Task Calling_the_plain_execute_is_refused()
    {
        await Assert.ThrowsAsync<NotSupportedException>(() =>
            new ParallelSubAction().ExecuteAsync(null!, new ParameterValues(new Dictionary<string, string>()), CancellationToken.None));
    }

    public void Dispose() => _h.Dispose();
}
