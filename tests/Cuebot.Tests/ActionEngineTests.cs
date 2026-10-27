using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Tests;

public class ActionEngineTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly ProfileService _profile;
    private readonly GlobalVariables _globals;
    private readonly RecordingSubAction _recorder = new();
    private readonly ActionEngine _engine;

    public ActionEngineTests()
    {
        _profile = new ProfileService(null, _log);
        _globals = new GlobalVariables(null, _log);
        var registry = new Registry(
            [_recorder, new IfSubAction(), new StopSubAction(), new SetArgumentSubAction(), new RunActionSubAction(_profile), new ThrowingSubAction()],
            [new EventTriggerType("test.event", "Test", "Test event", "test.event",
                [new ParameterDescriptor("match", "Match")],
                (e, s, args) =>
                {
                    args["fromTrigger"] = "yes";
                    return s["match"] == "" || Equals(e.Args.GetValueOrDefault("value"), s["match"]);
                })]);
        _engine = new ActionEngine(new EventBus(_log), _profile, registry, _globals, _log);
    }

    private ActionDefinition AddAction(string name, params SubActionDefinition[] steps)
    {
        var action = new ActionDefinition { Name = name, SubActions = [.. steps] };
        action.Triggers.Add(new TriggerDefinition { Type = "test.event" });
        _profile.Profile.Actions.Add(action);
        _profile.NotifyChanged();
        return action;
    }

    private static SubActionDefinition Record(string text) =>
        new() { Type = RecordingSubAction.Id, Parameters = { ["text"] = text } };

    private async Task<bool[]> DispatchAsync(object? value = null)
    {
        var e = new BotEvent("test.event", new Dictionary<string, object?> { ["value"] = value, ["user"] = "Alice" });
        return await Task.WhenAll(_engine.Dispatch(e));
    }

    [Fact]
    public async Task Runs_sub_actions_in_order_with_resolved_variables()
    {
        AddAction("Greet", Record("hello %user%"), Record("trigger %fromTrigger% via %triggerName%"));

        Assert.Equal(new[] { true }, await DispatchAsync());
        Assert.Equal(["hello Alice", "trigger yes via Test event"], _recorder.Calls);
    }

    [Fact]
    public async Task Skips_disabled_actions_triggers_and_steps()
    {
        var disabledStep = Record("skipped");
        disabledStep.Enabled = false;
        AddAction("A", disabledStep, Record("ran"));
        AddAction("B", Record("disabled action")).Enabled = false;
        var c = AddAction("C", Record("disabled trigger"));
        c.Triggers[0].Enabled = false;
        _profile.NotifyChanged();

        await DispatchAsync();
        Assert.Equal(["ran"], _recorder.Calls);
    }

    [Fact]
    public async Task Trigger_settings_filter_events()
    {
        var action = AddAction("Filtered", Record("matched"));
        action.Triggers[0].Settings["match"] = "yes";
        _profile.NotifyChanged();

        await DispatchAsync("no");
        await DispatchAsync("yes");
        Assert.Equal(["matched"], _recorder.Calls);
    }

    [Fact]
    public async Task If_runs_the_chosen_branch_then_continues()
    {
        var ifStep = new SubActionDefinition
        {
            Type = "core.if",
            Parameters = { ["left"] = "%user%", ["operator"] = "equals", ["right"] = "alice" },
        };
        ifStep.Branch(IfSubAction.True).Add(Record("is alice"));
        ifStep.Branch(IfSubAction.False).Add(Record("not alice"));
        AddAction("Branching", ifStep, Record("after"));

        await DispatchAsync();
        Assert.Equal(["is alice", "after"], _recorder.Calls);
    }

    [Fact]
    public async Task Stop_inside_a_branch_stops_the_whole_action()
    {
        var ifStep = new SubActionDefinition { Type = "core.if", Parameters = { ["left"] = "1", ["operator"] = "equals", ["right"] = "1" } };
        ifStep.Branch(IfSubAction.True).Add(new SubActionDefinition { Type = "core.stop" });
        AddAction("Stopper", ifStep, Record("never"));

        Assert.Equal(new[] { true }, await DispatchAsync());
        Assert.Empty(_recorder.Calls);
    }

    [Fact]
    public async Task Arguments_set_by_steps_are_visible_to_later_steps()
    {
        AddAction("Args",
            new SubActionDefinition { Type = "core.setArgument", Parameters = { ["name"] = "greeting", ["value"] = "hey %user%" } },
            Record("%greeting%!"));

        await DispatchAsync();
        Assert.Equal(["hey Alice!"], _recorder.Calls);
    }

    [Fact]
    public async Task Failing_step_fails_the_run_and_reports_it()
    {
        ActionRunRecord? record = null;
        _engine.RunCompleted += r => record = r;
        AddAction("Broken", new SubActionDefinition { Type = ThrowingSubAction.Id }, Record("never"));

        Assert.Equal(new[] { false }, await DispatchAsync());
        Assert.Empty(_recorder.Calls);
        Assert.False(record!.Success);
        Assert.Contains("boom", record.Error);
    }

    [Fact]
    public async Task Unknown_sub_action_type_fails_with_a_clear_message()
    {
        ActionRunRecord? record = null;
        _engine.RunCompleted += r => record = r;
        AddAction("Missing", new SubActionDefinition { Type = "plugin.gone" });

        await DispatchAsync();
        Assert.Contains("plugin.gone", record!.Error);
    }

    [Fact]
    public async Task Run_action_passes_arguments_and_guards_recursion()
    {
        var child = new ActionDefinition { Name = "Child", SubActions = [Record("child sees %user%")] };
        _profile.Profile.Actions.Add(child);
        AddAction("Parent", new SubActionDefinition { Type = "core.runAction", Parameters = { ["action"] = "child" } });

        await DispatchAsync();
        Assert.Equal(["child sees Alice"], _recorder.Calls);

        var loop = new ActionDefinition { Name = "Loop" };
        loop.SubActions.Add(new SubActionDefinition { Type = "core.runAction", Parameters = { ["action"] = "Loop" } });
        _profile.Profile.Actions.Add(loop);
        _profile.NotifyChanged();
        Assert.False(await _engine.RunAsync(loop));
    }

    [Fact]
    public async Task Non_concurrent_runs_are_queued()
    {
        var gate = new TaskCompletionSource();
        _recorder.Gate = gate.Task;
        var action = AddAction("Queued", Record("run"));

        var first = _engine.RunAsync(action);
        var second = _engine.RunAsync(action);
        await Task.Delay(50);
        Assert.Single(_recorder.Calls);

        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(2, _recorder.Calls.Count);
    }

    [Fact]
    public void Snapshot_is_isolated_from_live_edits()
    {
        var action = AddAction("Snap", Record("x"));
        action.Name = "Edited without notify";
        Assert.Equal("Snap", _profile.Snapshot.Actions.Single().Name);
    }

    public void Dispose()
    {
        _engine.Dispose();
        _log.Dispose();
    }

    private sealed class RecordingSubAction : ISubActionHandler
    {
        public const string Id = "test.record";
        public List<string> Calls { get; } = [];
        public Task Gate { get; set; } = Task.CompletedTask;
        public string TypeId => Id;
        public string Category => "Test";
        public string DisplayName => "Record";
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [new("text", "Text")];

        public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
        {
            lock (Calls) Calls.Add(p["text"]);
            await Gate;
            return SubActionResult.Next;
        }
    }

    private sealed class ThrowingSubAction : ISubActionHandler
    {
        public const string Id = "test.throw";
        public string TypeId => Id;
        public string Category => "Test";
        public string DisplayName => "Throw";
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
            throw new InvalidOperationException("boom");
    }
}
