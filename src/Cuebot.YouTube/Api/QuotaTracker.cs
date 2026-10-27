namespace Cuebot.YouTube.Api;

/// <summary>
/// YouTube Data API v3 quota costs (units) of the calls Cuebot makes, from Google's published quota table. A project
/// gets 10,000 units per day by default; the count resets at midnight Pacific time. See YOUTUBE.md.
/// </summary>
internal static class QuotaCosts
{
    public const int ChannelsList = 1;
    public const int VideosList = 1;
    public const int SubscriptionsList = 1;
    public const int LiveBroadcastsList = 5;
    public const int LiveChatMessagesList = 5;
    public const int LiveChatModeratorsList = 50;
    public const int LiveChatMessagesInsert = 50;
    public const int LiveChatMessagesDelete = 50;
    public const int LiveChatMessagesTransition = 50;
    public const int LiveChatBansInsert = 50;
    public const int LiveChatBansDelete = 50;
    public const int LiveChatModeratorsInsert = 50;
    public const int LiveChatModeratorsDelete = 50;
    public const int VideosUpdate = 50;

    /// <summary>
    /// Units per hour spent by polling alone. <paramref name="chatSeconds"/> is the effective chat interval;
    /// chat and live statistics are only polled while live.
    /// </summary>
    public static (double Live, double Idle) EstimatePerHour(double chatSeconds, double broadcastSeconds, double liveSeconds,
        double subscriberSeconds, bool monitorUpcoming, bool pollSubscribers)
    {
        static double PerHour(double seconds, int cost) => seconds > 0 ? 3600 / seconds * cost : 0;
        var broadcast = PerHour(broadcastSeconds, LiveBroadcastsList) * (monitorUpcoming ? 2 : 1);
        var subscribers = pollSubscribers ? PerHour(subscriberSeconds, SubscriptionsList) : 0;
        var idle = broadcast + subscribers;
        var live = idle + PerHour(chatSeconds, LiveChatMessagesList) + PerHour(liveSeconds, VideosList);
        return (Math.Round(live), Math.Round(idle));
    }
}

/// <summary>
/// Counts the quota units Cuebot spends per Google quota day (Pacific time) and remembers when YouTube reported the
/// quota exhausted, so polling loops can pause until the reset instead of hammering the API.
/// </summary>
internal sealed class QuotaTracker(TimeProvider time)
{
    private static readonly TimeZoneInfo Pacific = FindPacific();
    private readonly object _gate = new();
    private string _day = "";
    private int _used;
    private DateTimeOffset? _exhaustedUntil;

    /// <summary>Raised after every change (spend, exhaustion, reset); handlers must be quick.</summary>
    public event Action? Changed;

    /// <summary>Raised once when YouTube first reports the quota exhausted for the day.</summary>
    public event Action<DateTimeOffset>? Exhausted;

    public int UsedToday
    {
        get { lock (_gate) { Roll(); return _used; } }
    }

    public string Day
    {
        get { lock (_gate) { Roll(); return _day; } }
    }

    /// <summary>When the quota resets (next midnight Pacific).</summary>
    public DateTimeOffset NextReset => NextResetAfter(time.GetUtcNow());

    /// <summary>Non-null while the daily quota is exhausted: when polling may resume.</summary>
    public DateTimeOffset? ExhaustedUntil
    {
        get
        {
            lock (_gate)
            {
                if (_exhaustedUntil is { } until && until <= time.GetUtcNow()) _exhaustedUntil = null;
                return _exhaustedUntil;
            }
        }
    }

    /// <summary>Restores the persisted counter if it belongs to the current quota day.</summary>
    public void Restore(string day, int used)
    {
        lock (_gate)
        {
            Roll();
            if (day == _day) _used = Math.Max(_used, used);
        }
    }

    public void Spend(int units)
    {
        lock (_gate)
        {
            Roll();
            _used += units;
        }
        Changed?.Invoke();
    }

    public void MarkExhausted()
    {
        DateTimeOffset until;
        bool first;
        lock (_gate)
        {
            first = _exhaustedUntil is null;
            until = NextResetAfter(time.GetUtcNow());
            _exhaustedUntil = until;
        }
        Changed?.Invoke();
        if (first) Exhausted?.Invoke(until);
    }

    /// <summary>Waits until the quota resets when it is exhausted; returns immediately otherwise.</summary>
    public async Task WaitWhileExhaustedAsync(CancellationToken ct)
    {
        while (ExhaustedUntil is { } until)
        {
            var wait = until - time.GetUtcNow();
            if (wait > TimeSpan.Zero) await Task.Delay(wait, time, ct).ConfigureAwait(false);
        }
    }

    internal static DateTimeOffset NextResetAfter(DateTimeOffset now)
    {
        var local = TimeZoneInfo.ConvertTime(now, Pacific);
        var midnight = new DateTime(local.Year, local.Month, local.Day, 0, 0, 0, DateTimeKind.Unspecified).AddDays(1);
        return new DateTimeOffset(midnight, Pacific.GetUtcOffset(midnight));
    }

    internal static string DayOf(DateTimeOffset now) => TimeZoneInfo.ConvertTime(now, Pacific).ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    private void Roll()
    {
        var today = DayOf(time.GetUtcNow());
        if (today == _day) return;
        _day = today;
        _used = 0;
    }

    private static TimeZoneInfo FindPacific()
    {
        foreach (var id in (string[])["America/Los_Angeles", "Pacific Standard Time"])
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
            }
        }
        return TimeZoneInfo.CreateCustomTimeZone("Pacific", TimeSpan.FromHours(-8), "Pacific", "Pacific");
    }
}
