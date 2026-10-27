using Cuebot.Core.Actions;
using static Cuebot.Tests.Engine.EngineHarness;

namespace Cuebot.Tests.Engine;

public class HistoryTests : IDisposable
{
    private readonly EngineHarness _h = new(historyCapacity: 3);

    [Fact]
    public async Task Completed_run_records_queue_times_and_variable_snapshots()
    {
        _h.AddQueue("Q", blocking: false);
        var action = _h.Add("A", x => x.Queue = "Q", SetArg("added", "yes"), Record("x"));
        var changes = new List<(ActionHistoryChange, ActionRunState?)>();
        _h.History.Changed += (c, e) => changes.Add((c, e?.State));

        Assert.True(await _h.Run(action, new Dictionary<string, object?> { ["user"] = "Alice" }));

        var entry = Assert.Single(_h.History.Entries);
        Assert.Equal(ActionRunState.Completed, entry.State);
        Assert.Equal("A", entry.ActionName);
        Assert.Equal(action.Id, entry.ActionId);
        Assert.Equal("Q", entry.Queue);
        Assert.Equal("Manual", entry.Source);
        Assert.True(entry.QueuedAt <= entry.StartedAt && entry.StartedAt <= entry.CompletedAt);
        Assert.NotNull(entry.Duration);
        Assert.Equal("Alice", entry.ArgsWhenQueued["user"]);
        Assert.False(entry.ArgsWhenQueued.ContainsKey("added"));
        Assert.Equal("yes", entry.ArgsAfterRun!["added"]);
        Assert.Equal(entry.RunId.ToString(), entry.ArgsWhenQueued["runId"]);
        Assert.Equal(
            [(ActionHistoryChange.Added, ActionRunState.Pending), (ActionHistoryChange.Updated, ActionRunState.Running),
             (ActionHistoryChange.Updated, ActionRunState.Completed)],
            changes);
    }

    [Fact]
    public async Task Failed_runs_keep_the_error_and_run_completed_still_fires()
    {
        var action = _h.Add("Broken", Throw());
        Assert.False(await _h.Run(action));
        var entry = _h.History.Entries.Single();
        Assert.Equal(ActionRunState.Failed, entry.State);
        Assert.Equal("Throw: boom", entry.Error);

        var record = Assert.Single(_h.Completed);
        Assert.False(record.Success);
        Assert.Equal(entry.RunId, record.RunId);
        Assert.Equal(ActionRunState.Failed, record.State);
        Assert.Equal(QueueService.DefaultQueue, record.Queue);
    }

    [Fact]
    public async Task History_is_bounded_but_never_drops_pending_runs()
    {
        var action = _h.Add("A", Record("x"));
        var held = _h.Add("Held", x => x.Queue = "Paused", Record("held"));
        _h.Queues.Pause("Paused");
        var pending = _h.Run(held);
        var removed = 0;
        _h.History.Changed += (c, _) => { if (c == ActionHistoryChange.Removed) removed++; };

        for (var i = 0; i < 5; i++) await _h.Run(action);

        Assert.Equal(4, _h.History.Entries.Count); // 3 finished + 1 pending
        Assert.Equal(2, removed);
        Assert.Single(_h.History.Pending);
        _h.History.Clear();
        Assert.Single(_h.History.Entries);

        _h.Queues.Resume("Paused");
        await pending;
    }

    [Fact]
    public async Task Requeue_replays_the_original_arguments()
    {
        var action = _h.Add("A", SetArg("user", "changed"), Record("%user% %actionName%"));
        await _h.Run(action, new Dictionary<string, object?> { ["user"] = "Alice" });
        var first = _h.History.Entries.Single();

        Assert.True(await _h.Engine.RequeueAsync(first.RunId));
        var replay = _h.History.Entries.First();
        Assert.NotEqual(first.RunId, replay.RunId);
        Assert.Equal("Requeue", replay.Source);
        Assert.Equal("Alice", replay.ArgsWhenQueued["user"]);
        Assert.Equal(replay.RunId.ToString(), replay.ArgsWhenQueued["runId"]);
        Assert.Equal(["changed A", "changed A"], _h.Calls);
    }

    [Fact]
    public async Task Requeue_of_an_unknown_run_or_deleted_action_fails()
    {
        Assert.False(await _h.Engine.RequeueAsync(Guid.NewGuid()));

        var action = _h.Add("Gone", Record("x"));
        await _h.Run(action);
        _h.Profile.Profile.Actions.Clear();
        _h.Profile.NotifyChanged();
        Assert.False(await _h.Engine.RequeueAsync(_h.History.Entries.Single().RunId));
    }

    [Fact]
    public async Task Nested_runs_are_recorded_with_their_depth()
    {
        _h.Add("Child", Record("child"));
        var parent = _h.Add("Parent", Step("core.runAction", ("action", "Child")));
        await _h.Run(parent);
        var child = _h.History.Entries.Single(e => e.ActionName == "Child");
        Assert.Equal(1, child.Depth);
        Assert.Equal(ActionRunState.Completed, child.State);
        Assert.Equal(0, _h.History.Entries.Single(e => e.ActionName == "Parent").Depth);
    }

    public void Dispose() => _h.Dispose();
}
