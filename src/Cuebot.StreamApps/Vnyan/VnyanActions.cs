using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;
using Microsoft.Extensions.DependencyInjection;
using static Cuebot.StreamApps.Common.Steps;

namespace Cuebot.StreamApps.Vnyan;

/// <summary>VNyan service and status (its sub-action joins the module's SubActionSet).</summary>
internal static class VnyanRegistration
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton(sp => new VnyanService(sp.GetRequiredService<JsonFile<StreamAppsSettings>>(),
            sp.GetRequiredService<AppLog>(), sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<Cuebot.Core.IConnectionStatus>(sp => sp.GetRequiredService<VnyanService>());
    }
}

/// <summary>VNyan sub-actions. Type ids are stored in actions.json and must never change.</summary>
internal static class VnyanSubActions
{
    public const string Category = "VNyan";

    public static readonly string[] TypeIds = ["vnyan.send"];

    public static IReadOnlyList<ISubActionHandler> Create(VnyanService vnyan) =>
    [
        new StreamAppSubAction("vnyan.send", Category, "Send to VNyan",
            [
                new("action", "Action name")
                {
                    Help = "The name typed into an \"API Message\" node in VNyan's node graph; may use %arguments%",
                },
                new("payload", "Values", ParameterKind.Multiline)
                {
                    Help = "Optional. One name=value per line (or a JSON object). The node passes each value on; values may use %arguments%.",
                },
            ],
            (_, v, ct) => vnyan.SendAsync(v["action"], ParsePairs(v["payload"]), ct)),
    ];
}
