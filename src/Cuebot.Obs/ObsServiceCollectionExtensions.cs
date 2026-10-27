using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using Cuebot.Obs.Streamlabs;
using Cuebot.Obs.SubActions;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Obs;

/// <summary>Registers the OBS module (and Streamlabs Desktop, which shares it).</summary>
public static class ObsServiceCollectionExtensions
{
    /// <summary>
    /// Adds obs.json settings, <see cref="ObsService"/> (also as <see cref="IConnectionStatus"/>), the OBS sub-actions,
    /// triggers and script functions, and the Streamlabs Desktop equivalents (streamlabs-desktop.json).
    /// </summary>
    public static IServiceCollection AddObs(this IServiceCollection services)
    {
        services.AddSettingsFile<ObsSettings>("obs.json");
        services.AddSingleton<ObsService>();
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<ObsService>());

        // The catalog builds handlers without touching the service (it is only captured), so its size is known up front.
        services.AddSingleton(sp => new ObsSubActionList(ObsSubActionCatalog.Create(sp.GetRequiredService<ObsService>())));
        var count = ObsSubActionCatalog.Create(null!).Count;
        for (var i = 0; i < count; i++)
        {
            var index = i;
            services.AddSingleton<ISubActionHandler>(sp => sp.GetRequiredService<ObsSubActionList>().Handlers[index]);
        }
        services.AddSingleton<ITriggerProvider, ObsTriggers>();
        services.AddSingleton<IScriptFunctionProvider, ObsScriptFunctions>();

        services.AddStreamlabsDesktop();
        return services;
    }

    private sealed record ObsSubActionList(IReadOnlyList<ISubActionHandler> Handlers);
}
