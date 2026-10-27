using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Twitch.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Twitch;

/// <summary>Registers the Twitch module.</summary>
public static class TwitchServiceCollectionExtensions
{
    /// <summary>Adds Twitch settings (twitch.json), <see cref="TwitchService"/>, triggers, sub-actions and script functions.</summary>
    public static IServiceCollection AddTwitch(this IServiceCollection services)
    {
        services.AddSettingsFile<TwitchSettings>("twitch.json");
        services.AddSingleton(sp => new TwitchService(
            sp.GetRequiredService<JsonFile<TwitchSettings>>(),
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>()));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<TwitchService>());
        services.AddSingleton<ITriggerProvider, TwitchTriggers>();
        services.AddSingleton<ISubActionHandler, SendChatMessageSubAction>();
        services.AddSingleton<IScriptFunctionProvider, TwitchScriptFunctions>();
        return services;
    }
}
