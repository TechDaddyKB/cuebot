using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;

namespace Cuebot.Scripting.Tests;

/// <summary>A real action engine wired with the scripting sub-actions under test and a fake script function provider.</summary>
internal sealed class TestBot : IDisposable
{
    public TestBot(Func<ScriptApi, AppLog, IEnumerable<ISubActionHandler>> handlers)
    {
        Profile = new ProfileService(null, Log);
        Globals = new GlobalVariables(null, Log);
        var api = new ScriptApi([new CoreScriptFunctions(), Functions]);
        Engine = new ActionEngine(new EventBus(Log), Profile, new Registry([.. handlers(api, Log), Capture], []), Globals, Log);
        Engine.RunCompleted += record => LastRun = record;
    }

    public AppLog Log { get; } = new();
    public GlobalVariables Globals { get; }
    public ProfileService Profile { get; }
    public ActionEngine Engine { get; }
    public FakeFunctions Functions { get; } = new();
    public CaptureSubAction Capture { get; } = new();
    public ActionRunRecord? LastRun { get; private set; }

    /// <summary>Adds an action whose steps are (type, code, timeoutSeconds), followed by a capture step.</summary>
    public ActionDefinition AddAction(string name, string type, string code, double timeoutSeconds = 30)
    {
        var action = new ActionDefinition { Name = name };
        action.SubActions.Add(new SubActionDefinition
        {
            Type = type,
            Parameters = { ["code"] = code, ["timeoutSeconds"] = timeoutSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture) },
        });
        action.SubActions.Add(new SubActionDefinition { Type = CaptureSubAction.Id });
        Profile.Profile.Actions.Add(action);
        Profile.NotifyChanged();
        return action;
    }

    /// <summary>Runs the action; returns the run record (Error is the failure message).</summary>
    public async Task<ActionRunRecord> RunAsync(ActionDefinition action, Dictionary<string, object?>? args = null)
    {
        Capture.Reset();
        await Engine.RunAsync(action, args);
        return LastRun!;
    }

    public void Dispose()
    {
        Engine.Dispose();
        Profile.Dispose();
        Globals.Dispose();
        Log.Dispose();
    }
}

/// <summary>Records the arguments it sees, proving the action continued to this step.</summary>
internal sealed class CaptureSubAction : ISubActionHandler
{
    public const string Id = "test.capture";
    public string TypeId => Id;
    public string Category => "Test";
    public string DisplayName => "Capture";
    public IReadOnlyList<ParameterDescriptor> Parameters => [];
    public Dictionary<string, object?>? Args { get; private set; }

    public void Reset() => Args = null;

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        Args = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>A module-like script function provider: "test.add" sums a and b.</summary>
internal sealed class FakeFunctions : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("test.add", "Adds two numbers", ["a", "b"], (_, args, _) =>
            Task.FromResult<object?>(args.GetProperty("a").GetInt32() + args.GetProperty("b").GetInt32())),
        new("test.echo", "Returns its arguments", [], (_, args, _) => Task.FromResult<object?>(args.Clone())),
    ];
}
