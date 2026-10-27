using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.Speech;

/// <summary>Decides whether a chat message is read aloud and what exactly is said.</summary>
internal static class ChatSpeech
{
    /// <summary>The text to speak for a chat event, or null when it should stay silent.</summary>
    public static string? BuildUtterance(BotEvent chatEvent, SpeakChatSettings settings)
    {
        var args = chatEvent.Args;
        var message = VariableResolver.Format(args.GetValueOrDefault("message"));
        if (string.IsNullOrWhiteSpace(message)) return null;
        if (settings.IgnoreCommands && message.TrimStart().StartsWith('!')) return null;
        if (IsIgnored(args, settings.IgnoredUsers)) return null;

        if (settings.SkipLinks) message = TextFilter.ReplaceLinks(message);
        message = TextFilter.Truncate(TextFilter.CollapseWhitespace(message), settings.MaxLength);

        var templateArgs = new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase) { ["message"] = message };
        var text = TextFilter.CollapseWhitespace(VariableResolver.Resolve(settings.Template, templateArgs));
        return text.Length == 0 ? null : text;
    }

    private static bool IsIgnored(IReadOnlyDictionary<string, object?> args, IEnumerable<string> ignoredUsers)
    {
        var user = VariableResolver.Format(args.GetValueOrDefault("user"));
        var userName = VariableResolver.Format(args.GetValueOrDefault("userName"));
        return ignoredUsers.Any(ignored =>
            !string.IsNullOrWhiteSpace(ignored)
            && (string.Equals(ignored.Trim(), user, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ignored.Trim(), userName, StringComparison.OrdinalIgnoreCase)));
    }
}
