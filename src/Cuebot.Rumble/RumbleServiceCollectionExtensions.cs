using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Rumble.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Rumble;

/// <summary>Registers the Rumble module.</summary>
public static class RumbleServiceCollectionExtensions
{
    /// <summary>
    /// Adds Rumble settings (rumble.json), <see cref="RumbleService"/>, its combined-chat adapter and sender,
    /// triggers, sub-actions and script functions.
    /// </summary>
    public static IServiceCollection AddRumble(this IServiceCollection services)
    {
        services.AddSettingsFile<RumbleSettings>("rumble.json");
        services.AddSingleton(sp => new RumbleService(
            sp.GetRequiredService<JsonFile<RumbleSettings>>(),
            sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>()));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<RumbleService>());
        services.AddSingleton<IChatEventAdapter, RumbleChatAdapter>();
        services.AddSingleton<Cuebot.Core.Overlays.Events.IOverlayEventAdapter, RumbleOverlayAdapter>();
        services.AddSingleton<IChatSender, RumbleChatSender>();
        services.AddSingleton<ITriggerProvider, RumbleTriggers>();
        services.AddSingleton<ISubActionHandler, SendChatMessageSubAction>();
        services.AddSingleton<IScriptFunctionProvider, RumbleScriptFunctions>();
        return services;
    }
}
