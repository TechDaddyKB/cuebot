using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.WaveLink;

/// <summary>Registers the Elgato Wave Link integration.</summary>
public static class WaveLinkServiceCollectionExtensions
{
    /// <summary>
    /// Adds settings (wavelink.json), <see cref="WaveLinkService"/> (an <see cref="IConnectionStatus"/>), triggers,
    /// sub-actions and script functions. Call <see cref="WaveLinkService.StartAsync"/> at startup: it reports
    /// "not available" on Linux and connects only when auto-connect is on (default off).
    /// </summary>
    public static IServiceCollection AddWaveLink(this IServiceCollection services)
    {
        services.AddSettingsFile<WaveLinkSettings>("wavelink.json");
        services.AddSingleton(sp => new WaveLinkService(sp.GetRequiredService<JsonFile<WaveLinkSettings>>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<WaveLinkService>());
        services.AddSingleton<ITriggerProvider>(sp => new WaveLinkTriggers(sp.GetRequiredService<WaveLinkService>()));
        services.AddSingleton(sp => new SubActionSet(WaveLinkSubActions.Create(sp.GetRequiredService<WaveLinkService>())));
        // One registration per sub-action so the Registry sees each one.
        foreach (var typeId in WaveLinkSubActions.TypeIds)
            services.AddSingleton<ISubActionHandler>(sp => sp.GetRequiredService<SubActionSet>().Get(typeId));
        services.AddSingleton<IScriptFunctionProvider>(sp => new WaveLinkScriptFunctions(sp.GetRequiredService<WaveLinkService>()));
        return services;
    }

    /// <summary>The module's sub-actions, built once (their tables need the service).</summary>
    internal sealed class SubActionSet(IReadOnlyList<ISubActionHandler> handlers)
    {
        private readonly Dictionary<string, ISubActionHandler> _byId = handlers.ToDictionary(h => h.TypeId, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<ISubActionHandler> All => handlers;

        public ISubActionHandler Get(string typeId) => _byId[typeId];
    }
}
