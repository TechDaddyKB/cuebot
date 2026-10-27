using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat.Emotes;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Chat.Pronouns;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.App.ViewModels.Chat;

/// <summary>
/// Optional services the combined chat page uses beyond the chat hub. Anything null is simply not offered (no emote
/// images, no pronouns, no moderation menu, no quick actions).
/// </summary>
public sealed class ChatPageServices
{
    public EmoteService? Emotes { get; init; }
    public EmoteImageLoader? Images { get; init; }

    /// <summary>emotes.json, for the channel ids and provider toggles on the options panel.</summary>
    public JsonFile<EmoteSettings>? EmoteSettings { get; init; }
    public PronounService? Pronouns { get; init; }
    public ChatModerationService? Moderation { get; init; }
    public ActionEngine? Engine { get; init; }

    /// <summary>Action names offered in the quick action editor.</summary>
    public Func<IEnumerable<string>>? ActionNames { get; init; }

    /// <summary>Page settings; saved whenever the options change.</summary>
    public JsonFile<ChatPageSettings>? Settings { get; init; }

    /// <summary>Takes whatever the composition root registered (chat extras may not be wired yet).</summary>
    public static ChatPageServices From(AppHost host)
    {
        var paths = host.Get<AppPaths>();
        var log = host.Get<AppLog>();
        return new ChatPageServices
        {
            Emotes = host.GetAll<EmoteService>().FirstOrDefault(),
            EmoteSettings = host.GetAll<JsonFile<EmoteSettings>>().FirstOrDefault(),
            Images = new EmoteImageLoader(host.Get<HttpClient>(), paths.GetDirectory(Path.Combine("cache", "emote-images"))),
            Pronouns = host.GetAll<PronounService>().FirstOrDefault(),
            Moderation = host.GetAll<ChatModerationService>().FirstOrDefault(),
            Engine = host.Get<ActionEngine>(),
            ActionNames = () => host.Get<ProfileService>().Snapshot.Actions.Select(a => a.Name).OrderBy(n => n, StringComparer.OrdinalIgnoreCase),
            Settings = new JsonFile<ChatPageSettings>(paths.File("chat-page.json"), log),
        };
    }
}
