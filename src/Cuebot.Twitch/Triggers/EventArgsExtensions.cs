using System.Globalization;

namespace Cuebot.Twitch.Triggers;

/// <summary>Typed reads of <c>BotEvent.Args</c>, tolerant of numbers stored as int, long or string.</summary>
internal static class EventArgsExtensions
{
    public static string GetString(this IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var value) ? Convert.ToString(value, CultureInfo.InvariantCulture) ?? "" : "";

    public static long GetLong(this IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var value) ? value switch
        {
            int i => i,
            long l => l,
            double d => (long)d,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        } : 0;

    public static bool GetBool(this IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var value) && value switch
        {
            bool b => b,
            string s => bool.TryParse(s, out var parsed) && parsed,
            _ => false,
        };
}
