using System.Globalization;

namespace Cuebot.Core.Commands;

/// <summary>
/// A classic 5-field cron schedule: minute (0-59), hour (0-23), day of month (1-31), month (1-12 or JAN-DEC),
/// day of week (0-7 or SUN-SAT; 0 and 7 are Sunday). Fields accept <c>*</c>, values, ranges <c>a-b</c>, steps
/// <c>*/n</c>, <c>a-b/n</c>, <c>a/n</c> and comma lists. <c>?</c> means <c>*</c>. Macros: @yearly, @annually,
/// @monthly, @weekly, @daily, @midnight, @hourly. When both day fields are restricted a day matches either
/// (standard cron behaviour).
/// </summary>
public sealed class CronExpression
{
    private static readonly string[] MonthNames = ["JAN", "FEB", "MAR", "APR", "MAY", "JUN", "JUL", "AUG", "SEP", "OCT", "NOV", "DEC"];
    private static readonly string[] DayNames = ["SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT"];

    private readonly bool[] _minutes;
    private readonly bool[] _hours;
    private readonly bool[] _daysOfMonth;
    private readonly bool[] _months;
    private readonly bool[] _daysOfWeek;
    private readonly bool _domRestricted;
    private readonly bool _dowRestricted;

    private CronExpression(string text, bool[] minutes, bool[] hours, bool[] dom, bool[] months, bool[] dow,
        bool domRestricted, bool dowRestricted)
    {
        Text = text;
        _minutes = minutes;
        _hours = hours;
        _daysOfMonth = dom;
        _months = months;
        _daysOfWeek = dow;
        _domRestricted = domRestricted;
        _dowRestricted = dowRestricted;
    }

    public string Text { get; }

    public static CronExpression Parse(string text) =>
        TryParse(text, out var cron, out var error) ? cron : throw new FormatException(error);

    public static bool TryParse(string text, out CronExpression cron, out string error)
    {
        cron = null!;
        error = "";
        var source = (text ?? "").Trim();
        var expanded = source.ToLowerInvariant() switch
        {
            "@yearly" or "@annually" => "0 0 1 1 *",
            "@monthly" => "0 0 1 * *",
            "@weekly" => "0 0 * * 0",
            "@daily" or "@midnight" => "0 0 * * *",
            "@hourly" => "0 * * * *",
            _ => source,
        };
        var fields = expanded.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length != 5)
        {
            error = "A cron schedule needs 5 fields: minute hour day-of-month month day-of-week";
            return false;
        }

        if (!TryField(fields[0], 0, 59, null, "minute", out var minutes, out _, ref error)
            || !TryField(fields[1], 0, 23, null, "hour", out var hours, out _, ref error)
            || !TryField(fields[2], 1, 31, null, "day of month", out var dom, out var domRestricted, ref error)
            || !TryField(fields[3], 1, 12, MonthNames, "month", out var months, out _, ref error)
            || !TryField(fields[4], 0, 7, DayNames, "day of week", out var dow, out var dowRestricted, ref error))
        {
            return false;
        }
        if (dow[7]) dow[0] = true;
        cron = new CronExpression(source, minutes, hours, dom, months, dow, domRestricted, dowRestricted);
        return true;
    }

    /// <summary>True when the given local wall-clock minute is part of the schedule.</summary>
    public bool Matches(DateTime local) =>
        _minutes[local.Minute] && _hours[local.Hour] && _months[local.Month] && DayMatches(local);

    /// <summary>
    /// The first scheduled minute strictly after <paramref name="after"/>, evaluated in <paramref name="zone"/>
    /// (local time by default). Wall-clock times skipped by a daylight-saving change are skipped. Null when the
    /// schedule never occurs (e.g. 31 February) within the next five years.
    /// </summary>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTime(after, zone).DateTime;
        var t = new DateTime(local.Year, local.Month, local.Day, local.Hour, local.Minute, 0, DateTimeKind.Unspecified).AddMinutes(1);
        var limit = t.AddYears(5);
        while (t <= limit)
        {
            if (!_months[t.Month])
            {
                t = new DateTime(t.Year, t.Month, 1, 0, 0, 0, DateTimeKind.Unspecified).AddMonths(1);
                continue;
            }
            if (!DayMatches(t))
            {
                t = t.Date.AddDays(1);
                continue;
            }
            if (!_hours[t.Hour])
            {
                t = t.Date.AddHours(t.Hour + 1);
                continue;
            }
            if (!_minutes[t.Minute] || zone.IsInvalidTime(t))
            {
                t = t.AddMinutes(1);
                continue;
            }
            var result = new DateTimeOffset(t, zone.GetUtcOffset(t));
            if (result > after) return result;
            t = t.AddMinutes(1);
        }
        return null;
    }

    public override string ToString() => Text;

    private bool DayMatches(DateTime t)
    {
        var dom = _daysOfMonth[t.Day];
        var dow = _daysOfWeek[(int)t.DayOfWeek];
        if (_domRestricted && _dowRestricted) return dom || dow;
        return dom && dow;
    }

    private static bool TryField(string field, int min, int max, string[]? names, string label,
        out bool[] values, out bool restricted, ref string error)
    {
        values = new bool[max + 1];
        restricted = field is not ("*" or "?");
        foreach (var part in field.Split(','))
        {
            if (!TryPart(part, min, max, names, values))
            {
                error = $"Invalid {label} field '{field}' (allowed {min}-{max})";
                return false;
            }
        }
        return true;
    }

    private static bool TryPart(string part, int min, int max, string[]? names, bool[] values)
    {
        if (part.Length == 0) return false;
        var step = 1;
        var slash = part.IndexOf('/');
        var range = part;
        if (slash >= 0)
        {
            if (!int.TryParse(part[(slash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out step) || step <= 0) return false;
            range = part[..slash];
        }

        int from, to;
        if (range is "*" or "?")
        {
            from = min;
            to = max;
        }
        else
        {
            var dash = range.IndexOf('-', 1);
            if (dash > 0)
            {
                if (!TryValue(range[..dash], names, min, out from) || !TryValue(range[(dash + 1)..], names, min, out to)) return false;
            }
            else
            {
                if (!TryValue(range, names, min, out from)) return false;
                to = slash >= 0 ? max : from; // "a/n" means a through max every n
            }
        }
        if (from < min || to > max || from > to) return false;
        for (var v = from; v <= to; v += step) values[v] = true;
        return true;
    }

    private static bool TryValue(string text, string[]? names, int min, out int value)
    {
        if (int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out value)) return true;
        if (names is not null)
        {
            var index = Array.FindIndex(names, n => string.Equals(n, text, StringComparison.OrdinalIgnoreCase));
            if (index >= 0)
            {
                value = index + min; // months start at 1, days at 0
                return true;
            }
        }
        return false;
    }
}
