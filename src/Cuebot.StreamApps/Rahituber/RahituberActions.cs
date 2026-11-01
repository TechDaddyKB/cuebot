using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;
using Microsoft.Extensions.DependencyInjection;
using static Cuebot.StreamApps.Common.Steps;

namespace Cuebot.StreamApps.Rahituber;

/// <summary>RahiTuber service and status (its sub-action joins the module's SubActionSet).</summary>
internal static class RahituberRegistration
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton(sp => new RahituberService(sp.GetRequiredService<JsonFile<StreamAppsSettings>>(),
            sp.GetRequiredService<AppLog>(), sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<Cuebot.Core.IConnectionStatus>(sp => sp.GetRequiredService<RahituberService>());
    }
}

/// <summary>RahiTuber sub-actions. Type ids are stored in actions.json and must never change.</summary>
internal static class RahituberSubActions
{
    public const string Category = "RahiTuber";

    public static readonly string[] TypeIds = ["rahituber.state"];

    public static IReadOnlyList<ISubActionHandler> Create(RahituberService rahi) =>
    [
        new StreamAppSubAction("rahituber.state", Category, "RahiTuber state",
            [
                new("state", "State") { Help = "The state's name, or its place in RahiTuber's list counted from 0 (0 = the top one); may use %arguments%" },
                new("how", "How", ParameterKind.Choice)
                {
                    DefaultValue = "Press",
                    Choices = ["Press", "Release", "Hold"],
                    ChoiceLabels = new Dictionary<string, string>
                    {
                        ["Press"] = "Press it (like its hotkey)",
                        ["Release"] = "Let go (for \"while held\" states)",
                        ["Hold"] = "Hold it for some seconds, then let go",
                    },
                },
                new("seconds", "Seconds") { Help = "For Hold: how long to hold it (empty = 3)" },
            ],
            (_, v, ct) => v["how"] switch
            {
                "Release" => rahi.SetStateAsync(v["state"], false, ct),
                "Hold" => rahi.HoldStateAsync(v["state"],
                    TimeSpan.FromSeconds(OptionalNumber(v["seconds"], "Seconds") is { } s and > 0 ? s : 3), ct),
                _ => rahi.SetStateAsync(v["state"], true, ct),
            }),
    ];
}
