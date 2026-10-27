namespace Cuebot.App.ViewModels.Chat;

/// <summary>A "/name argument" line typed in the chat send box.</summary>
public sealed record SlashCommand(string Name, string Argument)
{
    /// <summary>Commands the send box understands, with one-line help.</summary>
    public static readonly IReadOnlyList<(string Usage, string Help)> Known =
    [
        ("/action <name> [input]", "run an action (gets the selected message's arguments and rawInput)"),
        ("/announce <text>", "post an announcement where the platform supports it"),
        ("/me <text>", "post an action line where the platform supports it"),
        ("/clear", "clear this chat view (not the platforms' chat)"),
        ("/help", "list these commands"),
        ("//text", "send text starting with a slash"),
    ];

    /// <summary>Parses a slash command, or returns null for ordinary text (including "//escaped" text).</summary>
    public static SlashCommand? Parse(string text)
    {
        var trimmed = text.TrimStart();
        if (trimmed.Length < 2 || trimmed[0] != '/' || trimmed[1] == '/' || char.IsWhiteSpace(trimmed[1])) return null;
        var space = trimmed.IndexOfAny([' ', '\t']);
        var name = (space < 0 ? trimmed[1..] : trimmed[1..space]).ToLowerInvariant();
        var argument = space < 0 ? "" : trimmed[(space + 1)..].Trim();
        return new SlashCommand(name, argument);
    }

    /// <summary>"//text" → "/text"; anything else unchanged.</summary>
    public static string Unescape(string text)
    {
        var trimmed = text.TrimStart();
        return trimmed.StartsWith("//", StringComparison.Ordinal) ? trimmed[1..] : text;
    }

    /// <summary>
    /// Splits "/action" arguments into an action name and input: the longest leading run of words that names an
    /// existing action wins (names may contain spaces); the rest is input.
    /// </summary>
    public static (string Name, string Input)? SplitAction(string argument, Func<string, bool> exists)
    {
        var words = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var count = words.Length; count > 0; count--)
        {
            var name = string.Join(' ', words.Take(count));
            if (exists(name)) return (name, string.Join(' ', words.Skip(count)));
        }
        return null;
    }
}
