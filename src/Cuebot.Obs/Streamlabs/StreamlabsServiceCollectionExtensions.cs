using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Obs.Streamlabs;

/// <summary>Registers Streamlabs Desktop (called from AddObs; it shares the OBS project).</summary>
public static class StreamlabsServiceCollectionExtensions
{
    /// <summary>
    /// Adds streamlabs-desktop.json settings, <see cref="StreamlabsDesktopService"/> (also as <see cref="IConnectionStatus"/>),
    /// sub-actions, triggers and script functions. The host must call <see cref="StreamlabsDesktopService.StartAsync"/>
    /// for auto-connect.
    /// </summary>
    public static IServiceCollection AddStreamlabsDesktop(this IServiceCollection services)
    {
        services.AddSettingsFile<StreamlabsSettings>("streamlabs-desktop.json");
        services.AddSingleton<StreamlabsDesktopService>();
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<StreamlabsDesktopService>());
        services.AddSingleton(sp => new StreamlabsSubActionList(StreamlabsCatalog.CreateSubActions(sp.GetRequiredService<StreamlabsDesktopService>())));
        var count = StreamlabsCatalog.CreateSubActions(null!).Count;
        for (var i = 0; i < count; i++)
        {
            var index = i;
            services.AddSingleton<ISubActionHandler>(sp => sp.GetRequiredService<StreamlabsSubActionList>().Handlers[index]);
        }
        services.AddSingleton<ITriggerProvider, StreamlabsTriggers>();
        services.AddSingleton<IScriptFunctionProvider, StreamlabsScriptFunctions>();
        return services;
    }

    private sealed record StreamlabsSubActionList(IReadOnlyList<ISubActionHandler> Handlers);
}
