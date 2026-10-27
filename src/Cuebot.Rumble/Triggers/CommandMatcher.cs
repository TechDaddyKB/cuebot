namespace Cuebot.Rumble.Triggers;

/// <summary>How a chat message is compared with a command.</summary>
internal enum CommandMatchMode
{
    /// <summary>The message begins with the command as a whole word ("!so bob" matches "!so", "!sob" does not).</summary>
    StartsWith,

    /// <summary>The message is exactly the command (surrounding whitespace ignored).</summary>
    Exact,
}

/// <summary>A successful command match: the text after the command and its whitespace-separated words.</summary>
internal sealed record CommandMatch(string Command, string Input, IReadOnlyList<string> Words);

/// <summary>Pure matching of chat messages against a command such as "!hello".</summary>
internal static class CommandMatcher
{
    public const string StartsWithChoice = "Starts with";
    public const string ExactChoice = "Exact";

    public static CommandMatchMode ParseMode(string choice) =>
        string.Equals(choice, ExactChoice, StringComparison.OrdinalIgnoreCase) ? CommandMatchMode.Exact : CommandMatchMode.StartsWith;

    public static CommandMatch? Match(string message, string command, CommandMatchMode mode, bool caseSensitive)
    {
        command = command.Trim();
        var text = message.Trim();
        if (command.Length == 0) return null;
        if (!text.StartsWith(command, caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase)) return null;

        var rest = text[command.Length..];
        if (rest.Length > 0 && (mode == CommandMatchMode.Exact || !char.IsWhiteSpace(rest[0]))) return null;

        var input = rest.Trim();
        return new CommandMatch(command, input, input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
