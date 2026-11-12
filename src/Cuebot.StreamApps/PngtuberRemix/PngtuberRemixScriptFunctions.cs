using System.Text.Json;
using Cuebot.Core.Scripting;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.PngtuberRemix;

/// <summary>PNGTuber Remix functions for user scripts (C# and Python).</summary>
internal sealed class PngtuberRemixScriptFunctions(PngtuberRemixService remix) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("pngtuberRemix.isConnected", "Whether Cuebot is connected to PNGTuber Remix", [],
            (_, _, _) => Task.FromResult<object?>(remix.IsConnected)),
        new("pngtuberRemix.setState", "Switch the avatar state by name or number (1 = the first); returns its name", ["state"],
            async (_, a, ct) => await remix.SetStateAsync(Text(a, "state"), ct).ConfigureAwait(false)),
        new("pngtuberRemix.getState", "The showing avatar state: name and number (asked fresh)", [], async (_, _, ct) =>
            await remix.GetStateAsync(ct).ConfigureAwait(false) is { } s ? new { name = s.Name, number = s.Id } : null),
        new("pngtuberRemix.getStates", "All avatar states: number, name, current", [], (_, _, _) =>
            Task.FromResult<object?>(remix.States.Select(s => new { number = s.Id, name = s.Name, current = s.IsCurrent }).ToArray())),
        new("pngtuberRemix.getSprites", "All sprites: name, id, visible, parentId, children (names), attached (count)", [], async (_, _, ct) =>
        {
            await remix.RefreshAsync(ct).ConfigureAwait(false);
            return remix.Sprites.Select(s => new
            {
                name = s.Name,
                id = s.Id,
                visible = s.Visible,
                parentId = s.ParentId,
                children = s.Children,
                attached = remix.AttachedCount(s),
            }).ToArray();
        }),
        new("pngtuberRemix.setVisibility", "Show (true), hide (false) or toggle (\"toggle\") a sprite; returns whether it is now visible",
            ["sprite", "visible"], async (_, a, ct) =>
            {
                var mode = a.GetElement("visible") switch
                {
                    { ValueKind: JsonValueKind.True } => SwitchMode.On,
                    { ValueKind: JsonValueKind.False } => SwitchMode.Off,
                    { ValueKind: JsonValueKind.String } s => Steps.ParseSwitch(s.GetString() ?? ""),
                    _ => SwitchMode.Toggle,
                };
                return await remix.SetVisibilityAsync(Text(a, "sprite"), mode, ct).ConfigureAwait(false);
            }),
        new("pngtuberRemix.isVisible", "Whether a sprite is visible now (asked fresh)", ["sprite"],
            async (_, a, ct) => await remix.GetVisibilityAsync(Text(a, "sprite"), ct).ConfigureAwait(false)),
        new("pngtuberRemix.pressKey", "Press a PNGTuber Remix key bind; returns what it did, like \"Hat (Toggle)\"", ["key"],
            async (_, a, ct) => await remix.PressKeyAsync(Text(a, "key"), ct).ConfigureAwait(false)),
        new("pngtuberRemix.throw", "Throw things at the avatar (item empty = random ones); returns the amount sent",
            ["item", "amount", "variance", "bothSides"], async (_, a, ct) =>
            {
                var amount = Num(a, "amount");
                bool? both = a.GetElement("bothSides") switch
                {
                    { ValueKind: JsonValueKind.True } => true,
                    { ValueKind: JsonValueKind.False } => false,
                    _ => null,
                };
                return await remix.ThrowAsync(Text(a, "item"), amount is { } n ? (int)Math.Min(Math.Round(n), int.MaxValue) : null, ct,
                    Num(a, "variance"), both).ConfigureAwait(false);
            }),
    ];

    private static string Text(JsonElement a, string name) => a.GetElement(name) switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
        { ValueKind: JsonValueKind.Number } n => n.ToString(),
        _ => "",
    };

    private static double? Num(JsonElement a, string name) => a.GetElement(name) switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetDouble(),
        { ValueKind: JsonValueKind.String } s => Steps.OptionalNumber(s.GetString() ?? "", name),
        _ => null,
    };
}
