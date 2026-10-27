using System.Globalization;

namespace Cuebot.YouTube;

/// <summary>Typed reads of <c>BotEvent.Args</c>, tolerant of numbers stored as numbers or strings.</summary>
internal static class EventArgsExtensions
{
    public static double GetDouble(this IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var value) ? value switch
        {
            int i => i,
            long l => l,
            double d => d,
            decimal m => (double)m,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0,
        } : 0;
}
