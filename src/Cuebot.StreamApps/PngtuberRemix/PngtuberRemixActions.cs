using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;
using Microsoft.Extensions.DependencyInjection;
using static Cuebot.StreamApps.Common.Steps;
using E = Cuebot.StreamApps.PngtuberRemix.PngtuberRemixEvents;

namespace Cuebot.StreamApps.PngtuberRemix;

/// <summary>PNGTuber Remix service, status and triggers (its sub-actions join the module's SubActionSet).</summary>
internal static class PngtuberRemixRegistration
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton(sp => new PngtuberRemixService(sp.GetRequiredService<JsonFile<StreamAppsSettings>>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton<Cuebot.Core.IConnectionStatus>(sp => sp.GetRequiredService<PngtuberRemixService>());
        services.AddSingleton<ITriggerProvider>(sp => new PngtuberRemixTriggers(sp.GetRequiredService<PngtuberRemixService>()));
        services.AddSingleton<Cuebot.Core.Scripting.IScriptFunctionProvider>(sp => new PngtuberRemixScriptFunctions(sp.GetRequiredService<PngtuberRemixService>()));
    }
}

/// <summary>PNGTuber Remix triggers (category "PNGTuber Remix"). Type ids are stored in actions.json and must never change.</summary>
internal sealed class PngtuberRemixTriggers(PngtuberRemixService? remix = null) : ITriggerProvider
{
    public const string Category = "PNGTuber Remix";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Trigger(Category, E.Connected, "Connected"),
        Trigger(Category, E.Disconnected, "Disconnected"),
        Trigger(Category, E.StateChanged, "Avatar state changed",
            [
                new("state", "States")
                {
                    Help = "Comma-separated state names or numbers; leave empty for any",
                    Suggestions = remix is null ? null : remix.StateNames,
                },
            ],
            (e, s) => MatchesList(s["state"], e.Text("pngtuberRemix.state"), e.Text("pngtuberRemix.stateNumber"))),
        Trigger(Category, E.SpritesChanged, "Sprites changed (model loaded or edited)"),
        Trigger(Category, E.ThrowingPausedChanged, "Throwing paused/resumed",
            [new("state", "Now", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", "Paused", "Running"] }],
            (e, s) => s["state"] is "" or "Any" || e.Flag("pngtuberRemix.throwingPaused") == (s["state"] == "Paused")),
    ];
}

/// <summary>PNGTuber Remix sub-actions. Type ids are stored in actions.json and must never change.</summary>
internal static class PngtuberRemixSubActions
{
    public static readonly string[] TypeIds =
    [
        "pngtuberRemix.setState", "pngtuberRemix.getState", "pngtuberRemix.setVisibility", "pngtuberRemix.shake",
        "pngtuberRemix.bounce", "pngtuberRemix.move", "pngtuberRemix.throw", "pngtuberRemix.pauseThrowing",
        "pngtuberRemix.loadModel", "pngtuberRemix.matchVisibility", "pngtuberRemix.getVisibility", "pngtuberRemix.pressKey",
    ];

    public static IReadOnlyList<ISubActionHandler> Create(PngtuberRemixService remix)
    {
        ParameterDescriptor Sprite(string key = "sprite", string label = "Sprite", bool attached = false) => new(key, label)
        {
            Help = "The sprite's name exactly as in PNGTuber Remix's Sprites list (capital letters matter), or its sprite id; may use %arguments%",
            Suggestions = attached ? remix.SpriteNamesWithAttached : remix.SpriteNames,
        };
        ParameterDescriptor Number(string key, string label, string help) => new(key, label) { Help = help };
        return
        [
            new StreamAppSubAction("pngtuberRemix.setState", PngtuberRemixTriggers.Category, "Change avatar state",
                [
                    new("state", "State")
                    {
                        Help = "The state's name, or its number (1 = the first state button); may use %arguments%",
                        Suggestions = remix.StateNames,
                    },
                ],
                async (ctx, v, ct) => Store(ctx, "pngtuberRemix.state", await remix.SetStateAsync(v["state"], ct).ConfigureAwait(false))),
            new StreamAppSubAction("pngtuberRemix.getState", PngtuberRemixTriggers.Category, "Get avatar state", [],
                async (ctx, _, ct) =>
                {
                    var current = await remix.GetStateAsync(ct).ConfigureAwait(false);
                    ctx.Args["pngtuberRemix.state"] = current?.Name ?? "";
                    ctx.Args["pngtuberRemix.stateNumber"] = current?.Id.ToString(CultureInfo.InvariantCulture) ?? "";
                    ctx.Args["pngtuberRemix.states"] = string.Join(", ", remix.StateNames());
                }),
            // "what" (Sprite/Group) was dropped: PNGTuber Remix shows or hides the sprites attached to a sprite either way.
            // Saved actions keep the value; it is ignored.
            new StreamAppSubAction("pngtuberRemix.setVisibility", PngtuberRemixTriggers.Category, "Show/hide sprite",
                [Sprite(attached: true), Switch("Visible", "Hidden")],
                async (ctx, v, ct) => Store(ctx, "pngtuberRemix.visible",
                    await remix.SetVisibilityAsync(v["sprite"], ParseSwitch(v["state"]), ct).ConfigureAwait(false))),
            new StreamAppSubAction("pngtuberRemix.matchVisibility", PngtuberRemixTriggers.Category, "Match sprite visibility",
                [
                    Sprite(attached: true),
                    Sprite("match", "Match sprite"),
                    new("opposite", "Opposite", ParameterKind.Boolean) { DefaultValue = "false", Help = "Hide it when the other is visible, and show it when the other is hidden" },
                    new("attached", "Attached sprites follow", ParameterKind.Boolean)
                    {
                        DefaultValue = "true",
                        Help = "Also show or hide everything attached to it (off = only this sprite)",
                    },
                ],
                async (ctx, v, ct) => Store(ctx, "pngtuberRemix.visible",
                    await remix.MatchVisibilityAsync(v["sprite"], v["match"], v.GetBool("opposite"), v.GetBool("attached", true), ct).ConfigureAwait(false))),
            new StreamAppSubAction("pngtuberRemix.getVisibility", PngtuberRemixTriggers.Category, "Get sprite visibility",
                [Sprite()],
                async (ctx, v, ct) => Store(ctx, "pngtuberRemix.visible", await remix.GetVisibilityAsync(v["sprite"], ct).ConfigureAwait(false))),
            new StreamAppSubAction("pngtuberRemix.pressKey", PngtuberRemixTriggers.Category, "Press PNGTuber Remix key bind",
                [new("key", "Key") { Help = "The key as Godot names it, e.g. A, 1, F5, Space; may use %arguments%" }],
                async (ctx, v, ct) =>
                {
                    var found = await remix.PressKeyAsync(v["key"], ct).ConfigureAwait(false);
                    ctx.Args["pngtuberRemix.actionsFound"] = string.Join(", ", found);
                    ctx.Args["pngtuberRemix.actionsFoundCount"] = found.Count;
                }),
            new StreamAppSubAction("pngtuberRemix.shake", PngtuberRemixTriggers.Category, "Shake sprite",
                [
                    Sprite(),
                    Number("strength", "Strength", "How far it shakes, in pixels (empty = 10)"),
                    Number("seconds", "Seconds", "How long it shakes (empty = 1)"),
                ],
                (_, v, ct) => remix.ShakeAsync(v["sprite"], OptionalNumber(v["strength"], "Strength"), OptionalNumber(v["seconds"], "Seconds"), ct)),
            new StreamAppSubAction("pngtuberRemix.bounce", PngtuberRemixTriggers.Category, "Bounce sprite",
                [
                    Sprite(),
                    Number("height", "Height", "How high it jumps, in pixels (empty = 20)"),
                    Number("seconds", "Seconds", "How long one bounce takes (empty = 0.5)"),
                ],
                (_, v, ct) => remix.BounceAsync(v["sprite"], OptionalNumber(v["height"], "Height"), OptionalNumber(v["seconds"], "Seconds"), ct)),
            new StreamAppSubAction("pngtuberRemix.move", PngtuberRemixTriggers.Category, "Move, grow or spin sprite",
                [
                    Sprite(),
                    Number("x", "X", "Where to move it, left/right in pixels; set X and Y together (both empty = don't move)"),
                    Number("y", "Y", "Where to move it, up/down in pixels; set X and Y together"),
                    Number("scale", "Size", "1 = normal, 2 = twice as big; set with Turn (empty = 1 when Turn or a stretch is set)"),
                    Number("rotation", "Turn (degrees)", "e.g. 15 to tilt it; set with Size (empty = 0, upright)"),
                    Number("scaleX", "Stretch wide", "Size left/right only, e.g. 1.3 (empty = Size)"),
                    Number("scaleY", "Stretch tall", "Size up/down only, e.g. 0.7 to squash (empty = Size)"),
                    Number("seconds", "Seconds", "How long the change takes (empty = 1)"),
                    Number("returnAfter", "Go back after (seconds)", "Empty = stay there"),
                ],
                (_, v, ct) => remix.MoveAsync(v["sprite"], OptionalNumber(v["x"], "X"), OptionalNumber(v["y"], "Y"),
                    OptionalNumber(v["scale"], "Size"), OptionalNumber(v["rotation"], "Turn"), OptionalNumber(v["seconds"], "Seconds"),
                    OptionalNumber(v["returnAfter"], "Go back after"), ct,
                    OptionalNumber(v["scaleX"], "Stretch wide"), OptionalNumber(v["scaleY"], "Stretch tall"))),
            new StreamAppSubAction("pngtuberRemix.throw", PngtuberRemixTriggers.Category, "Throw things at avatar",
                [
                    new("item", "Item") { Help = "An image name from PNGTuber Remix's throwables; empty = random ones; may use %arguments%" },
                    Number("amount", "How many", "Empty = 1 named item, or PNGTuber Remix's own amount for random ones; capped by the card's \"most per throw\"; may use %arguments%"),
                    Number("variance", "Variance", "How much the throws vary, e.g. 0.5 (empty = PNGTuber Remix's own setting)"),
                    new("bothSides", "From both sides", ParameterKind.Choice)
                    {
                        DefaultValue = "App setting",
                        Choices = ["App setting", "Yes", "No"],
                    },
                ],
                async (ctx, v, ct) => Store(ctx, "pngtuberRemix.thrown", await remix.ThrowAsync(v["item"],
                    OptionalNumber(v["amount"], "How many") is { } n ? (int)Math.Min(n > 0 ? Math.Max(1, Math.Round(n)) : n, int.MaxValue) : null, ct,
                    OptionalNumber(v["variance"], "Variance"),
                    v["bothSides"] switch { "Yes" => true, "No" => false, _ => null }).ConfigureAwait(false))),
            new StreamAppSubAction("pngtuberRemix.pauseThrowing", PngtuberRemixTriggers.Category, "Pause/resume throwing",
                [Switch("Paused", "Running")],
                async (ctx, v, ct) => Store(ctx, "pngtuberRemix.throwingPaused",
                    await remix.SetThrowingPausedAsync(ParseSwitch(v["state"] switch { "Paused" => "on", "Running" => "off", var s => s }), ct).ConfigureAwait(false))),
            new StreamAppSubAction("pngtuberRemix.loadModel", PngtuberRemixTriggers.Category, "Load PNGTuber Remix model",
                [new("path", "Model file") { Help = "The full path of a saved .pngRemix (or .save) model file; may use %arguments%" }],
                (_, v, ct) => remix.LoadModelAsync(v["path"], ct)),
        ];
    }
}
