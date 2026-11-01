using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;
using Microsoft.Extensions.DependencyInjection;
using static Cuebot.StreamApps.Common.Steps;

namespace Cuebot.StreamApps.Vmc;

/// <summary>VMC service and status (no triggers: VMC only sends; its sub-actions join the module's SubActionSet).</summary>
internal static class VmcRegistration
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton(sp => new VmcService(sp.GetRequiredService<JsonFile<StreamAppsSettings>>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton<Cuebot.Core.IConnectionStatus>(sp => sp.GetRequiredService<VmcService>());
    }
}

/// <summary>VMC sub-actions (category "VMC (3D avatars)"). Type ids are stored in actions.json and must never change.</summary>
internal static class VmcSubActions
{
    public const string Category = "VMC (3D avatars)";

    public static readonly string[] TypeIds = ["vmc.expression", "vmc.clearExpressions"];

    public static IReadOnlyList<ISubActionHandler> Create(VmcService vmc) =>
    [
        new StreamAppSubAction("vmc.expression", Category, "Show avatar expression (VMC)",
            [
                new("expression", "Expression")
                {
                    Help = "The avatar's expression (blend shape clip) name, e.g. Joy, Angry or one you made; may use %arguments%",
                    Suggestions = () => VmcService.PresetExpressions,
                },
                new("strength", "Strength") { Help = "0 to 1, or a percentage like 50% (empty = 1, full)" },
                new("seconds", "For (seconds)") { Help = "How long before it goes back to 0 (empty = stay until changed)" },
            ],
            async (ctx, v, ct) => Store(ctx, "vmc.strength",
                (double)await vmc.SetExpressionAsync(v["expression"], VmcService.ParseValue(v["strength"]), OptionalNumber(v["seconds"], "Seconds"), ct)
                    .ConfigureAwait(false))),
        new StreamAppSubAction("vmc.clearExpressions", Category, "Clear avatar expressions (VMC)",
            [
                new("expressions", "Expressions")
                {
                    Help = "Comma-separated expression names to set back to 0; may use %arguments%",
                    DefaultValue = "Joy, Angry, Sorrow, Fun, Surprised",
                },
            ],
            (_, v, ct) => vmc.ClearExpressionsAsync(v["expressions"].Split(','), ct)),
    ];
}
