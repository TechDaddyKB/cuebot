using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Speech;

/// <summary>Registers the speech and audio module.</summary>
public static class SpeechServiceCollectionExtensions
{
    public static IServiceCollection AddSpeech(this IServiceCollection services)
    {
        services.AddSettingsFile<SpeechSettings>("speech.json");

        services.AddSingleton<ITtsEngine, WindowsSpeechEngine>();
        services.AddSingleton<ITtsEngine, EspeakEngine>();
        services.AddSingleton<ITtsEngine>(sp =>
        {
            var settings = sp.GetRequiredService<JsonFile<SpeechSettings>>();
            return new ElevenLabsEngine(sp.GetRequiredService<HttpClient>(), () => settings.Value.ElevenLabsApiKey);
        });
        services.AddSingleton(sp => AudioPlayerFactory.Create(sp.GetRequiredService<Cuebot.Core.Logging.AppLog>()));
        services.AddSingleton<IAudioPlayer>(sp => sp.GetRequiredService<AudioOutput>());
        services.AddSingleton<IAudioInput>(sp => sp.GetRequiredService<AudioOutput>());
        services.AddSingleton<SpeechService>();
        services.AddSingleton<AudioService>();

        services.AddSingleton<ISubActionHandler, SpeakSubAction>();
        services.AddSingleton<ISubActionHandler, SkipSpeechSubAction>();
        services.AddSingleton<ISubActionHandler, ClearSpeechQueueSubAction>();
        services.AddSingleton<ISubActionHandler, PauseSpeechSubAction>();
        services.AddSingleton<ISubActionHandler, PlaySoundSubAction>();
        services.AddSingleton<ISubActionHandler, PlaySoundFromFolderSubAction>();
        services.AddSingleton<ISubActionHandler, StopSoundsSubAction>();
        services.AddSingleton<ITriggerProvider, SpeechTriggers>();
        services.AddSingleton<IScriptFunctionProvider, SpeechScriptFunctions>();
        return services;
    }
}
