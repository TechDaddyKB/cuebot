using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Core.Webhooks;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Relay;

/// <summary>Registers the webhook relay client.</summary>
public static class RelayServiceCollectionExtensions
{
    /// <summary>
    /// Adds relay settings (relay.json) and <see cref="RelayClient"/> (also as <see cref="IConnectionStatus"/>), which
    /// dispatches to every <see cref="IWebhookHandler"/> registered in DI. Call <see cref="RelayClient.StartAsync"/> at
    /// startup; it connects only when the relay is enabled and auto-connect is on.
    /// </summary>
    public static IServiceCollection AddRelay(this IServiceCollection services)
    {
        services.AddSettingsFile<RelaySettings>("relay.json");
        services.AddSingleton(sp => new RelayClient(
            sp.GetRequiredService<JsonFile<RelaySettings>>(),
            sp.GetServices<IWebhookHandler>(),
            sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>()));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<RelayClient>());
        return services;
    }
}
