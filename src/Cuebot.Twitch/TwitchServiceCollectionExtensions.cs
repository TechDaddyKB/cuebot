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
        foreach (var spec in TwitchSubActions.All)
            services.AddSingleton<ISubActionHandler>(sp => new TwitchSubAction(spec, () => sp.GetRequiredService<TwitchService>().Api));
        services.AddSingleton<IScriptFunctionProvider, TwitchScriptFunctions>();
        services.AddSingleton<Cuebot.Core.Chat.IChatEventAdapter, TwitchChatAdapter>();
        services.AddSingleton<Cuebot.Core.Chat.IChatSender, TwitchChatSender>();
        services.AddSingleton<Cuebot.Core.Chat.Moderation.IChatModerator>(sp =>
        {
            var twitch = sp.GetRequiredService<TwitchService>();
            return new TwitchChatModerator(() => twitch.Api, () => twitch.State == Cuebot.Core.ConnectionState.Connected && twitch.IsAuthorized);
        });
        services.AddSingleton<Cuebot.Core.Chat.Moderation.IChannelRoles>(sp =>
            new TwitchChannelRoles(() => sp.GetRequiredService<TwitchService>().Api, sp.GetRequiredService<AppLog>()));
        return services;
    }
}
