using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Tests.SystemTools;

/// <summary>Runs sub-actions through a real <see cref="ActionEngine"/> and captures the resulting arguments.</summary>
internal sealed class SystemTestHarness : IDisposable
{
    private readonly ActionEngine _engine;
    private readonly Capture _capture = new();

    public SystemTestHarness(params ISubActionHandler[] handlers)
    {
        Directory.CreateDirectory(Folder);
        var profile = new ProfileService(null, Log);
        var registry = new Registry([.. handlers, _capture], []);
        _engine = new ActionEngine(new EventBus(Log), profile, registry, new GlobalVariables(null, Log), Log);
    }

    public string Folder { get; } = Path.Combine(Path.GetTempPath(), "cuebot-system-tests", Guid.NewGuid().ToString("N"));
    public AppLog Log { get; } = new();

    /// <summary>The arguments after the step ran; null when the action failed.</summary>
    public async Task<Dictionary<string, object?>?> RunAsync(string type, params (string Key, string Value)[] parameters) =>
        await RunAsync(type, null, parameters);

    public async Task<Dictionary<string, object?>?> RunAsync(string type, IReadOnlyDictionary<string, object?>? args,
        params (string Key, string Value)[] parameters)
    {
        var step = new SubActionDefinition { Type = type };
        foreach (var (key, value) in parameters) step.Parameters[key] = value;
        step.Branch("True").Add(new SubActionDefinition { Type = Capture.DefaultId, Parameters = { ["branch"] = "True" } });
        step.Branch("False").Add(new SubActionDefinition { Type = Capture.DefaultId, Parameters = { ["branch"] = "False" } });
        var action = new ActionDefinition { Name = "Test", SubActions = [step, new SubActionDefinition { Type = Capture.DefaultId }] };
        _capture.Args = null;
        return await _engine.RunAsync(action, args) ? _capture.Args : null;
    }

    public string PathOf(string name) => Path.Combine(Folder, name);

    public void Dispose()
    {
        Log.Dispose();
        try { Directory.Delete(Folder, recursive: true); }
        catch (IOException) { }
    }

    private sealed class Capture(string id = Capture.DefaultId) : ISubActionHandler
    {
        public const string DefaultId = "test.capture";
        public Dictionary<string, object?>? Args { get; set; }
        public string TypeId => id;
        public string Category => "Test";
        public string DisplayName => id;
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [new("branch", "Branch")];

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
        {
            // The first capture (inside a branch) records which branch ran; the final one keeps that.
            if (p["branch"].Length > 0) context.Args["branchTaken"] = p["branch"];
            Args = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(SubActionResult.Next);
        }
    }
}
