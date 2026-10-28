using System.Globalization;

namespace Cuebot.Core.Variables;

/// <summary>
/// Variables available in every text field. Action arguments with the same name win.
/// <list type="table">
/// <item><term>%date%</term><description>local date as Streamer.bot writes it, "yyyy/MM/dd" (the "/" is the culture's date
/// separator); <c>%date:fmt%</c> formats the current local date/time</description></item>
/// <item><term>%time%</term><description>local time as Streamer.bot writes it, "hh:mm" (12-hour, no AM/PM); <c>%time:fmt%</c>
/// likewise, so <c>%time:t%</c> is the culture's short time</description></item>
/// <item><term>%longtime%</term><description>local time with seconds ("T")</description></item>
/// <item><term>%datetime%</term><description>local date and time ("g")</description></item>
/// <item><term>%unixtime%</term><description>seconds since 1970-01-01 UTC; %unixtimems% in milliseconds</description></item>
/// <item><term>%filedatetime%</term><description>file-name-safe local timestamp "yyyyMMdd_HHmmss"</description></item>
/// <item><term>%lf% %cr% %crlf% %tab%</term><description>line feed, carriage return, CR+LF, tab</description></item>
/// <item><term>%random%</term><description>integer 1-100; <c>%random:a-b%</c> integer between a and b inclusive</description></item>
/// <item><term>%randomdecimal%</term><description>real number in [0, 1)</description></item>
/// <item><term>%guid%</term><description>a new random identifier</description></item>
/// </list>
/// </summary>
public static class BuiltInVariables
{
    /// <summary>Format of %date% without one: Streamer.bot's.</summary>
    public const string DateFormat = "yyyy/MM/dd";

    /// <summary>Format of %time% without one: Streamer.bot's.</summary>
    public const string TimeFormat = "hh:mm";

    public static IReadOnlyList<string> Names { get; } =
    [
        "date", "time", "longtime", "datetime", "unixtime", "unixtimems", "filedatetime",
        "lf", "cr", "crlf", "tab", "random", "randomdecimal", "guid",
    ];

    /// <summary>
    /// Looks up a built-in. <paramref name="format"/> is replaced by the format to apply (the default one when
    /// none was given, or <c>null</c> when the built-in consumed it, like <c>%random:1-6%</c>).
    /// </summary>
    public static bool TryGet(string name, ref string? format, TextEnvironment env, out object? value)
    {
        switch (name.ToLowerInvariant())
        {
            case "date":
                value = env.LocalNow();
                format ??= DateFormat;
                return true;
            case "time":
                value = env.LocalNow();
                format ??= TimeFormat;
                return true;
            case "longtime":
                value = env.LocalNow();
                format ??= "T";
                return true;
            case "datetime":
                value = env.LocalNow();
                format ??= "g";
                return true;
            case "filedatetime":
                value = env.LocalNow();
                format ??= "yyyyMMdd_HHmmss";
                return true;
            case "unixtime":
                value = env.Clock.GetUtcNow().ToUnixTimeSeconds();
                return true;
            case "unixtimems":
                value = env.Clock.GetUtcNow().ToUnixTimeMilliseconds();
                return true;
            case "lf":
                value = "\n";
                return true;
            case "cr":
                value = "\r";
                return true;
            case "crlf":
                value = "\r\n";
                return true;
            case "tab":
                value = "\t";
                return true;
            case "guid":
                value = Guid.NewGuid().ToString();
                return true;
            case "randomdecimal":
                value = env.Random.NextDouble();
                return true;
            case "random":
                long min = 1, max = 100;
                if (format is not null)
                {
                    if (!TryParseRange(format, out min, out max))
                    {
                        value = null;
                        return false;
                    }
                    format = null;
                }
                value = env.Random.NextInt64(min, max + 1);
                return true;
            default:
                value = null;
                return false;
        }
    }

    /// <summary>Parses "a-b" (also "-5-5" or "1..6") into an inclusive range, swapping reversed bounds.</summary>
    internal static bool TryParseRange(string text, out long min, out long max)
    {
        min = max = 0;
        var span = text.AsSpan().Trim();
        var split = span.IndexOf("..", StringComparison.Ordinal);
        int skip = 2;
        if (split < 0)
        {
            split = span.Length > 1 ? span[1..].IndexOf('-') : -1;
            if (split >= 0) split++;
            skip = 1;
        }
        if (split <= 0) return false;
        var inv = CultureInfo.InvariantCulture;
        if (!long.TryParse(span[..split].Trim(), NumberStyles.AllowLeadingSign, inv, out min)) return false;
        if (!long.TryParse(span[(split + skip)..].Trim(), NumberStyles.AllowLeadingSign, inv, out max)) return false;
        if (min > max) (min, max) = (max, min);
        return max < long.MaxValue;
    }
}
