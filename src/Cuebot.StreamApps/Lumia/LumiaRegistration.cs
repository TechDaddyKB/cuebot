using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.StreamApps.Lumia;

/// <summary>Lumia Stream service, status, triggers and script functions (its sub-actions join the module's SubActionSet).</summary>
internal static class LumiaRegistration
{
    public static void Add(IServiceCollection services)
    {
        services.AddSingleton(sp => new LumiaService(sp.GetRequiredService<JsonFile<StreamAppsSettings>>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>(), sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<LumiaService>());
        services.AddSingleton<ITriggerProvider>(sp => new LumiaTriggers(sp.GetRequiredService<LumiaService>()));
        services.AddSingleton<IScriptFunctionProvider>(sp => new LumiaScriptFunctions(sp.GetRequiredService<LumiaService>()));
    }
}
