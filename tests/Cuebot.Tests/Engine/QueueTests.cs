using Cuebot.Core.Actions;
using static Cuebot.Tests.Engine.EngineHarness;

namespace Cuebot.Tests.Engine;

public class QueueTests : IDisposable
{
    private readonly EngineHarness _h = new();

    private static Dictionary<string, object?> Args(string gate, string label = "") =>
        new() { ["g"] = gate, ["label"] = label == "" ? gate : label };

    [Fact]
    public async Task Blocking_queue_runs_actions_one_at_a_time_in_arrival_order()
    {
        _h.AddQueue("Alerts", blocking: true);
        var a = _h.Add("A", x => x.Queue = "Alerts", Gate("%g%"), Record("A %label%"));
        var b = _h.Add("B", x => { x.Queue = "alerts"; x.Concurrent = true; }, Gate("%g%"), Record("B %label%"));

        var first = _h.Run(a, Args("1"));
        var second = _h.Run(b, Args("2"));
        var third = _h.Run(a, Args("3"));
        await Until(() => _h.Gates.Inside == 1);
        Assert.Equal(2, _h.Queues.GetPendingCount("Alerts"));

        // Opening later gates first must not let later runs overtake.
        _h.Gates.Open("3");
        _h.Gates.Open("2");
        await Task.Delay(30);
        Assert.Empty(_h.Calls);

        _h.Gates.Open("1");
        Assert.All(await Task.WhenAll(first, second, third), Assert.True);
        Assert.Equal(["A 1", "B 2", "A 3"], _h.Calls);
        Assert.Equal(1, _h.Gates.MaxInside);
        Assert.Equal(3, _h.Queues.GetState("Alerts").Completed);
    }

    [Fact]
    public async Task Non_blocking_named_queue_lets_runs_overlap_even_for_the_same_action()
    {
        _h.AddQueue("Free", blocking: false);
        var a = _h.Add("A", x => x.Queue = "Free", Gate("%g%"));

        var runs = new[] { _h.Run(a, Args("1")), _h.Run(a, Args("2")) };
        await Until(() => _h.Gates.Inside == 2, "both runs inside");
        _h.Gates.ReleaseAll();
        await Task.WhenAll(runs);
    }

    [Fact]
    public async Task Legacy_actions_without_a_queue_keep_per_action_serialization()
    {
        var serial = _h.Add("Serial", Gate("%g%"));
        var concurrent = _h.Add("Concurrent", x => x.Concurrent = true, Gate("%g%"));

        var s1 = _h.Run(serial, Args("s1"));
        var s2 = _h.Run(serial, Args("s2"));
        var c1 = _h.Run(concurrent, Args("c1"));
        var c2 = _h.Run(concurrent, Args("c2"));

        // s1, c1 and c2 run; s2 waits for s1. Different actions never wait for each other.
        await Until(() => _h.Gates.Inside == 3, "three runs inside");
        await Task.Delay(30);
        Assert.Equal(3, _h.Gates.Inside);
        Assert.Equal(1, _h.Queues.GetPendingCount(QueueService.DefaultQueue));

        _h.Gates.ReleaseAll();
        await Task.WhenAll(s1, s2, c1, c2);
    }

    [Fact]
    public async Task Paused_queue_holds_runs_until_resumed_and_reports_pending()
    {
        _h.AddQueue("Q", blocking: false);
        var a = _h.Add("A", x => x.Queue = "Q", Record("ran"));
        _h.Queues.Pause("q");
        Assert.True(_h.Queues.IsPaused("Q"));

        var run = _h.Run(a);
        await Task.Delay(30);
        Assert.Empty(_h.Calls);
        Assert.Equal(1, _h.Queues.GetPendingCount("Q"));
        var pending = Assert.Single(_h.History.Pending);
        Assert.Equal(ActionRunState.Pending, pending.State);
        Assert.Equal("Q", pending.Queue);

        _h.Queues.Resume("Q");
        Assert.True(await run);
        Assert.Equal(["ran"], _h.Calls);
        Assert.Empty(_h.History.Pending);
    }

    [Fact]
    public async Task Always_run_actions_ignore_a_paused_queue()
    {
        _h.AddQueue("Q", blocking: true);
        var normal = _h.Add("Normal", x => x.Queue = "Q", Record("normal"));
        var always = _h.Add("Always", x => { x.Queue = "Q"; x.AlwaysRun = true; }, Record("always"));
        _h.Queues.Pause("Q");

        var held = _h.Run(normal);
        Assert.True(await _h.Run(always).WaitAsync(TestTimeout));
        Assert.Equal(["always"], _h.Calls);
        Assert.False(held.IsCompleted);

        _h.Queues.Resume("Q");
        await held;
        Assert.Equal(["always", "normal"], _h.Calls);
    }

    [Fact]
    public async Task Pausing_default_queue_holds_actions_without_a_queue()
    {
        var a = _h.Add("A", Record("ran"));
        _h.Queues.Pause(null);
        var run = _h.Run(a);
        await Task.Delay(30);
        Assert.Equal(1, _h.Queues.GetPendingCount(""));
        _h.Queues.Resume(QueueService.DefaultQueue);
        Assert.True(await run);
    }

    [Fact]
    public async Task Pause_all_and_resume_all()
    {
        _h.AddQueue("Q1", blocking: false);
        _h.AddQueue("Q2", blocking: true);
        var a = _h.Add("A", x => x.Queue = "Q1", Record("a"));
        var b = _h.Add("B", x => x.Queue = "Q2", Record("b"));
        var c = _h.Add("C", Record("c"));

        _h.Queues.PauseAll();
        Assert.All(_h.Queues.Queues, q => Assert.True(q.Paused));
        var runs = new[] { _h.Run(a), _h.Run(b), _h.Run(c) };
        await Task.Delay(30);
        Assert.Empty(_h.Calls);

        _h.Queues.ResumeAll();
        await Task.WhenAll(runs);
        Assert.Equal(["a", "b", "c"], _h.Calls.Order());
    }

    [Fact]
    public async Task Clear_cancels_pending_runs_but_not_the_running_one()
    {
        _h.AddQueue("Q", blocking: true);
        var a = _h.Add("A", x => x.Queue = "Q", Gate("%g%"), Record("%g%"));
        var running = _h.Run(a, Args("1"));
        await Until(() => _h.Gates.Inside == 1);
        var p1 = _h.Run(a, Args("2"));
        var p2 = _h.Run(a, Args("3"));
        await Until(() => _h.Queues.GetPendingCount("Q") == 2);

        Assert.Equal(2, _h.Queues.Clear("Q"));
        Assert.False(await p1);
        Assert.False(await p2);
        _h.Gates.ReleaseAll();
        Assert.True(await running);

        Assert.Equal(["1"], _h.Calls);
        Assert.Equal(2, _h.History.Entries.Count(e => e.State == ActionRunState.Cancelled));
        Assert.Equal(0, _h.Queues.GetPendingCount("Q"));
    }

    [Fact]
    public async Task Cancelling_a_pending_run_removes_it_from_the_queue()
    {
        var a = _h.Add("A", x => x.Queue = "Q", Record("ran"));
        _h.Queues.Pause("Q");
        using var cts = new CancellationTokenSource();
        var run = _h.Run(a, ct: cts.Token);
        await Until(() => _h.Queues.GetPendingCount("Q") == 1);

        await cts.CancelAsync();
        Assert.False(await run);
        Assert.Equal(0, _h.Queues.GetPendingCount("Q"));
        _h.Queues.Resume("Q");
        Assert.Empty(_h.Calls);
    }

    [Fact]
    public async Task Turning_blocking_off_releases_waiting_runs()
    {
        _h.AddQueue("Q", blocking: true);
        var a = _h.Add("A", x => x.Queue = "Q", Gate("%g%"));
        var r1 = _h.Run(a, Args("1"));
        var r2 = _h.Run(a, Args("2"));
        await Until(() => _h.Gates.Inside == 1);

        _h.Profile.Profile.Queues.Single().Blocking = false;
        _h.Profile.NotifyChanged();
        await Until(() => _h.Gates.Inside == 2, "second run released");
        _h.Gates.ReleaseAll();
        await Task.WhenAll(r1, r2);
    }

    [Fact]
    public async Task Nested_run_action_does_not_deadlock_a_blocking_queue()
    {
        _h.AddQueue("Q", blocking: true);
        _h.Add("Child", x => x.Queue = "Q", Record("child"));
        var parent = _h.Add("Parent", x => x.Queue = "Q", Step("core.runAction", ("action", "Child")), Record("parent"));

        Assert.True(await _h.Run(parent).WaitAsync(TestTimeout));
        Assert.Equal(["child", "parent"], _h.Calls);
    }

    [Fact]
    public void Queue_list_has_default_first_then_profile_queues()
    {
        _h.AddQueue("Alerts", blocking: true);
        var queues = _h.Queues.Queues;
        Assert.Equal([QueueService.DefaultQueue, "Alerts"], queues.Select(q => q.Name));
        Assert.True(queues[0].IsDefault);
        Assert.False(queues[0].Blocking);
        Assert.True(queues[1].Blocking);
        Assert.True(_h.Queues.IsBlocking("ALERTS"));
    }

    [Fact]
    public async Task Changed_is_raised_for_queue_activity()
    {
        var changes = 0;
        _h.Queues.Changed += () => Interlocked.Increment(ref changes);
        _h.Queues.Pause("Q");
        _h.Queues.Resume("Q");
        await _h.Run(_h.Add("A", Record("x")));
        Assert.True(changes >= 3);
    }

    public void Dispose() => _h.Dispose();
}
