using Cuebot.Core.Chat.Emotes;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Overlays;

/// <summary>
/// Registration for overlays: overlays.json, elements.json, the asset folder, the feed to overlay pages and the bridge
/// that fills it from bot events (the app creates it at startup; platform modules add <see cref="IOverlayEventAdapter"/>s).
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
        return services;
    }
}
