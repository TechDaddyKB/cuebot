namespace Cuebot.Speech;

/// <summary>
/// Supplies third-party emote names (7TV, BTTV, FFZ, ...) so they can be stripped from spoken chat. Register
/// implementations in DI; none ship yet.
/// </summary>
public interface IEmoteNameProvider
{
    /// <summary>Short source name, e.g. "7TV".</summary>
    string Source { get; }

    /// <summary>Emote names currently active for a platform's channel (case-sensitive).</summary>
    IReadOnlyCollection<string> GetEmoteNames(string platform);
}

/// <summary>Why a message was not spoken.</summary>
public enum SkipReason { None, Empty, IgnoredUser, Bot, SharedChat, IgnoredPrefix, BadWord, Link }

/// <summary>The text left after filtering, or the reason it is skipped.</summary>
public readonly record struct FilterOutcome(string? Text, SkipReason Reason)
{
    public bool Skipped => Text is null;
    public static FilterOutcome Skip(SkipReason reason) => new(null, reason);
}

/// <summary>Applies a <see cref="FilterProfile"/> to chat messages and other spoken text.</summary>
public static class MessageFilter
{
    /// <summary>Whether a chat message must be ignored before its text is even looked at.</summary>
    public static SkipReason CheckIgnore(ChatContext chat, FilterProfile profile, SpeakChatSettings legacy)
    {
        var names = chat.Names().ToArray();
        bool Listed(IEnumerable<string> list) =>
            list.Any(i => !string.IsNullOrWhiteSpace(i)
                          && names.Any(n => string.Equals(n, i.Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase)));

        if (Listed(profile.IgnoredUsers) || Listed(legacy.IgnoredUsers)) return SkipReason.IgnoredUser;
        if (profile.IgnoreBots && (Listed(FilterProfile.KnownBots) || Listed(profile.BotNames))) return SkipReason.Bot;
        if (profile.IgnoreSharedChat && chat.IsSharedChat) return SkipReason.SharedChat;
        if (HasIgnoredPrefix(chat.Message, profile, legacy)) return SkipReason.IgnoredPrefix;
        return SkipReason.None;
    }

    public static bool HasIgnoredPrefix(string message, FilterProfile profile, SpeakChatSettings legacy)
    {
        var trimmed = message.TrimStart();
        var prefixes = profile.IgnoredPrefixes ?? (legacy.IgnoreCommands ? ["!"] : []);
        return prefixes.Any(p => !string.IsNullOrEmpty(p) && trimmed.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Cleans a message body: emotes, emoji, bad words, links, repetition and length limits.</summary>
    /// <param name="emoteRanges">Platform emote positions (Twitch format), or empty.</param>
    /// <param name="emoteNames">Emote names to strip (platform-reported plus provider names).</param>
    public static FilterOutcome Apply(string text, FilterProfile profile, SpeakChatSettings legacy,
        string emoteRanges = "", IReadOnlySet<string>? emoteNames = null)
    {
        if (profile.StripEmotes)
        {
            text = TextFilter.StripEmoteRanges(text, emoteRanges, profile.AllowFirstEmote, profile.AllowedEmotes);
            if (emoteNames is { Count: > 0 })
                text = TextFilter.StripEmoteNames(text, emoteNames, profile.AllowFirstEmote, profile.AllowedEmotes);
        }
        if (profile.StripEmoji) text = TextFilter.StripEmoji(text);

        var afterBadWords = TextFilter.ApplyBadWords(text, profile.BadWords, profile.BadWordAction, profile.BadWordReplacement);
        if (afterBadWords is null) return FilterOutcome.Skip(SkipReason.BadWord);

        var urlMode = profile.UrlMode ?? (legacy.SkipLinks ? UrlMode.Replace : UrlMode.Keep);
        var afterUrls = TextFilter.ApplyUrlMode(afterBadWords, urlMode, string.IsNullOrEmpty(profile.UrlWord) ? TextFilter.LinkWord : profile.UrlWord);
        if (afterUrls is null) return FilterOutcome.Skip(SkipReason.Link);

        text = TextFilter.CompressRepeatedWords(TextFilter.CollapseWhitespace(afterUrls), profile.MaxRepeatedWords);
        text = TextFilter.CompressRepeatedCharacters(text, profile.MaxRepeatedCharacters);
        text = TextFilter.LimitWords(text, profile.MaxWords);
        text = TextFilter.Truncate(TextFilter.CollapseWhitespace(text), profile.MaxCharacters > 0 ? profile.MaxCharacters : legacy.MaxLength);
        return text.Length == 0 ? FilterOutcome.Skip(SkipReason.Empty) : new FilterOutcome(text, SkipReason.None);
    }

    /// <summary>The profile named <paramref name="name"/> (case-insensitive), else the default one.</summary>
    public static FilterProfile Resolve(SpeechSettings settings, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return settings.Filters;
        return settings.FilterProfiles.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
               ?? settings.Filters;
    }
}
