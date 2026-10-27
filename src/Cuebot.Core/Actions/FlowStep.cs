namespace Cuebot.Core.Actions;

/// <summary>
/// The running step handed to an <see cref="IFlowContainerHandler"/>: re-resolves its parameters (variables may
/// change between loop iterations) and runs its branches through the engine.
/// </summary>
public sealed class FlowStep
{
    private readonly ActionEngine _engine;
    private readonly ActionRunContext _context;

    internal FlowStep(ActionEngine engine, ActionRunContext context, ISubActionHandler handler, SubActionDefinition definition)
    {
        _engine = engine;
        _context = context;
        Handler = handler;
        Definition = definition;
    }

    public ISubActionHandler Handler { get; }
    public SubActionDefinition Definition { get; }

    /// <summary>Parameter values resolved against the current arguments and globals.</summary>
    public ParameterValues ResolveParameters() => ActionEngine.ResolveParameters(Handler, Definition, _context);

    /// <summary>The child steps of a branch (empty when it has none).</summary>
    public IReadOnlyList<SubActionDefinition> GetBranch(string name) =>
        Definition.Branches.TryGetValue(name, out var list) ? list : [];

    /// <summary>Runs a branch's enabled steps in order.</summary>
    public Task<FlowOutcome> RunBranchAsync(string name, CancellationToken ct) =>
        _engine.RunChainAsync(_context, GetBranch(name), ct);

    /// <summary>Runs ONE enabled child of a branch, picked by <see cref="SubActionDefinition.Weight"/>.</summary>
    public Task<FlowOutcome> RunRandomChildAsync(string name, CancellationToken ct) =>
        _engine.RunRandomAsync(_context, GetBranch(name), ct);

    /// <summary>Runs all enabled children of a branch at the same time (see <see cref="ActionDefinition.ConcurrentSubActions"/>).</summary>
    public Task<FlowOutcome> RunBranchConcurrentlyAsync(string name, CancellationToken ct) =>
        _engine.RunConcurrentlyAsync(_context, GetBranch(name), ct);
}
