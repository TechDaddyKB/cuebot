using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;
using Microsoft.Extensions.DependencyInjection;
using static Cuebot.StreamApps.Common.Steps;
using E = Cuebot.StreamApps.Nizima.NizimaEvents;

namespace Cuebot.StreamApps.Nizima;

/// <summary>nizima LIVE service, status and triggers (its sub-actions join the module's SubActionSet).</summary>
internal static class NizimaRegistration
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton(sp => new NizimaService(sp.GetRequiredService<JsonFile<StreamAppsSettings>>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton<Cuebot.Core.IConnectionStatus>(sp => sp.GetRequiredService<NizimaService>());
        services.AddSingleton<ITriggerProvider>(sp => new NizimaTriggers(sp.GetRequiredService<NizimaService>()));
    }
}

/// <summary>nizima LIVE triggers (category "nizima LIVE"). Type ids are stored in actions.json and must never change.</summary>
internal sealed class NizimaTriggers(NizimaService nizima) : ITriggerProvider
{
    public const string Category = "nizima LIVE";

    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Trigger(Category, E.Connected, "Connected"),
        Trigger(Category, E.Disconnected, "Disconnected"),
        Trigger(Category, E.ModelChanged, "Selected model changed",
            [
                new("model", "Models")
                {
                    Help = "Comma-separated model names; leave empty for any",
                    Suggestions = () => [.. nizima.Models.Select(m => m.Name)],
                },
            ],
            (e, s) => MatchesList(s["model"], e.Text("model"))),
    ];
}

/// <summary>nizima LIVE sub-actions. Type ids are stored in actions.json and must never change.</summary>
internal static class NizimaSubActions
{
    public static readonly string[] TypeIds =
    [
        "nizima.expression", "nizima.clearExpressions", "nizima.motion", "nizima.stopMotion", "nizima.hotkey",
        "nizima.addItem", "nizima.removeItem", "nizima.changeModel", "nizima.effect",
    ];

    public static IReadOnlyList<ISubActionHandler> Create(NizimaService nizima)
    {
        ParameterDescriptor Model() => new("model", "Model")
        {
            Help = "Which model on screen; leave empty for the selected one (most people have just one); may use %arguments%",
            Suggestions = () => [.. nizima.Models.Select(m => m.Name)],
        };
        ParameterDescriptor Named(string key, string label, string help, Func<IReadOnlyList<string>> suggestions) =>
            new(key, label) { Help = help + "; may use %arguments%", Suggestions = suggestions };
        return
        [
            new StreamAppSubAction("nizima.expression", NizimaTriggers.Category, "nizima LIVE expression on/off",
                [
                    Model(),
                    Named("expression", "Expression", "The expression's name, as in nizima LIVE",
                        () => [.. nizima.Expressions.Select(x => x.Name)]),
                    Switch("On", "Off", defaultValue: "On"),
                    new("only", "Turn the other expressions off first", ParameterKind.Boolean) { DefaultValue = "false" },
                ],
                async (ctx, v, ct) => Store(ctx, "nizima.expressionOn",
                    await nizima.SetExpressionAsync(v["model"], v["expression"], ParseSwitch(v["state"]),
                        bool.TryParse(v["only"], out var only) && only, ct).ConfigureAwait(false))),
            new StreamAppSubAction("nizima.clearExpressions", NizimaTriggers.Category, "nizima LIVE expressions off",
                [Model()],
                (_, v, ct) => nizima.StopAllExpressionsAsync(v["model"], ct)),
            new StreamAppSubAction("nizima.motion", NizimaTriggers.Category, "Play nizima LIVE motion",
                [
                    Model(),
                    Named("motion", "Motion", "The motion's name, as in nizima LIVE", () => [.. nizima.Motions.Select(m => m.Name)]),
                ],
                async (ctx, v, ct) => Store(ctx, "nizima.motion", await nizima.StartMotionAsync(v["model"], v["motion"], ct).ConfigureAwait(false))),
            new StreamAppSubAction("nizima.stopMotion", NizimaTriggers.Category, "Stop nizima LIVE motion",
                [Model()],
                (_, v, ct) => nizima.StopMotionAsync(v["model"], ct)),
            new StreamAppSubAction("nizima.hotkey", NizimaTriggers.Category, "Press nizima LIVE hotkey",
                [
                    Model(),
                    new("key", "Hotkey") { Help = "The model's hotkey as nizima LIVE shows it, e.g. F1 or Ctrl+A; may use %arguments%" },
                ],
                (_, v, ct) => nizima.TriggerHotkeyAsync(v["model"], v["key"], ct)),
            new StreamAppSubAction("nizima.addItem", NizimaTriggers.Category, "Show nizima LIVE item",
                [
                    Named("item", "Item", "An item's name from nizima LIVE's item list", () => [.. nizima.RegisteredItems.Select(i => i.Name)]),
                    new("model", "Next to model") { Help = "Leave empty for the selected model's window", Suggestions = () => [.. nizima.Models.Select(m => m.Name)] },
                ],
                async (ctx, v, ct) => Store(ctx, "nizima.itemId", await nizima.AddItemAsync(v["model"], v["item"], ct).ConfigureAwait(false))),
            new StreamAppSubAction("nizima.removeItem", NizimaTriggers.Category, "Hide nizima LIVE item",
                [Named("item", "Item", "The item's name; every item with that name on screen goes", () => [.. nizima.Items.Select(i => i.Name).Distinct()])],
                async (ctx, v, ct) => Store(ctx, "nizima.removed", (double)await nizima.RemoveItemsAsync(v["item"], ct).ConfigureAwait(false))),
            new StreamAppSubAction("nizima.changeModel", NizimaTriggers.Category, "Change nizima LIVE model",
                [
                    Named("newModel", "Change to", "A model's name from nizima LIVE's model list", () => [.. nizima.RegisteredModels.Select(m => m.Name)]),
                    new("model", "Instead of") { Help = "Which model on screen to swap; leave empty for the selected one", Suggestions = () => [.. nizima.Models.Select(m => m.Name)] },
                ],
                (_, v, ct) => nizima.ChangeModelAsync(v["model"], v["newModel"], ct)),
            new StreamAppSubAction("nizima.effect", NizimaTriggers.Category, "nizima LIVE effect on/off",
                [
                    Named("effect", "Effect", "The effect's name, as in nizima LIVE's effect list", () => [.. nizima.EffectGroups.Select(g => g.Name)]),
                    Switch("On", "Off"),
                ],
                async (ctx, v, ct) => Store(ctx, "nizima.effectOn",
                    await nizima.SetEffectAsync(v["effect"], ParseSwitch(v["state"]), ct).ConfigureAwait(false))),
        ];
    }
}
