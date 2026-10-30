using Cuebot.Core.Logging;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Overlays;

/// <summary>Registration for overlays: overlays.json, elements.json, the asset folder and the feed to overlay pages.</summary>
public static class OverlayFeatures
{
    public static IServiceCollection AddOverlayFeatures(this IServiceCollection services)
    {
        services.AddSingleton(sp => new OverlayStore(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton(sp => new ElementStore(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton(sp => new AssetStore(sp.GetRequiredService<AppPaths>()));
        services.AddSingleton(sp => new OverlayFeed(sp.GetRequiredService<AppLog>()));
        return services;
    }
}
