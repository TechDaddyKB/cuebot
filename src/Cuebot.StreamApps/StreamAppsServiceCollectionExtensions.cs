using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Meld;
using Cuebot.StreamApps.Mtion;
using Cuebot.StreamApps.Obsbot;
using Cuebot.StreamApps.PolyPop;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.StreamApps;

/// <summary>Registers the Meld Studio and PolyPop integrations.</summary>
public static class StreamAppsServiceCollectionExtensions
{
    /// <summary>
    /// Adds settings (streamapps.json), <see cref="MeldStudioService"/> and <see cref="PolyPopService"/> (both
    /// <see cref="IConnectionStatus"/>), triggers, sub-actions and script functions. Call <c>StartAsync</c> on both
    /// services at startup; each starts only when its auto-connect/auto-start option is on (default off).
    /// </summary>
    public static IServiceCollection AddStreamApps(this IServiceCollection services)
    {
        services.AddSettingsFile<StreamAppsSettings>("streamapps.json");
        services.AddSingleton(sp => new MeldStudioService(Settings(sp), sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton(sp => new PolyPopService(Settings(sp), sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<MeldStudioService>());
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<PolyPopService>());
        Tits.TitsRegistration.Add(services);
        Lumia.LumiaRegistration.Add(services);
        services.AddSingleton(sp => new ObsbotService(Settings(sp), sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<ObsbotService>());
        services.AddSingleton(sp => new MtionService(Settings(sp), sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>(), sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<MtionService>());

        services.AddSingleton<ITriggerProvider>(sp => new MeldTriggers(sp.GetRequiredService<MeldStudioService>()));
        services.AddSingleton<ITriggerProvider>(_ => new PolyPopTriggers());
        services.AddSingleton<ITriggerProvider>(_ => new ObsbotTriggers());
        services.AddSingleton<ITriggerProvider>(_ => new MtionTriggers());

        services.AddSingleton(sp => new SubActionSet(
        [
            .. MeldSubActions.Create(sp.GetRequiredService<MeldStudioService>()),
            .. PolyPopSubActions.Create(sp.GetRequiredService<PolyPopService>()),
            .. Tits.TitsSubActions.Create(sp.GetRequiredService<Tits.TitsService>()),
            .. Lumia.LumiaSubActions.Create(sp.GetRequiredService<Lumia.LumiaService>()),
            .. ObsbotSubActions.Create(sp.GetRequiredService<ObsbotService>()),
            .. MtionSubActions.Create(sp.GetRequiredService<MtionService>()),
        ]));
        // One registration per sub-action so the Registry sees each one.
        foreach (var typeId in SubActionSet.TypeIds)
            services.AddSingleton<ISubActionHandler>(sp => sp.GetRequiredService<SubActionSet>().Get(typeId));

        services.AddSingleton<IScriptFunctionProvider>(sp => new StreamAppsScriptFunctions(
            sp.GetRequiredService<MeldStudioService>(), sp.GetRequiredService<PolyPopService>()));
        services.AddSingleton<IScriptFunctionProvider>(sp => new ObsbotScriptFunctions(sp.GetRequiredService<ObsbotService>()));
        services.AddSingleton<IScriptFunctionProvider>(sp => new MtionScriptFunctions(sp.GetRequiredService<MtionService>()));
        return services;
    }

    private static JsonFile<StreamAppsSettings> Settings(IServiceProvider sp) => sp.GetRequiredService<JsonFile<StreamAppsSettings>>();

    /// <summary>The module's sub-actions, built once (their tables need the services).</summary>
    internal sealed class SubActionSet(IReadOnlyList<ISubActionHandler> handlers)
    {
        private readonly Dictionary<string, ISubActionHandler> _byId = handlers.ToDictionary(h => h.TypeId, StringComparer.OrdinalIgnoreCase);

        /// <summary>Every type id, known without building the services (kept in sync by a test).</summary>
        public static readonly string[] TypeIds =
        [
            .. MeldSubActions.TypeIds, .. PolyPopSubActions.TypeIds,
            .. Tits.TitsSubActions.TypeIds,
            .. Lumia.LumiaSubActions.TypeIds,
            .. ObsbotSubActions.TypeIds, .. MtionSubActions.TypeIds,
        ];

        public IReadOnlyList<ISubActionHandler> All => handlers;

        public ISubActionHandler Get(string typeId) => _byId[typeId];
    }
}
