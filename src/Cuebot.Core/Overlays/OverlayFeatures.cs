using Cuebot.Core.Actions;
using Cuebot.Core.Chat.Emotes;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays.Events;
using Cuebot.Core.Overlays.Session;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Overlays;

/// <summary>
/// Registration for overlays: overlays.json, elements.json, the asset folder, the feed to overlay pages, the bridge that
/// fills it from bot events (platform modules add <see cref="IOverlayEventAdapter"/>s) and the stream totals
/// (session-data.json). The app creates the bridge and the totals at startup.
/// </summary>
public static class OverlayFeatures
{
    public static IServiceCollection AddOverlayFeatures(this IServiceCollection services)
    {
        services.AddSingleton(sp => new OverlayStore(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton(sp => new ElementStore(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton(sp => new AssetStore(sp.GetRequiredService<AppPaths>()));
        services.AddSingleton(sp => new OverlayFeed(sp.GetRequiredService<AppLog>()));
        services.AddSingleton(sp => new OverlayEventBridge(sp.GetRequiredService<EventBus>(), sp.GetRequiredService<OverlayFeed>(),
            sp.GetServices<IOverlayEventAdapter>(), sp.GetRequiredService<AppLog>(), sp.GetService<EmoteService>()));
        services.AddSingleton(sp => new SessionDataStore(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<AppLog>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<OverlayFeed>(), sp.GetRequiredService<OverlayEventBridge>()));
        services.AddSingleton<IPeriodicService>(sp => sp.GetRequiredService<SessionDataStore>());
        return services;
    }
}
