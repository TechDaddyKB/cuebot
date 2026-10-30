using Cuebot.Core.Actions;
using Cuebot.Core.Chat.Emotes;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Chat.Pronouns;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Storage;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Chat;

/// <summary>Registration for chat extras: emotes (7TV/BTTV/FFZ + platform emotes), pronouns and moderation lookup.</summary>
public static class ChatExtrasFeatures
{
    public static IServiceCollection AddChatExtrasFeatures(this IServiceCollection services)
    {
        services.AddSettingsFile<EmoteSettings>("emotes.json");
        services.AddSingleton<IEmoteProvider>(sp => new SevenTvEmoteProvider(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<IEmoteProvider>(sp => new BttvEmoteProvider(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton<IEmoteProvider>(sp => new FfzEmoteProvider(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton(sp =>
        {
            // Resolved once: the chat page can still ask for emotes while the app shuts down and the provider is gone.
            var settings = sp.GetRequiredService<JsonFile<EmoteSettings>>();
            return new EmoteService(
                sp.GetRequiredService<Events.EventBus>(),
                sp.GetRequiredService<AppLog>(),
                EmoteDiskCache.For(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<AppLog>()),
                () => settings.Value,
                sp.GetServices<IEmoteProvider>(),
                sp.GetServices<IEmoteChannelSource>());
        });
        services.AddSingleton<ISubActionHandler, RefreshEmotesSubAction>();
        services.AddSingleton<ITriggerProvider, EmoteTriggers>();
        services.AddSingleton<IScriptFunctionProvider, EmoteScriptFunctions>();

        services.AddSingleton(sp => new PronounService(sp.GetRequiredService<HttpClient>(), sp.GetRequiredService<AppLog>()));
        services.AddSingleton<ISubActionHandler, AddPronounsSubAction>();
        services.AddSingleton<IScriptFunctionProvider, PronounScriptFunctions>();

        services.AddSingleton(sp => new ChatModerationService(sp.GetServices<IChatModerator>(), sp.GetServices<IChatAnnouncer>()));
        return services;
    }
}
