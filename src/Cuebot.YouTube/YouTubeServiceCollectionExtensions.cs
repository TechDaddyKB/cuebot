using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.YouTube.SubActions;
using Cuebot.YouTube.Triggers;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.YouTube;

/// <summary>Registers the YouTube module.</summary>
public static class YouTubeServiceCollectionExtensions
{
    /// <summary>
    /// Adds YouTube settings (youtube.json), <see cref="YouTubeService"/>, its combined-chat adapter and sender,
    /// triggers, sub-actions and script functions. The app starts it with <see cref="YouTubeService.StartAsync"/>.
    /// </summary>
    public static IServiceCollection AddYouTube(this IServiceCollection services)
    {
        services.AddSettingsFile<YouTubeSettings>("youtube.json");
        services.AddSingleton(sp => new YouTubeService(
            sp.GetRequiredService<JsonFile<YouTubeSettings>>(),
            sp.GetRequiredService<HttpClient>(),
            sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>()));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<YouTubeService>());
        services.AddSingleton<IChatEventAdapter, YouTubeChatAdapter>();
        services.AddSingleton<Cuebot.Core.Overlays.Events.IOverlayEventAdapter, YouTubeOverlayAdapter>();
        services.AddSingleton<Cuebot.Core.Chat.Moderation.IChatModerator>(sp => new YouTubeChatModerator(sp.GetRequiredService<YouTubeService>()));
        services.AddSingleton<IChatSender, YouTubeChatSender>();
        services.AddSingleton<ITriggerProvider, YouTubeTriggers>();
        services.AddSingleton(sp => new SubActionSet(YouTubeSubActions.Create(sp.GetRequiredService<YouTubeService>()).ToArray()));
        for (var i = 0; i < YouTubeSubActions.Count; i++)
        {
            var index = i;
            services.AddSingleton(sp => sp.GetRequiredService<SubActionSet>().Handlers[index]);
        }
        services.AddSingleton<IScriptFunctionProvider, YouTubeScriptFunctions>();
        return services;
    }

    /// <summary>The module's sub-action handlers, built once and registered individually.</summary>
    private sealed record SubActionSet(ISubActionHandler[] Handlers);
}
