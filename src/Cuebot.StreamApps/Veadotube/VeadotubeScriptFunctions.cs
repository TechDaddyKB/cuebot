using System.Text.Json;
using Cuebot.Core.Scripting;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Veadotube;

/// <summary>veadotube functions for user scripts (C# and Python).</summary>
internal sealed class VeadotubeScriptFunctions(VeadotubeService veadotube) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("veadotube.isConnected", "True while veadotube is connected", [], (_, _, _) => Task.FromResult<object?>(veadotube.IsConnected)),
        new("veadotube.changeState", "Change the avatar state (mode Set, Push, Pop, Toggle, Random or Clear), optionally for seconds; returns the state id",
            ["state", "mode", "seconds", "node", "instance"], async (_, a, ct) =>
                await veadotube.ChangeStateAsync(a.GetString("mode", "Set"), a.GetString("state"), a.GetString("node"), a.GetString("instance"),
                    Seconds(a), ct).ConfigureAwait(false)),
        new("veadotube.getState", "The current avatar state: state, stateId, states, image (data: URL)", ["node", "instance"], async (_, a, ct) =>
        {
            var (state, stateId, states, image) = await veadotube.GetStateAsync(a.GetString("node"), a.GetString("instance"), ct).ConfigureAwait(false);
            return new { state, stateId, states = states.Select(s => s.Name).ToArray(), image };
        }),
        new("veadotube.setPushToTalk", "Push-to-talk (or another on/off node) On, Off or Toggle, optionally for seconds; returns the value",
            ["state", "seconds", "node", "instance"], async (_, a, ct) =>
                await veadotube.SetSwitchAsync(Steps.ParseSwitch(a.GetString("state", "On")), a.GetString("node"), a.GetString("instance"), Seconds(a), ct)
                    .ConfigureAwait(false)),
        new("veadotube.setNumber", "Set (or add to) a Number node; min and max set its range; returns the new value", ["node", "value", "add", "min", "max", "instance"],
            async (_, a, ct) => (await veadotube.SetNumberAsync(a.GetString("node"), Num(a, "value") ?? throw new ArgumentException("value must be a number"),
                a.GetBool("add"), Num(a, "min"), Num(a, "max"), a.GetString("instance"), ct).ConfigureAwait(false))?.Value),
        new("veadotube.getNumber", "A Number node's value, min and max", ["node", "instance"], async (_, a, ct) =>
            await veadotube.GetNumberAsync(a.GetString("node"), a.GetString("instance"), ct).ConfigureAwait(false) is { } n
                ? new { value = n.Value, min = n.Min, max = n.Max } : null),
    ];

    private static double? Num(JsonElement a, string name) => a.GetElement(name) switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetDouble(),
        { ValueKind: JsonValueKind.String } s => Steps.OptionalNumber(s.GetString() ?? "", name),
        _ => null,
    };

    private static TimeSpan? Seconds(JsonElement a) => Num(a, "seconds") is { } s && s > 0 ? TimeSpan.FromSeconds(Math.Min(s, 86_400)) : null;
}
