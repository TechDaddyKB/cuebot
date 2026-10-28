using System.Collections.Concurrent;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Tests.Engine;

/// <summary>A real engine with every engine-owned sub-action plus test steps that record, wait and fail.</summary>
internal sealed class EngineHarness : IDisposable
{
    public static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    public EngineHarness(IRandomSource? random = null, int historyCapacity = ActionHistory.DefaultCapacity)
    {
        Bus = new EventBus(Log);
        Profile = new ProfileService(null, Log);
        Globals = new GlobalVariables(null, Log);
        Random = random ?? new SequenceRandom(0);
        Queues = new QueueService(Profile);
        History = new ActionHistory(historyCapacity);
        Registry = new Registry(
        [
            Recorder, new GateSubAction(Gates), new ThrowSubAction(),
            new DelaySubAction(), new LogSubAction(), new CommentSubAction(), new GroupSubAction(), new IfSubAction(),
            new SwitchSubAction(), new WhileSubAction(), new LoopBreakSubAction(), new LoopContinueSubAction(),
            new GetInputSubAction(), new StopSubAction(), new RunActionSubAction(Profile), new SetArgumentSubAction(),
            new SendSignalSubAction(Signals), new WaitForSignalSubAction(Signals),
            new SetActionStateSubAction(Profile), new GetActionStateSubAction(Profile),
            new GetInputDialogSubAction(Prompts), new ClearActionQueueSubAction(Queues, Profile),
            new SetActionQueueBlockingSubAction(Profile), new SetActionQueuePauseSubAction(Queues, Profile),
            new SetTriggerStateSubAction(Profile), new SetSubActionStateSubAction(Profile), new SetSubActionWeightSubAction(Profile),
            new GetActionGroupStateSubAction(Profile), new SetActionGroupStateSubAction(Profile), new TriggerCustomEventSubAction(Bus),
            new RandomNumberSubAction(),
        ],
        [
            new EventTriggerType("test.range", "Test", "Range event", "test.range",
                [new ParameterDescriptor("minAmount", "Min", ParameterKind.Number), new ParameterDescriptor("maxAmount", "Max", ParameterKind.Number)],
                (e, s, _) => RangeMatches(e, s)),
            new EventTriggerType("custom.codeEvent", "Test", "Custom code event", "custom.codeEvent",
                [new ParameterDescriptor("eventName", "Event")], (e, s, _) => Equals(e.Args.GetValueOrDefault("eventName"), s["eventName"])),
            new EventTriggerType("test.event", "Test", "Test event", "test.event",
                [new ParameterDescriptor("match", "Match")],
                (e, s, _) => s["match"] == "" || Equals(e.Args.GetValueOrDefault("value"), s["match"])),
        ]);
        Engine = new ActionEngine(Bus, Profile, Registry, Globals, Log, Queues, History, Random);
        Engine.RunCompleted += r => Completed.Enqueue(r);
    }

    public AppLog Log { get; } = new();
    public EventBus Bus { get; }
    public UserPromptService Prompts { get; } = new();
    public ProfileService Profile { get; }
    public GlobalVariables Globals { get; }
    public SignalService Signals { get; } = new();
    public IRandomSource Random { get; }
    public QueueService Queues { get; }
    public ActionHistory History { get; }
    public Registry Registry { get; }
    public ActionEngine Engine { get; }
    public RecordSubAction Recorder { get; } = new();
    public Gates Gates { get; } = new();
    public ConcurrentQueue<ActionRunRecord> Completed { get; } = new();

    public IReadOnlyList<string> Calls => Recorder.Snapshot();

    public ActionDefinition Add(string name, params SubActionDefinition[] steps) => Add(name, null, steps);

    public ActionDefinition Add(string name, Action<ActionDefinition>? configure, params SubActionDefinition[] steps)
    {
        var action = new ActionDefinition { Name = name, SubActions = [.. steps] };
        action.Triggers.Add(new TriggerDefinition { Type = "test.event" });
        configure?.Invoke(action);
        Profile.Profile.Actions.Add(action);
        Profile.NotifyChanged();
        return Profile.Snapshot.FindAction(action.Id)!;
    }

    public void AddQueue(string name, bool blocking)
    {
        Profile.Profile.Queues.Add(new QueueDefinition { Name = name, Blocking = blocking });
        Profile.NotifyChanged();
    }

    public Task<bool> Run(ActionDefinition action, IReadOnlyDictionary<string, object?>? args = null, CancellationToken ct = default) =>
        Engine.RunAsync(action, args, ct);

    public static SubActionDefinition Step(string type, params (string Key, string Value)[] parameters)
    {
        var step = new SubActionDefinition { Type = type };
        foreach (var (k, v) in parameters) step.Parameters[k] = v;
        return step;
    }

    public static SubActionDefinition Record(string text, double weight = 1)
    {
        var step = Step(RecordSubAction.Id, ("text", text));
        step.Weight = weight;
        return step;
    }

    /// <summary>"test.range": amount between minAmount (blank = none) and maxAmount (blank/0 = none).</summary>
    private static bool RangeMatches(BotEvent e, ParameterValues s)
    {
        var amount = Convert.ToDouble(e.Args.GetValueOrDefault("amount") ?? 0, System.Globalization.CultureInfo.InvariantCulture);
        var min = s.GetDouble("minAmount", double.NegativeInfinity);
        var max = s.GetDouble("maxAmount", 0);
        return amount >= min && (max <= 0 || amount <= max);
    }

    public ActionDefinition AddWithTrigger(string name, TriggerDefinition trigger, params SubActionDefinition[] steps)
    {
        var action = new ActionDefinition { Name = name, SubActions = [.. steps] };
        action.Triggers.Add(trigger);
        Profile.Profile.Actions.Add(action);
        Profile.NotifyChanged();
        return Profile.Snapshot.FindAction(action.Id)!;
    }

    public static SubActionDefinition Gate(string name) => Step(GateSubAction.Id, ("gate", name));

    public static SubActionDefinition Throw() => Step(ThrowSubAction.Id);

    public static SubActionDefinition Stop() => Step("core.stop");

    public static SubActionDefinition Break() => Step("core.loopBreak");

    public static SubActionDefinition Continue() => Step("core.loopContinue");

    public static SubActionDefinition SetArg(string name, string value) =>
        Step("core.setArgument", ("name", name), ("value", value));

    public static SubActionDefinition Group(params SubActionDefinition[] children) => Group(false, children);

    public static SubActionDefinition Group(bool random, params SubActionDefinition[] children)
    {
        var group = Step(GroupSubAction.Id, ("random", random ? "true" : "false"), ("color", "#336699"));
        group.Branch(GroupSubAction.Steps).AddRange(children);
        return group;
    }

    public static SubActionDefinition If(string left, string op, string right,
        SubActionDefinition[]? whenTrue = null, SubActionDefinition[]? whenFalse = null)
    {
        var step = Step("core.if", ("left", left), ("operator", op), ("right", right));
        step.Branch(IfSubAction.True).AddRange(whenTrue ?? []);
        step.Branch(IfSubAction.False).AddRange(whenFalse ?? []);
        return step;
    }

    public static SubActionDefinition While(string left, string op, string right, params SubActionDefinition[] body)
    {
        var step = Step(WhileSubAction.Id, ("left", left), ("operator", op), ("right", right));
        step.Branch(WhileSubAction.Body).AddRange(body);
        return step;
    }

    /// <summary>Waits until <paramref name="condition"/> holds (polling), failing the test after <see cref="TestTimeout"/>.</summary>
    public static async Task Until(Func<bool> condition, string because = "condition")
    {
        var deadline = DateTime.UtcNow + TestTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {because}");
            await Task.Delay(5);
        }
    }

    public void Dispose()
    {
        Gates.ReleaseAll();
        Engine.Dispose();
        Queues.Dispose();
        Log.Dispose();
    }
}

/// <summary>Returns the given values in turn (repeating the last).</summary>
internal sealed class SequenceRandom(params double[] values) : IRandomSource
{
    private int _next;

    public double NextDouble()
    {
        var i = Math.Min(Interlocked.Increment(ref _next) - 1, values.Length - 1);
        return values[i];
    }
}

/// <summary>Named gates test steps can wait on; tracks how many runs are inside a gate at once.</summary>
internal sealed class Gates
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _gates = new(StringComparer.OrdinalIgnoreCase);
    private int _inside;
    private int _maxInside;

    public int Inside => Volatile.Read(ref _inside);
    public int MaxInside => Volatile.Read(ref _maxInside);

    private volatile bool _allOpen;

    public TaskCompletionSource Get(string name)
    {
        var gate = _gates.GetOrAdd(name, _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
        if (_allOpen) gate.TrySetResult();
        return gate;
    }

    public void Open(string name) => Get(name).TrySetResult();

    /// <summary>Opens every gate, including ones first used later.</summary>
    public void ReleaseAll()
    {
        _allOpen = true;
        foreach (var gate in _gates.Values) gate.TrySetResult();
    }

    public async Task PassAsync(string name, CancellationToken ct)
    {
        var now = Interlocked.Increment(ref _inside);
        int max;
        while (now > (max = Volatile.Read(ref _maxInside)) && Interlocked.CompareExchange(ref _maxInside, now, max) != max)
        {
        }
        try
        {
            await Get(name).Task.WaitAsync(ct);
        }
        finally
        {
            Interlocked.Decrement(ref _inside);
        }
    }
}

internal sealed class RecordSubAction : ISubActionHandler
{
    public const string Id = "test.record";
    private readonly List<string> _calls = [];
    public string TypeId => Id;
    public string Category => "Test";
    public string DisplayName => "Record";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [new("text", "Text")];

    public IReadOnlyList<string> Snapshot()
    {
        lock (_calls) return [.. _calls];
    }

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        lock (_calls) _calls.Add(p["text"]);
        return Task.FromResult(SubActionResult.Next);
    }
}

internal sealed class GateSubAction(Gates gates) : ISubActionHandler
{
    public const string Id = "test.gate";
    public string TypeId => Id;
    public string Category => "Test";
    public string DisplayName => "Gate";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [new("gate", "Gate")];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await gates.PassAsync(p["gate"], ct);
        return SubActionResult.Next;
    }
}

internal sealed class ThrowSubAction : ISubActionHandler
{
    public const string Id = "test.throw";
    public string TypeId => Id;
    public string Category => "Test";
    public string DisplayName => "Throw";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct) =>
        throw new InvalidOperationException("boom");
}

internal sealed class EngineTempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cuebot-engine-" + Guid.NewGuid().ToString("N"));

    public EngineTempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
