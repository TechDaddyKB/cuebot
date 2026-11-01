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
        services.AddSingleton<ITriggerProvider>(_ => new PngtuberRemixTriggers());
    }
}

/// <summary>PNGTuber Remix triggers (category "PNGTuber Remix"). Type ids are stored in actions.json and must never change.</summary>
internal sealed class PngtuberRemixTriggers : ITriggerProvider
{
    public const string Category = "PNGTuber Remix";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Trigger(Category, E.Connected, "Connected"),
        Trigger(Category, E.Disconnected, "Disconnected"),
    ];
}

/// <summary>PNGTuber Remix sub-actions. Type ids are stored in actions.json and must never change.</summary>
internal static class PngtuberRemixSubActions
{
    public static readonly string[] TypeIds =
    [
        "pngtuberRemix.setState", "pngtuberRemix.getState", "pngtuberRemix.setVisibility", "pngtuberRemix.shake",
        "pngtuberRemix.bounce", "pngtuberRemix.move", "pngtuberRemix.throw", "pngtuberRemix.pauseThrowing",
        "pngtuberRemix.loadModel",
    ];

    public static IReadOnlyList<ISubActionHandler> Create(PngtuberRemixService remix)
    {
        ParameterDescriptor Sprite() => new("sprite", "Sprite")
        {
            Help = "The sprite's name as in PNGTuber Remix's Sprites list (or its sprite id); may use %arguments%",
            Suggestions = remix.SpriteNames,
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
            new StreamAppSubAction("pngtuberRemix.setVisibility", PngtuberRemixTriggers.Category, "Show/hide sprite",
                [
                    Sprite(),
                    new("what", "Also its children", ParameterKind.Choice)
                    {
                        DefaultValue = "Sprite",
                        Choices = ["Sprite", "Group"],
                        ChoiceLabels = new Dictionary<string, string>
                        {
                            ["Sprite"] = "Just this sprite",
                            ["Group"] = "This sprite and everything attached to it",
                        },
                    },
                    Switch("Visible", "Hidden"),
                ],
                async (ctx, v, ct) => Store(ctx, "pngtuberRemix.visible",
                    await remix.SetVisibilityAsync(v["sprite"], v["what"] == "Group", ParseSwitch(v["state"]), ct).ConfigureAwait(false))),
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
                    Number("x", "X", "Where to move it, left/right in pixels (empty = don't move)"),
                    Number("y", "Y", "Where to move it, up/down in pixels (empty = don't move)"),
                    Number("scale", "Size", "1 = normal, 2 = twice as big (empty = don't change)"),
                    Number("rotation", "Turn (degrees)", "e.g. 15 to tilt it (empty = don't turn)"),
                    Number("seconds", "Seconds", "How long the change takes (empty = 1)"),
                    Number("returnAfter", "Go back after (seconds)", "Empty = stay there"),
                ],
                (_, v, ct) => remix.MoveAsync(v["sprite"], OptionalNumber(v["x"], "X"), OptionalNumber(v["y"], "Y"),
                    OptionalNumber(v["scale"], "Size"), OptionalNumber(v["rotation"], "Turn"), OptionalNumber(v["seconds"], "Seconds"),
                    OptionalNumber(v["returnAfter"], "Go back after"), ct)),
            new StreamAppSubAction("pngtuberRemix.throw", PngtuberRemixTriggers.Category, "Throw things at avatar",
                [
                    new("item", "Item") { Help = "An image name from PNGTuber Remix's throwables; empty = random ones; may use %arguments%" },
                    Number("amount", "How many", "Empty = 1 named item, or PNGTuber Remix's own amount for random ones; may use %arguments%"),
                ],
                (_, v, ct) => remix.ThrowAsync(v["item"], OptionalNumber(v["amount"], "How many") is { } n ? (int)Math.Round(n) : null, ct)),
            new StreamAppSubAction("pngtuberRemix.pauseThrowing", PngtuberRemixTriggers.Category, "Pause/resume throwing",
                [Switch("Paused", "Running")],
                async (ctx, v, ct) => Store(ctx, "pngtuberRemix.throwingPaused",
                    await remix.SetThrowingPausedAsync(ParseSwitch(v["state"] switch { "Paused" => "on", "Running" => "off", var s => s }), ct).ConfigureAwait(false))),
            new StreamAppSubAction("pngtuberRemix.loadModel", PngtuberRemixTriggers.Category, "Load PNGTuber Remix model",
                [new("path", "Model file") { Help = "The full path of a saved .pngRemix model file; may use %arguments%" }],
                (_, v, ct) => remix.LoadModelAsync(v["path"], ct)),
        ];
    }
}
