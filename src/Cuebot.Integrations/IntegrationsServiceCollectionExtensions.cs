using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Integrations.HeartRate;
using Cuebot.Integrations.HeartRate.HypeRate;
using Cuebot.Integrations.HeartRate.Pulsoid;
using Cuebot.Integrations.VTubeStudio;
using Cuebot.Integrations.Voicemod;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Cuebot.Integrations;

/// <summary>Registers the stream-app integrations: VTube Studio, Voicemod, Pulsoid and HypeRate.</summary>
public static class IntegrationsServiceCollectionExtensions
{
    /// <summary>
    /// Adds settings (integrations.json), <see cref="VTubeStudioService"/>, <see cref="VoicemodService"/>,
    /// <see cref="PulsoidService"/> and <see cref="HypeRateService"/> (all <see cref="IConnectionStatus"/>), the shared
    /// <see cref="HeartRateHub"/>, triggers, sub-actions and script functions. Call <c>StartAsync</c> on the four services
    /// at startup; each connects only when its auto-connect option is on (default off).
    /// </summary>
    public static IServiceCollection AddIntegrations(this IServiceCollection services)
    {
        services.AddSettingsFile<IntegrationsSettings>("integrations.json");
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton(sp => new HeartRateHub(sp.GetService<GlobalVariables>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton(sp => new VTubeStudioService(Settings(sp), sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton(sp => new VoicemodService(Settings(sp), sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton(sp => new PulsoidService(Settings(sp), sp.GetRequiredService<HeartRateHub>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton(sp => new HypeRateService(Settings(sp), sp.GetRequiredService<HeartRateHub>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<VTubeStudioService>());
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<VoicemodService>());
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<PulsoidService>());
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<HypeRateService>());

        services.AddSingleton<ITriggerProvider>(sp => new VTubeStudioTriggers(sp.GetRequiredService<VTubeStudioService>()));
        services.AddSingleton<ITriggerProvider>(sp => new VoicemodTriggers(sp.GetRequiredService<VoicemodService>()));
        services.AddSingleton<ITriggerProvider>(sp => new HeartRateTriggers("Pulsoid", HeartRateEventTypes.Pulsoid,
            sp.GetRequiredService<TimeProvider>(), clips: false));
        services.AddSingleton<ITriggerProvider>(sp => new HeartRateTriggers("HypeRate", HeartRateEventTypes.HypeRate,
            sp.GetRequiredService<TimeProvider>(), clips: true));

        services.AddSingleton<SubActionSet>(sp => new SubActionSet(
        [
            .. VTubeStudioSubActions.Create(sp.GetRequiredService<VTubeStudioService>()),
            .. VoicemodSubActions.Create(sp.GetRequiredService<VoicemodService>()),
            .. HeartRateSubActions.Create(sp.GetRequiredService<HeartRateHub>(), sp.GetRequiredService<TimeProvider>()),
        ]));
        // One registration per sub-action so the Registry sees each one.
        foreach (var typeId in SubActionSet.TypeIds)
            services.AddSingleton<ISubActionHandler>(sp => sp.GetRequiredService<SubActionSet>().Get(typeId));

        services.AddSingleton<IScriptFunctionProvider>(sp => new IntegrationsScriptFunctions(
            sp.GetRequiredService<VTubeStudioService>(), sp.GetRequiredService<VoicemodService>(), sp.GetRequiredService<PulsoidService>(),
            sp.GetRequiredService<HypeRateService>(), sp.GetRequiredService<HeartRateHub>()));
        return services;
    }

    private static JsonFile<IntegrationsSettings> Settings(IServiceProvider sp) => sp.GetRequiredService<JsonFile<IntegrationsSettings>>();

    /// <summary>The module's sub-actions, built once (their tables need the services).</summary>
    internal sealed class SubActionSet(IReadOnlyList<ISubActionHandler> handlers)
    {
        private readonly Dictionary<string, ISubActionHandler> _byId = handlers.ToDictionary(h => h.TypeId, StringComparer.OrdinalIgnoreCase);

        /// <summary>Every type id, known without building the services (kept in sync by a test).</summary>
        public static readonly string[] TypeIds =
        [
            "vtubestudio.triggerHotkey", "vtubestudio.loadModel", "vtubestudio.moveModel", "vtubestudio.setExpression",
            "vtubestudio.removeAllExpressions", "vtubestudio.injectParameters", "vtubestudio.colorTint", "vtubestudio.resetColorTint",
            "vtubestudio.loadItem", "vtubestudio.unloadItem", "vtubestudio.moveItem", "vtubestudio.getCurrentModel", "vtubestudio.getModels",
            "vtubestudio.getHotkeys", "vtubestudio.getExpressions", "vtubestudio.rawRequest",
            "voicemod.selectVoice", "voicemod.selectRandomVoice", "voicemod.getCurrentVoice", "voicemod.getVoices", "voicemod.setVoiceChanger",
            "voicemod.setHearMyself", "voicemod.setBackgroundEffects", "voicemod.setMute", "voicemod.playSound", "voicemod.stopAllSounds",
            "voicemod.getSounds", "voicemod.setCensorBeep",
            "heartrate.get",
        ];

        public IReadOnlyList<ISubActionHandler> All => handlers;

        public ISubActionHandler Get(string typeId) => _byId[typeId];
    }
}
