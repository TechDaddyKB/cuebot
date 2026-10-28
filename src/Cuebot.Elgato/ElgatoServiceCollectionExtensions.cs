using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Elgato.CameraHub;
using Cuebot.Elgato.Lights;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Elgato;

/// <summary>Registers the Elgato lights (Control Center), Camera Hub (webcam) and Studio integrations.</summary>
public static class ElgatoServiceCollectionExtensions
{
    /// <summary>
    /// Adds settings (elgato.json), <see cref="ElgatoLightsService"/> and <see cref="CameraHubService"/> (both
    /// <see cref="IConnectionStatus"/>), triggers, sub-actions and script functions. Call <c>StartAsync</c> on both
    /// services at startup; each starts only when its auto-start option is on (default off).
    /// </summary>
    public static IServiceCollection AddElgato(this IServiceCollection services)
    {
        services.AddSettingsFile<ElgatoSettings>("elgato.json");
        services.AddSingleton(sp => new ElgatoLightsService(Settings(sp), sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton(sp => new CameraHubService(Settings(sp), sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<ElgatoLightsService>());
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<CameraHubService>());

        services.AddSingleton<ITriggerProvider>(sp => new LightTriggers(sp.GetRequiredService<ElgatoLightsService>()));
        services.AddSingleton<ITriggerProvider>(sp => new CameraHubTriggers(sp.GetRequiredService<CameraHubService>()));

        services.AddSingleton(sp => new SubActionSet(
        [
            .. LightSubActions.Create(sp.GetRequiredService<ElgatoLightsService>()),
            .. CameraHubSubActions.Create(sp.GetRequiredService<CameraHubService>()),
        ]));
        foreach (var typeId in SubActionSet.TypeIds)
            services.AddSingleton<ISubActionHandler>(sp => sp.GetRequiredService<SubActionSet>().Get(typeId));

        services.AddSingleton<IScriptFunctionProvider>(sp => new ElgatoScriptFunctions(
            sp.GetRequiredService<ElgatoLightsService>(), sp.GetRequiredService<CameraHubService>()));
        return services;
    }

    private static JsonFile<ElgatoSettings> Settings(IServiceProvider sp) => sp.GetRequiredService<JsonFile<ElgatoSettings>>();

    /// <summary>The module's sub-actions, built once (their tables need the services).</summary>
    internal sealed class SubActionSet(IReadOnlyList<ISubActionHandler> handlers)
    {
        private readonly Dictionary<string, ISubActionHandler> _byId = handlers.ToDictionary(h => h.TypeId, StringComparer.OrdinalIgnoreCase);

        /// <summary>Every type id, known without building the services (kept in sync by a test).</summary>
        public static readonly string[] TypeIds = [.. LightSubActions.TypeIds, .. CameraHubSubActions.TypeIds];

        public IReadOnlyList<ISubActionHandler> All => handlers;

        public ISubActionHandler Get(string typeId) => _byId[typeId];
    }
}
