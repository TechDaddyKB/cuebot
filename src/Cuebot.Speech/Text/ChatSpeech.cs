using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.Speech;

/// <summary>Decides whether a chat message is read aloud and what exactly is said.</summary>
internal static class ChatSpeech
{
    /// <summary>The text to speak for a chat event with the default filters, or null when it should stay silent.</summary>
    public static string? BuildUtterance(BotEvent chatEvent, SpeakChatSettings settings) =>
        ChatContext.FromEvent(chatEvent) is { } chat
            ? BuildUtterance(chat, settings, new FilterProfile { MaxRepeatedCharacters = 0, MaxRepeatedWords = 0, IgnoreBots = false })
            : null;

    /// <summary>The text to speak for a chat message, or null when it should stay silent.</summary>
    /// <param name="nickname">Spoken instead of the user name when set.</param>
    /// <param name="repeat">Use the shorter repeat template (same user spoke recently).</param>
    /// <param name="emoteNames">Third-party emote names to strip.</param>
    /// <param name="checkPrefixes">False for speaking commands, whose text may start with an ignored prefix.</param>
    public static string? BuildUtterance(ChatContext chat, SpeakChatSettings settings, FilterProfile profile,
        string? nickname = null, bool repeat = false, IReadOnlySet<string>? emoteNames = null, bool checkPrefixes = true)
    {
        if (string.IsNullOrWhiteSpace(chat.Message)) return null;
        var ignored = MessageFilter.CheckIgnore(chat, profile, settings);
        if (ignored != SkipReason.None && (checkPrefixes || ignored != SkipReason.IgnoredPrefix)) return null;

        var names = new HashSet<string>(chat.EmoteNames, StringComparer.Ordinal);
        if (emoteNames is not null) names.UnionWith(emoteNames);
        var outcome = MessageFilter.Apply(chat.Message, profile, settings, chat.EmoteRanges, names);
        if (outcome.Skipped) return null;

        var spokenName = settings.UseNicknames && !string.IsNullOrWhiteSpace(nickname) ? nickname.Trim() : chat.User;
        var templateArgs = new Dictionary<string, object?>(chat.Args, StringComparer.OrdinalIgnoreCase)
        {
            ["message"] = outcome.Text,
            ["user"] = spokenName,
            ["nickname"] = spokenName,
        };
        var template = repeat && !string.IsNullOrWhiteSpace(settings.RepeatTemplate) ? settings.RepeatTemplate : settings.Template;
        var text = TextFilter.CollapseWhitespace(VariableResolver.Resolve(template, templateArgs));
        return text.Length == 0 ? null : text;
    }
}
