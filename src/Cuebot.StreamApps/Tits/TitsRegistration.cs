using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.StreamApps.Tits;

/// <summary>T.I.T.S. service, status, triggers and script functions (its sub-actions join the module's SubActionSet).</summary>
internal static class TitsRegistration
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton(sp => new TitsService(sp.GetRequiredService<JsonFile<StreamAppsSettings>>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<TitsService>());
        services.AddSingleton<ITriggerProvider>(sp => new TitsTriggers(sp.GetRequiredService<TitsService>()));
        services.AddSingleton<IScriptFunctionProvider>(sp => new TitsScriptFunctions(sp.GetRequiredService<TitsService>()));
    }
}
