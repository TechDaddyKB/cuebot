using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Cuebot.Core.Commands;

/// <summary>
/// A successful command match. <see cref="Command"/> is the alias (or pattern) that matched, <see cref="Input"/> the
/// text after it, <see cref="Words"/> that text split on whitespace. For regex commands <see cref="Matched"/> is the
/// matched text and <see cref="Groups"/> the capture groups ("1", "2"... for numbered, the name for named groups).
/// </summary>
public sealed record CommandMatch(string Command, string Input, IReadOnlyList<string> Words)
{
    public string Matched { get; init; } = "";
    public IReadOnlyDictionary<string, string> Groups { get; init; } = new Dictionary<string, string>();
}

/// <summary>Pure, platform-neutral matching of a chat message against command texts. Thread-safe.</summary>
public static class CommandMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
    private static readonly ConcurrentDictionary<(string, CommandLocation, bool), Regex?> RegexCache = new();

    /// <summary>The first of <paramref name="commands"/> that matches, in order.</summary>
    public static CommandMatch? MatchAny(string message, IEnumerable<string> commands, CommandMode mode,
        CommandLocation location, bool caseSensitive)
    {
        foreach (var command in commands)
        {
            if (Match(message, command, mode, location, caseSensitive) is { } match) return match;
        }
        return null;
    }

    public static CommandMatch? Match(string message, string command, CommandMode mode, CommandLocation location,
        bool caseSensitive)
    {
        var text = (message ?? "").Trim();
        if (mode == CommandMode.Regex) return MatchRegex(text, command ?? "", location, caseSensitive);

        command = (command ?? "").Trim();
        if (command.Length == 0) return null;
        var cmp = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        switch (location)
        {
            case CommandLocation.Exact:
                return string.Equals(text, command, cmp) ? Create(command, "") : null;
            case CommandLocation.Start:
                if (!text.StartsWith(command, cmp)) return null;
                var rest = text[command.Length..];
                return rest.Length == 0 || char.IsWhiteSpace(rest[0]) ? Create(command, rest) : null;
            default:
                for (var i = text.IndexOf(command, cmp); i >= 0; i = text.IndexOf(command, i + 1, cmp))
                {
                    var end = i + command.Length;
                    if (IsBoundary(i > 0 ? text[i - 1] : null, command[0]) &&
                        IsBoundary(end < text.Length ? text[end] : null, command[^1]))
                    {
                        return Create(command, text[end..]);
                    }
                }
                return null;
        }
    }

    /// <summary>Checks a regex command text; returns null when valid, otherwise the parser's message.</summary>
    public static string? ValidateRegex(string pattern)
    {
        try
        {
            _ = new Regex(pattern, RegexOptions.None, RegexTimeout);
            return null;
        }
        catch (ArgumentException ex)
        {
            return ex.Message;
        }
    }

    /// <summary>A neighbour is a boundary when absent, whitespace, or a non-word character next to a word character.</summary>
    private static bool IsBoundary(char? neighbour, char edge) =>
        neighbour is not { } n || char.IsWhiteSpace(n) || (IsWord(edge) && !IsWord(n));

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static CommandMatch Create(string command, string rest)
    {
        var input = rest.Trim();
        return new CommandMatch(command, input, Split(input));
    }

    private static string[] Split(string input) => input.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    private static CommandMatch? MatchRegex(string text, string pattern, CommandLocation location, bool caseSensitive)
    {
        if (pattern.Trim().Length == 0) return null;
        var regex = RegexCache.GetOrAdd((pattern, location, caseSensitive), key => Build(key.Item1, key.Item2, key.Item3));
        if (regex is null) return null;

        Match m;
        try
        {
            m = regex.Match(text);
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
        if (!m.Success) return null;

        var groups = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var number in regex.GetGroupNumbers())
        {
            if (number == 0) continue;
            var group = m.Groups[number];
            groups[regex.GroupNameFromNumber(number)] = group.Success ? group.Value : "";
        }
        var input = text[(m.Index + m.Length)..].Trim();
        return new CommandMatch(pattern, input, Split(input)) { Matched = m.Value, Groups = groups };
    }

    private static Regex? Build(string pattern, CommandLocation location, bool caseSensitive)
    {
        var anchored = location switch
        {
            CommandLocation.Start => $"^(?:{pattern})",
            CommandLocation.Exact => $"^(?:{pattern})$",
            _ => pattern,
        };
        var options = RegexOptions.CultureInvariant | (caseSensitive ? RegexOptions.None : RegexOptions.IgnoreCase);
        try
        {
            return new Regex(anchored, options, RegexTimeout);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
