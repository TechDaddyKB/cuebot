using System.Globalization;
using System.Text.RegularExpressions;

namespace Cuebot.Core.Variables;

/// <summary>
/// Helpers for adding Streamer.bot's documented argument names next to Cuebot's own, so imported Streamer.bot
/// actions (which read <c>%rewardName%</c>, <c>%rawInputEscaped%</c>, ...) find their values. Nothing here
/// overwrites an argument that is already set.
/// </summary>
public static class StreamerBotArgs
{
    /// <summary>Copies <paramref name="from"/> to <paramref name="name"/> when the source exists and the target does not.</summary>
    public static void Alias(IDictionary<string, object?> args, string name, string from)
    {
        if (!args.ContainsKey(name) && args.TryGetValue(from, out var value)) args[name] = value;
    }

    /// <summary>
    /// A variable Streamer.bot documents but the platform's API cannot supply: set to a neutral value of the documented
    /// type (0, false, "", an empty list, <c>default(DateTime)</c>) unless already present, so actions written for
    /// Streamer.bot that read it do not fail. Each module lists its placeholders in <c>docs/PARITY.md</c>.
    /// </summary>
    public static void Placeholder(IDictionary<string, object?> args, string name, object? value) => Set(args, name, value);

    /// <summary>Sets <paramref name="name"/> unless it is already present.</summary>
    public static void Set(IDictionary<string, object?> args, string name, object? value)
    {
        if (!args.ContainsKey(name)) args[name] = value;
    }

    /// <summary>
    /// Streamer.bot's "Escaped" form: characters that are special in regular expressions and in Streamer.bot's
    /// own variable syntax get a backslash; spaces are kept ("https://streamer\.bot/Test Text \$\$").
    /// </summary>
    public static string Escape(string? text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var escaped = Regex.Escape(text);
        // Regex.Escape also escapes white space, which Streamer.bot keeps as is.
        return escaped.Replace("\\ ", " ", StringComparison.Ordinal).Replace("\\t", "\t", StringComparison.Ordinal)
            .Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\r", "\r", StringComparison.Ordinal);
    }

    /// <summary>URL encoding as used by <c>rawInputUrlEncoded</c> (spaces as %20).</summary>
    public static string UrlEncode(string? text) => string.IsNullOrEmpty(text) ? "" : Uri.EscapeDataString(text);

    /// <summary>
    /// <c>rawInput</c> (when missing), <c>rawInputEscaped</c>, <c>rawInputUrlEncoded</c> and, when
    /// <paramref name="words"/> is set, <c>input#</c>, <c>inputEscaped#</c> and <c>inputUrlEncoded#</c> per word.
    /// </summary>
    public static void AddInput(IDictionary<string, object?> args, string? rawInput, bool words = true)
    {
        var text = rawInput ?? "";
        Set(args, "rawInput", text);
        Set(args, "rawInputEscaped", Escape(text));
        Set(args, "rawInputUrlEncoded", UrlEncode(text));
        if (!words) return;
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length; i++)
        {
            var n = i.ToString(CultureInfo.InvariantCulture);
            Set(args, "input" + n, parts[i]);
            Set(args, "inputEscaped" + n, Escape(parts[i]));
            Set(args, "inputUrlEncoded" + n, UrlEncode(parts[i]));
        }
    }

    /// <summary><c>colorR</c>, <c>colorG</c>, <c>colorB</c> from a "#RRGGBB" color (nothing for other text).</summary>
    public static void AddColor(IDictionary<string, object?> args, string? color)
    {
        var hex = (color ?? "").Trim().TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb)) return;
        Set(args, "colorR", (rgb >> 16) & 0xFF);
        Set(args, "colorG", (rgb >> 8) & 0xFF);
        Set(args, "colorB", rgb & 0xFF);
    }

    /// <summary>An ISO 8601 time (string or DateTimeOffset) as a local <see cref="DateTime"/>, like Streamer.bot's DateTime arguments.</summary>
    public static DateTime? Time(object? value) => value switch
    {
        DateTime d => d,
        DateTimeOffset o => o.LocalDateTime,
        string s when s.Length > 0 && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            => t.LocalDateTime,
        _ => null,
    };

    /// <summary>Sets <paramref name="name"/> to <see cref="Time"/> of <paramref name="value"/> when it parses.</summary>
    public static void SetTime(IDictionary<string, object?> args, string name, object? value)
    {
        if (Time(value) is { } time) Set(args, name, time);
    }

    /// <summary>
    /// Changes an argument holding a time as text (ISO 8601) into a local <see cref="DateTime"/> in place, for names
    /// Streamer.bot types as DateTime, so C# casts and date formats work. Other values are left alone.
    /// </summary>
    public static void ToTime(IDictionary<string, object?> args, string name)
    {
        if (args.TryGetValue(name, out var value) && value is string or DateTimeOffset && Time(value) is { } time) args[name] = time;
    }

    /// <summary>Changes a whole-number argument into an <see cref="int"/> in place (Streamer.bot's Int32), when it fits.</summary>
    public static void ToInt(IDictionary<string, object?> args, string name)
    {
        if (args.TryGetValue(name, out var value) && value is long l and >= int.MinValue and <= int.MaxValue) args[name] = (int)l;
    }

    /// <summary>Changes a numeric argument into a <see cref="decimal"/> in place (Streamer.bot's type for money and durations).</summary>
    public static void ToDecimal(IDictionary<string, object?> args, string name)
    {
        if (!args.TryGetValue(name, out var value)) return;
        decimal? d = value switch
        {
            int i => i,
            long l => l,
            double x when double.IsFinite(x) => (decimal)x,
            float f when float.IsFinite(f) => (decimal)f,
            _ => null,
        };
        if (d is { } number) args[name] = number;
    }

    /// <summary>The argument as text ("" when missing).</summary>
    public static string Text(IReadOnlyDictionary<string, object?> args, string name) =>
        args.TryGetValue(name, out var v) && v is not null ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";

    /// <summary>The argument as a number (0 when missing or not numeric).</summary>
    public static long Number(IReadOnlyDictionary<string, object?> args, string name) => args.TryGetValue(name, out var v) ? v switch
    {
        int i => i,
        long l => l,
        double d => (long)d,
        decimal m => (long)m,
        string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
        _ => 0,
    } : 0;

    /// <summary>True when the argument is the boolean true (or the text "true").</summary>
    public static bool Flag(IReadOnlyDictionary<string, object?> args, string name) =>
        args.TryGetValue(name, out var v) && (v is true || (v is string s && bool.TryParse(s, out var b) && b));
}
