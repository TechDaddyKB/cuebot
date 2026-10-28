using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Cuebot.Speech;
using Cuebot.VoiceControl.Engines;
using Cuebot.VoiceControl.Models;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.VoiceControl;

/// <summary>Registers voice control (speech-to-text commands).</summary>
public static class VoiceControlServiceCollectionExtensions
{
    /// <summary>
    /// Adds settings (voicecontrol.json), the Whisper and Windows engines, the model store (data folder
    /// <c>models/whisper</c>), <see cref="VoiceControlService"/> (an <see cref="IConnectionStatus"/>), triggers,
    /// sub-actions and script functions. The microphone comes from the speech module's <see cref="IAudioInput"/>
    /// (register <c>AddSpeech()</c> too; without it voice control reports that no audio input exists). Call
    /// <see cref="VoiceControlService.StartAsync"/> at startup; it listens only when Auto Start is on (default off).
    /// </summary>
    public static IServiceCollection AddVoiceControl(this IServiceCollection services)
    {
        services.AddSettingsFile<VoiceControlSettings>("voicecontrol.json");
        services.AddSingleton(sp => new ModelStore(
            sp.GetRequiredService<AppPaths>().GetDirectory(Path.Combine("models", "whisper")),
            sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton<ISpeechRecognizerEngine>(sp =>
        {
            var settings = sp.GetRequiredService<JsonFile<VoiceControlSettings>>();
            return new WhisperEngine(() => settings.Value, sp.GetRequiredService<ModelStore>());
        });
        services.AddSingleton<ISpeechRecognizerEngine, WindowsSpeechEngine>();
        services.AddSingleton(sp => new VoiceControlService(
            sp.GetRequiredService<JsonFile<VoiceControlSettings>>(),
            sp.GetServices<ISpeechRecognizerEngine>(),
            sp.GetService<IAudioInput>(),
            sp.GetRequiredService<ModelStore>(),
            sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>()));
        services.AddSingleton<IConnectionStatus>(sp => sp.GetRequiredService<VoiceControlService>());
        services.AddSingleton<ITriggerProvider>(sp => new VoiceControlTriggers(sp.GetRequiredService<VoiceControlService>()));
        services.AddSingleton(sp => new SubActionSet(VoiceControlSubActions.Create(sp.GetRequiredService<VoiceControlService>())));
        foreach (var typeId in VoiceControlSubActions.TypeIds)
            services.AddSingleton<ISubActionHandler>(sp => sp.GetRequiredService<SubActionSet>().Get(typeId));
        services.AddSingleton<IScriptFunctionProvider>(sp => new VoiceControlScriptFunctions(sp.GetRequiredService<VoiceControlService>()));
        return services;
    }

    /// <summary>The module's sub-actions, built once (their table needs the service).</summary>
    internal sealed class SubActionSet(IReadOnlyList<ISubActionHandler> handlers)
    {
        private readonly Dictionary<string, ISubActionHandler> _byId = handlers.ToDictionary(h => h.TypeId, StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<ISubActionHandler> All => handlers;

        public ISubActionHandler Get(string typeId) => _byId[typeId];
    }
}
