using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Kick.SubActions;
using Cuebot.Kick.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Kick;

/// <summary>Registers the Kick module.</summary>
public static class KickServiceCollectionExtensions
{
    /// <summary>
    /// Adds Kick settings (kick.json), <see cref="KickService"/>, its combined-chat adapter and sender, triggers,
    /// sub-actions and script functions.
    /// </summary>
    public static IServiceCollection AddKick(this IServiceCollection services)
    {
        services.AddSettingsFile<KickSettings>("kick.json");
        services.AddSingleton(sp => new KickService(
            sp.GetRequiredService<JsonFile<KickSettings>>(),
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>()));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<KickService>());
        services.AddSingleton<IChatEventAdapter, KickChatAdapter>();
        services.AddSingleton<IChatSender, KickChatSender>();
        services.AddSingleton<ITriggerProvider, KickTriggers>();
        services.AddSingleton<ISubActionHandler, SendMessageSubAction>();
        services.AddSingleton<ISubActionHandler, ReplyToMessageSubAction>();
        services.AddSingleton<ISubActionHandler, DeleteMessageSubAction>();
        services.AddSingleton<ISubActionHandler, SetTitleSubAction>();
        services.AddSingleton<ISubActionHandler, SetCategorySubAction>();
        services.AddSingleton<ISubActionHandler, BanUserSubAction>();
        services.AddSingleton<ISubActionHandler, TimeoutUserSubAction>();
        services.AddSingleton<ISubActionHandler>(sp => new UnbanUserSubAction(sp.GetRequiredService<KickService>(), "kick.unbanUser", "Unban user"));
        services.AddSingleton<ISubActionHandler>(sp => new UnbanUserSubAction(sp.GetRequiredService<KickService>(), "kick.untimeoutUser", "Remove timeout"));
        services.AddSingleton<ISubActionHandler, AddBroadcasterInfoSubAction>();
        services.AddSingleton<ISubActionHandler, GetUserInfoSubAction>();
        services.AddSingleton<IScriptFunctionProvider, KickScriptFunctions>();
        return services;
    }
}
