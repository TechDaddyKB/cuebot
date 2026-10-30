using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
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
        services.AddSettingsFile<SpeechUserStore>("speech-users.json");
        services.AddSingleton(sp => new SpeechUserDirectory(sp.GetRequiredService<JsonFile<SpeechUserStore>>()));
        services.AddSingleton<Cuebot.Core.Users.IViewerDataStore>(sp => sp.GetRequiredService<SpeechUserDirectory>());

        AddEngines(services);

        // Audio playback and input: one mixing backend serves speech, sounds and the VoiceGate mic monitor.
        services.AddSingleton(sp => AudioPlayerFactory.Create(sp.GetRequiredService<AppLog>()));
        services.AddSingleton<IAudioPlayer>(sp => sp.GetRequiredService<AudioOutput>());
        services.AddSingleton<IAudioInput>(sp => sp.GetRequiredService<AudioOutput>());
        services.AddSingleton<AudioService>();

        services.AddSingleton<ISpeechChatReplier, ChatHubReplier>();
        services.AddSingleton(sp => new SpeechService(
            sp.GetRequiredService<JsonFile<SpeechSettings>>(),
            sp.GetServices<ITtsEngine>(),
            sp.GetRequiredService<IAudioPlayer>(),
            sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>(),
            sp.GetRequiredService<SpeechUserDirectory>(),
            sp.GetService<ISpeechChatReplier>(),
            sp.GetService<IAudioInput>(),
            sp.GetServices<IEmoteNameProvider>(),
            roles: sp.GetServices<Cuebot.Core.Chat.Moderation.IChannelRoles>()));

        services.AddSingleton<ISubActionHandler, SpeakSubAction>();
        services.AddSingleton<ISubActionHandler, SkipSpeechSubAction>();
        services.AddSingleton<ISubActionHandler, ClearSpeechQueueSubAction>();
        services.AddSingleton<ISubActionHandler, PauseSpeechSubAction>();
        services.AddSingleton<ISubActionHandler, SetSpeechEnabledSubAction>();
        services.AddSingleton<ISubActionHandler, SetEventSpeechSubAction>();
        services.AddSingleton<ISubActionHandler, SetSpeechAliasSubAction>();
        services.AddSingleton<ISubActionHandler, SetUserVoiceSubAction>();
        services.AddSingleton<ISubActionHandler, StopUserSpeechSubAction>();
        services.AddSingleton<ISubActionHandler, ActivateVoiceGateSubAction>();
        services.AddSingleton<ISubActionHandler, PlaySoundSubAction>();
        services.AddSingleton<ISubActionHandler, PlaySoundFromFolderSubAction>();
        services.AddSingleton<ISubActionHandler, StopSoundsSubAction>();
        services.AddSingleton<ITriggerProvider, SpeechTriggers>();
        services.AddSingleton<IScriptFunctionProvider, SpeechScriptFunctions>();
        return services;
    }

    private static void AddEngines(IServiceCollection services)
    {
        services.AddSingleton<ITtsEngine, WindowsSpeechEngine>();
        services.AddSingleton<ITtsEngine, EspeakEngine>();
        AddCloud(services, (http, s) => new ElevenLabsEngine(http, () => s().ElevenLabsApiKey));
        AddCloud(services, (http, s) => new AzureEngine(http, () => s().Engines.Azure));
        AddCloud(services, (http, s) => new PollyEngine(http, () => s().Engines.Polly));
        AddCloud(services, (http, s) => new GoogleEngine(http, () => s().Engines.Google));
        AddCloud(services, (http, s) => new WatsonEngine(http, () => s().Engines.Watson));
        AddCloud(services, (http, s) => new UberDuckEngine(http, () => s().Engines.UberDuck));
        AddCloud(services, (http, s) => new TtsMonsterEngine(http, () => s().Engines.TtsMonster));
        AddCloud(services, (http, s) => new AcapelaEngine(http, () => s().Engines.Acapela));
        AddCloud(services, (http, s) => new CereProcEngine(http, () => s().Engines.CereProc));
    }

    private static void AddCloud(IServiceCollection services, Func<HttpClient, Func<SpeechSettings>, ITtsEngine> create) =>
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<JsonFile<SpeechSettings>>();
            return create(sp.GetRequiredService<HttpClient>(), () => settings.Value);
        });
}

/// <summary>Replies through the combined chat hub to the platform the command came from.</summary>
internal sealed class ChatHubReplier(ChatHub hub) : ISpeechChatReplier
{
    public Task ReplyAsync(string platform, string text, CancellationToken ct) => hub.SendAsync(platform, text, ct);
}
