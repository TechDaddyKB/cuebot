using System.Text.Json;
using Cuebot.Core.Scripting;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Rahituber;

/// <summary>RahiTuber functions for user scripts (C# and Python).</summary>
internal sealed class RahituberScriptFunctions(RahituberService rahi) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("rahituber.press", "Press a state like its hotkey (by name, or its place counted from 0)", ["state"], async (_, a, ct) =>
        {
            await rahi.SetStateAsync(a.GetString("state"), true, ct).ConfigureAwait(false);
            return null;
        }),
        new("rahituber.release", "Let go of a \"while held\" state", ["state"], async (_, a, ct) =>
        {
            await rahi.SetStateAsync(a.GetString("state"), false, ct).ConfigureAwait(false);
            return null;
        }),
        new("rahituber.hold", "Hold a \"while held\" state for some seconds (default 3), then let go", ["state", "seconds"], async (_, a, ct) =>
        {
            var seconds = a.GetElement("seconds") switch
            {
                { ValueKind: JsonValueKind.Number } n => n.GetDouble(),
                { ValueKind: JsonValueKind.String } s => Steps.OptionalNumber(s.GetString() ?? "", "seconds") ?? 3,
                _ => 3,
            };
            await rahi.HoldStateAsync(a.GetString("state"), TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 86_400)), ct).ConfigureAwait(false);
            return null;
        }),
        new("rahituber.getStates", "The loaded avatar's states, when RahiTuber's folder is set: index, name, type, enabled, condition, timeout",
            [], (_, _, _) => Task.FromResult<object?>(rahi.Avatar?.States.Select(s => new
            {
                index = s.Index,
                name = s.Name,
                type = s.Type.ToString(),
                enabled = s.Enabled,
                condition = s.Condition.ToString(),
                timeout = s.Timeout,
            }).ToArray() ?? [])),
        new("rahituber.check", "Checks that RahiTuber (not another program) answers; returns what was found", [], async (_, _, ct) =>
            await rahi.TestAsync(ct).ConfigureAwait(false)),
    ];
}
