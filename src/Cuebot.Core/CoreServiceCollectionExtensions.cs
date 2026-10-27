using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Commands;
using Cuebot.Core.Storage;
using Cuebot.Core.Variables;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core;

public static class CoreServiceCollectionExtensions
{
    public static IServiceCollection AddCuebotCore(this IServiceCollection services, AppPaths paths)
    {
        services.AddSingleton(paths);
        services.AddSingleton(_ => new AppLog(paths));
        services.AddSingleton<EventBus>();
        services.AddSingleton(sp => new GlobalVariables(paths, sp.GetRequiredService<AppLog>()));
        services.AddSingleton(sp => new ProfileService(paths, sp.GetRequiredService<AppLog>()));
        services.AddSingleton<Registry>(sp => new Registry(
            sp.GetServices<ISubActionHandler>(), sp.GetServices<ITriggerType>(), sp.GetServices<ITriggerProvider>()));
        services.AddSingleton<ActionEngine>();
        services.AddSingleton<ScriptApi>();
        services.AddSingleton(_ => new HttpClient { Timeout = TimeSpan.FromSeconds(30) });
        services.AddSingleton<ChatHub>();
        services.AddSingleton<ITriggerProvider, ChatTriggers>();
        services.AddSingleton<ISubActionHandler, SendChatToPlatformsSubAction>();
        services.AddSingleton<IScriptFunctionProvider, CoreScriptFunctions>();

        // Each feature area registers its own services, triggers and sub-actions.
        return services
            .AddEngineFeatures()
            .AddVariableFeatures()
            .AddCommandAndUserFeatures()
            .AddSystemFeatures()
            .AddChatExtrasFeatures()
            .AddWave4Features();
    }

    /// <summary>Registers a module's settings file (e.g. "twitch.json") as a singleton JsonFile.</summary>
    public static IServiceCollection AddSettingsFile<T>(this IServiceCollection services, string fileName) where T : class, new() =>
        services.AddSingleton(sp => new JsonFile<T>(sp.GetRequiredService<AppPaths>().File(fileName), sp.GetRequiredService<AppLog>()));
}
