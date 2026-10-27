using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Kick.Tests.Support;

/// <summary>A real action engine that runs one sub-action and captures the arguments it left behind.</summary>
internal sealed class ActionHarness : IDisposable
{
    private readonly ProfileService _profile;
    private readonly GlobalVariables _globals;
    private readonly ActionEngine _engine;
    private readonly CaptureSubAction _capture = new();
    private ActionRunRecord? _last;

    public ActionHarness(AppLog log, IEnumerable<ISubActionHandler> handlers)
    {
        _profile = new ProfileService(null, log);
        _globals = new GlobalVariables(null, log);
        _engine = new ActionEngine(new EventBus(log), _profile, new Registry([.. handlers, _capture], []), _globals, log);
        _engine.RunCompleted += r => _last = r;
    }

    /// <summary>Runs <paramref name="type"/> (then, on its True branch or after it, the capture step).</summary>
    public async Task<(ActionRunRecord Run, Dictionary<string, object?>? Args)> RunAsync(string type, Dictionary<string, string> parameters,
        Dictionary<string, object?>? args = null, string? branch = null)
    {
        var step = new SubActionDefinition { Type = type };
        foreach (var (k, v) in parameters) step.Parameters[k] = v;
        if (branch is not null) step.Branches[branch] = [new SubActionDefinition { Type = CaptureSubAction.Id }];
        var action = new ActionDefinition { Name = "test " + type };
        action.SubActions.Add(step);
        if (branch is null) action.SubActions.Add(new SubActionDefinition { Type = CaptureSubAction.Id });
        _capture.Args = null;
        await _engine.RunAsync(action, args);
        return (_last!, _capture.Args);
    }

    public void Dispose()
    {
        _engine.Dispose();
        _profile.Dispose();
        _globals.Dispose();
    }

    private sealed class CaptureSubAction : ISubActionHandler
    {
        public const string Id = "test.capture";
        public string TypeId => Id;
        public string Category => "Test";
        public string DisplayName => "Capture";
        public IReadOnlyList<ParameterDescriptor> Parameters => [];
        public Dictionary<string, object?>? Args { get; set; }

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
        {
            Args = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(SubActionResult.Next);
        }
    }
}
