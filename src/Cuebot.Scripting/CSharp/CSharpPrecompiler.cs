using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;

namespace Cuebot.Scripting;

/// <summary>
/// "Precompile on Application Start": at startup, compiles every "Execute C# code" step that asks for it, so its
/// first run is not delayed and a CPHInline class's Init() runs straight away (Streamer.bot runs it at startup too).
/// Steps with "Delayed start" are compiled once Cuebot has finished starting (<see cref="CoreEvents.AppStarted"/>).
/// Only enabled steps of enabled actions are compiled: imported code stays untouched until the user enables it.
/// Compile errors are logged; the step then reports them again when it runs.
/// </summary>
public sealed class CSharpPrecompiler(CSharpScriptEngine engine, ProfileService profile, EventBus bus, AppLog log)
{
    private int _started;

    /// <summary>Compiles the non-delayed steps in the background and schedules the delayed ones. Never throws.</summary>
    public Task StartAsync()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return Task.CompletedTask;
        IDisposable? subscription = null;
        subscription = bus.Subscribe(e =>
        {
            if (e.Type != CoreEvents.AppStarted) return;
            subscription?.Dispose();
            _ = Task.Run(() => Compile(delayed: true));
        });
        return Task.Run(() => Compile(delayed: false));
    }

    /// <summary>The steps to precompile: enabled, in enabled actions, with the precompile option on.</summary>
    internal static IEnumerable<(ActionDefinition Action, SubActionDefinition Step)> Steps(IEnumerable<ActionDefinition> actions, bool delayed) =>
        actions.Where(a => a.Enabled)
            .SelectMany(a => Enabled(a.SubActions).Select(s => (a, s)))
            .Where(x => x.s.Type == CSharpCodeSubAction.Id && Flag(x.s, CSharpCodeSubAction.PrecompileParameter)
                        && Flag(x.s, CSharpCodeSubAction.DelayedStartParameter) == delayed
                        && x.s.Parameters.GetValueOrDefault("code") is { Length: > 0 });

    private void Compile(bool delayed)
    {
        foreach (var (action, step) in Steps(profile.Snapshot.Actions, delayed).ToList())
        {
            try
            {
                engine.Precompile(step.Parameters["code"], CSharpCodeSubAction.References(step));
                log.Info("C#", $"Precompiled the C# code of '{action.Name}'");
            }
            catch (ScriptException ex)
            {
                log.Warn("C#", $"Precompiling the C# code of '{action.Name}' failed: {ex.Message}");
            }
            catch (Exception ex)
            {
                log.Error("C#", $"The C# code of '{action.Name}' failed in Init()", ex);
            }
        }
    }

    private static IEnumerable<SubActionDefinition> Enabled(IEnumerable<SubActionDefinition> steps)
    {
        foreach (var step in steps.Where(s => s.Enabled))
        {
            yield return step;
            foreach (var nested in Enabled(step.Branches.Values.SelectMany(b => b))) yield return nested;
        }
    }

    private static bool Flag(SubActionDefinition step, string key) =>
        bool.TryParse(step.Parameters.GetValueOrDefault(key), out var on) && on;
}
