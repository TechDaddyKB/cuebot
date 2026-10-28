using System.Text.Json;
using Cuebot.Core.Scripting;

namespace Cuebot.Controllers;

/// <summary>Game-controller functions for user scripts (all Cuebot extras).</summary>
public sealed class ControllerScriptFunctions(ControllerService controllers) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("controllers.status", "{running, backend, version, state, message, mode}", [],
            (_, _, _) => Task.FromResult<object?>(new Dictionary<string, object?>
            {
                ["running"] = controllers.IsRunning,
                ["backend"] = controllers.BackendName,
                ["version"] = controllers.BackendVersion,
                ["state"] = controllers.State.ToString(),
                ["message"] = controllers.StatusMessage,
                ["mode"] = controllers.Settings.Mode.ToString(),
            })),
        new("controllers.list", "Connected controllers with identity, battery, held buttons, axes and hats (starts watching if needed)", [],
            async (_, _, ct) =>
            {
                await Ensure(ct).ConfigureAwait(false);
                return controllers.Controllers.Select(c => c.Describe()).ToArray();
            }),
        new("controllers.get", "One controller (#slot, name, type or GUID), or null", ["controller"], async (_, a, ct) =>
        {
            await Ensure(ct).ConfigureAwait(false);
            return controllers.Resolve(a.GetString("controller", "#1")).FirstOrDefault()?.Describe();
        }),
        new("controllers.rumble", "Rumble: low/high motor 0-100 %, durationMs; returns how many controllers rumbled",
            ["controller", "low", "high", "durationMs"], async (_, a, ct) =>
            {
                var done = await Call(() => controllers.RumbleAsync(a.GetString("controller", "Any"), Num(a, "low", 50), Num(a, "high", 50),
                    (int)Num(a, "durationMs", 500), ct)).ConfigureAwait(false);
                return done.Count;
            }),
        new("controllers.setLed", "Set the light colour (#RRGGBB or r,g,b); returns how many controllers changed", ["controller", "color"],
            async (_, a, ct) =>
            {
                var done = await Call(() =>
                {
                    var (r, g, b) = ControllerService.ParseColor(a.GetString("color"));
                    return controllers.SetLedAsync(a.GetString("controller", "Any"), r, g, b, ct);
                }).ConfigureAwait(false);
                return done.Count;
            }),
        new("controllers.learn", "Wait (timeoutMs, default 10000) for the next button, axis or hat; returns {kind, name, label, controller, "
                                 + "triggerType, settings} or null", ["timeoutMs", "controller"], async (_, a, ct) =>
        {
            var timeout = TimeSpan.FromMilliseconds(Math.Clamp(Num(a, "timeoutMs", 10_000), 100, 300_000));
            var input = await Call(() => controllers.LearnAsync(timeout, a.GetString("controller"), ct)).ConfigureAwait(false);
            return input is null
                ? null
                : new Dictionary<string, object?>
                {
                    ["kind"] = input.Kind, ["name"] = input.Name, ["label"] = input.Label, ["controller"] = input.Controller.Describe(),
                    ["triggerType"] = input.TriggerType, ["settings"] = input.TriggerSettings, ["description"] = input.Description,
                };
        }),
        new("controllers.reloadMappings", "Load the gamecontrollerdb mappings file again; returns how many mappings were added", [],
            async (_, _, ct) => await Call(() => controllers.ReloadMappingsAsync(ct)).ConfigureAwait(false)),
    ];

    private async Task Ensure(CancellationToken ct) => await Call(async () =>
    {
        await controllers.EnsureRunningAsync(ct).ConfigureAwait(false);
        return true;
    }).ConfigureAwait(false);

    private static async Task<T> Call<T>(Func<Task<T>> call)
    {
        try { return await call().ConfigureAwait(false); }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { throw new ScriptException(ex.Message); }
    }

    private static double Num(JsonElement args, string name, double fallback) =>
        args.GetElement(name) is { } v
            ? v.ValueKind == JsonValueKind.Number ? v.GetDouble()
            : double.TryParse(v.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d
            : fallback
            : fallback;
}
