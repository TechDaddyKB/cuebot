using Cuebot.Core.Chat;

namespace Cuebot.App.ViewModels.Chat;

/// <summary>chat-page.json: how the combined chat page looks and behaves (display, highlights, quick actions).</summary>
public sealed class ChatPageSettings
{
    public bool ShowEmotes { get; set; } = true;

    /// <summary>Show alejo.io pronouns next to Twitch names.</summary>
    public bool ShowPronouns { get; set; } = true;

    public HighlightRules Highlights { get; set; } = new();

    public List<QuickActionDefinition> QuickActions { get; set; } = [];
}

/// <summary>Which messages stand out in the combined chat.</summary>
public sealed class HighlightRules
{
    /// <summary>Highlight messages that mention one of <see cref="MentionNames"/> ("@name" or the bare name).</summary>
    public bool Mentions { get; set; } = true;

    /// <summary>Your names on each platform (broadcaster, bot...).</summary>
    public List<string> MentionNames { get; set; } = [];

    /// <summary>Words or phrases that highlight a message (case-insensitive).</summary>
    public List<string> Keywords { get; set; } = [];

    /// <summary>
    /// Highlight a chatter's first message, when the platform flags it (event arguments isFirstMessage, firstMessage,
    /// isFirstTime or firstTime).
    /// </summary>
    public bool FirstTimeChatters { get; set; } = true;
}

/// <summary>What a quick action needs selected, and which arguments it gets.</summary>
public enum QuickActionScope
{
    /// <summary>Needs a selected message; gets message and user arguments.</summary>
    Message,

    /// <summary>Needs a selected message; gets only the user's arguments.</summary>
    User,

    /// <summary>Always available; gets the selection's arguments when there is one.</summary>
    Global,
}

/// <summary>A user-configured button that runs an action from the chat page.</summary>
public sealed class QuickActionDefinition
{
    public string Label { get; set; } = "";
    public string ActionName { get; set; } = "";
    public QuickActionScope Scope { get; set; } = QuickActionScope.Message;
}

/// <summary>Why a message is highlighted.</summary>
public enum HighlightKind { None, Mention, Keyword, FirstTime }

public readonly record struct HighlightResult(HighlightKind Kind, string Reason)
{
    public static readonly HighlightResult None = new(HighlightKind.None, "");
    public bool IsHighlighted => Kind != HighlightKind.None;
}

/// <summary>Applies <see cref="HighlightRules"/> to a message.</summary>
public static class ChatHighlighter
{
    private static readonly string[] FirstMessageKeys = ["isFirstMessage", "firstMessage", "isFirstTime", "firstTime"];

    /// <param name="raw">The platform event's arguments (for first-message flags), when known.</param>
    public static HighlightResult Evaluate(ChatMessage message, IReadOnlyDictionary<string, object?>? raw, HighlightRules rules)
    {
        var text = message.Text;
        if (rules.Mentions)
            foreach (var name in rules.MentionNames.Select(n => n.Trim().TrimStart('@')).Where(n => n.Length > 0))
                if (MentionsName(text, name))
                    return new HighlightResult(HighlightKind.Mention, $"Mentions {name}");

        foreach (var keyword in rules.Keywords.Select(k => k.Trim()).Where(k => k.Length > 0))
            if (text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return new HighlightResult(HighlightKind.Keyword, $"Keyword \"{keyword}\"");

        if (rules.FirstTimeChatters && raw is not null && FirstMessageKeys.Any(k => IsTrue(raw.GetValueOrDefault(k))))
            return new HighlightResult(HighlightKind.FirstTime, "First message");

        return HighlightResult.None;
    }

    /// <summary>"@name" anywhere, or the name as a whole word.</summary>
    internal static bool MentionsName(string text, string name)
    {
        var index = 0;
        while ((index = text.IndexOf(name, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            var end = index + name.Length;
            var before = index == 0 || !IsWordChar(text[index - 1]) || text[index - 1] == '@';
            var after = end >= text.Length || !IsWordChar(text[end]);
            if (before && after) return true;
            index = end;
        }
        return false;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool IsTrue(object? value) => value switch
    {
        bool b => b,
        string s => bool.TryParse(s, out var parsed) && parsed,
        _ => false,
    };
}
