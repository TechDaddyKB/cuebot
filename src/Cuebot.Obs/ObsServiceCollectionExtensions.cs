using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using Cuebot.Obs.SubActions;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Obs;

/// <summary>Registers the OBS module.</summary>
public static class ObsServiceCollectionExtensions
{
    /// <summary>Adds obs.json settings, <see cref="ObsService"/> (also as <see cref="IConnectionStatus"/>), sub-actions, triggers and script functions.</summary>
    public static IServiceCollection AddObs(this IServiceCollection services)
    {
        services.AddSettingsFile<ObsSettings>("obs.json");
        services.AddSingleton<ObsService>();
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<ObsService>());

        services.AddSingleton<ISubActionHandler, SetSceneSubAction>();
        services.AddSingleton<ISubActionHandler, SetSourceVisibilitySubAction>();
        services.AddSingleton<ISubActionHandler, SetInputMuteSubAction>();
        services.AddSingleton<ISubActionHandler, SetTextSubAction>();
        services.AddSingleton<ISubActionHandler>(sp => OutputSubAction.Streaming(sp.GetRequiredService<ObsService>()));
        services.AddSingleton<ISubActionHandler>(sp => OutputSubAction.Recording(sp.GetRequiredService<ObsService>()));
        services.AddSingleton<ISubActionHandler, RawRequestSubAction>();
        services.AddSingleton<ITriggerProvider, ObsTriggers>();
        services.AddSingleton<IScriptFunctionProvider, ObsScriptFunctions>();
        return services;
    }
}
