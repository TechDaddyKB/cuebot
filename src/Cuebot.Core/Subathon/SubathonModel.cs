namespace Cuebot.Core.Subathon;

/// <summary>
/// What each kind of event is worth to one timer (seconds) or one goal (points). 0 means it does not count. Money is
/// per 1 of the streamer's currency (Top Spenders), at the stream's locked exchange rate; gift subs count as one sub each.
/// </summary>
public sealed class EventValues
{
    public double Follow { get; set; }
    public double TwitchTier1 { get; set; }
    public double TwitchTier2 { get; set; }
    public double TwitchTier3 { get; set; }
    public double KickSub { get; set; }
    public double YouTubeMembership { get; set; }

    /// <summary>
    /// Subscriptions that come without a price (Rumble). Paid memberships from Ko-fi, Patreon and Fourthwall arrive as
    /// money, so they follow <see cref="PerCurrencyUnit"/> or their service's <see cref="MoneyBySource"/> value.
    /// </summary>
    public double OtherSub { get; set; }

    /// <summary>Gift subs count like subs (one per sub gifted); off: gift subs do not count.</summary>
    public bool CountGiftSubs { get; set; } = true;

    public double Per100Bits { get; set; }
    public double Per100Kicks { get; set; }
    public double Per100Jewels { get; set; }

    /// <summary>Tips, Super Chats, rants, donations and shop orders: per 1 of the chosen currency.</summary>
    public double PerCurrencyUnit { get; set; }

    /// <summary>
    /// Money from one service worth something else than <see cref="PerCurrencyUnit"/>, by the platform or service name
    /// the purchase arrives with (Throne, Fourthwall, Ko-fi, YouTube...). 0 = money from it does not count; a service
    /// that is not listed uses <see cref="PerCurrencyUnit"/>.
    /// </summary>
    public Dictionary<string, double> MoneyBySource { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The services money can come from, as they name themselves on purchases (the list the app offers).</summary>
    public static IReadOnlyList<string> MoneySources { get; } =
    [
        "YouTube", "Rumble", "Twitch", "Ko-fi", "Patreon", "Fourthwall", "Shopify", "Throne",
        "Streamlabs", "StreamElements", "TipeeeStream", "TreatStream", "Pally.gg", "DonorDrive", "Manual",
    ];

    /// <summary>What 1 of money from <paramref name="source"/> is worth (matched ignoring capitals: saved lists lose their comparer).</summary>
    public double MoneyValue(string source)
    {
        foreach (var (key, value) in MoneyBySource)
            if (string.Equals(key.Trim(), source.Trim(), StringComparison.OrdinalIgnoreCase)) return value;
        return PerCurrencyUnit;
    }

    public double RaidEach { get; set; }
    public double RaidPerViewer { get; set; }

    /// <summary>Channel point rewards (any platform) by reward title.</summary>
    public Dictionary<string, double> Redemptions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A subathon's usual starting values, in seconds: 5 minutes a sub, 1 minute per 100 bits or Kicks or per 1 of money.</summary>
    public static EventValues TimerDefaults() => new()
    {
        TwitchTier1 = 300, TwitchTier2 = 600, TwitchTier3 = 1500, KickSub = 300, YouTubeMembership = 300, OtherSub = 300,
        Per100Bits = 60, Per100Kicks = 60, Per100Jewels = 60, PerCurrencyUnit = 60,
    };

    /// <summary>A sub goal's usual starting values, in points: 1 per sub (2 and 5 for tiers 2 and 3).</summary>
    public static EventValues GoalDefaults() => new()
    {
        TwitchTier1 = 1, TwitchTier2 = 2, TwitchTier3 = 5, KickSub = 1, YouTubeMembership = 1, OtherSub = 1,
    };
}

public enum TimerState { Stopped, Running, Paused, Ended }

/// <summary>A subathon / donothon countdown that events add time to.</summary>
public sealed class SubathonTimer
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Subathon";

    /// <summary>The length a new (or reset) timer starts with.</summary>
    public double InitialSeconds { get; set; } = 4 * 3600;

    /// <summary>The longest it may ever run in total (time already run plus time left); 0 = no limit.</summary>
    public double MaxSeconds { get; set; }

    /// <summary>Everything events add is multiplied by this ("happy hour" 2, or 0.5 to slow it down); manual changes are not.</summary>
    public double Multiplier { get; set; } = 1;

    /// <summary>Events add time while the timer is paused too (never while stopped or ended).</summary>
    public bool AddWhilePaused { get; set; }

    public bool StartOnStreamOnline { get; set; }
    public bool PauseOnStreamOffline { get; set; }

    public EventValues Values { get; set; } = EventValues.TimerDefaults();

    public TimerState State { get; set; } = TimerState.Stopped;

    /// <summary>Time left when not running; while running, time left at <see cref="SavedAt"/> (frozen there if Cuebot closes).</summary>
    public double RemainingSeconds { get; set; } = 4 * 3600;

    /// <summary>When it reaches zero, while running.</summary>
    public DateTimeOffset? EndsAt { get; set; }

    /// <summary>Time it has counted down so far (for <see cref="MaxSeconds"/>), up to <see cref="SavedAt"/> while running.</summary>
    public double ElapsedSeconds { get; set; }

    /// <summary>When <see cref="RemainingSeconds"/> and <see cref="ElapsedSeconds"/> were last brought up to date while running.</summary>
    public DateTimeOffset? SavedAt { get; set; }

    /// <summary>Everything ever added (events and manual), for overlays' "+5:00" pop-ups.</summary>
    public double AddedSeconds { get; set; }

    /// <summary>
    /// Counts the timer's runs: a reset (or starting again after it ended) begins the next one. History entries from an
    /// earlier run are only reported; correcting them leaves the clock alone.
    /// </summary>
    public int Run { get; set; } = 1;
}

/// <summary>One rung of a goal ladder: reached when the goal's points get to <see cref="Points"/>.</summary>
public sealed class GoalStep
{
    public string Name { get; set; } = "";
    public double Points { get; set; }
}

/// <summary>A points goal with steps ("10: new emote, 20: second emote, ..."), kept in ascending order.</summary>
public sealed class GoalLadder
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Sub goal";
    public double Points { get; set; }
    public List<GoalStep> Steps { get; set; } = [];

    /// <summary>The highest step total already announced; steps at or below it never fire again (until a reset).</summary>
    public double? ReachedThrough { get; set; }

    public double Multiplier { get; set; } = 1;
    public bool ResetOnNewStream { get; set; }
    public EventValues Values { get; set; } = EventValues.GoalDefaults();

    /// <summary>Counts resets to 0, like <see cref="SubathonTimer.Run"/>.</summary>
    public int Run { get; set; } = 1;
}

/// <summary>subathon.json.</summary>
public sealed class SubathonFile
{
    public List<SubathonTimer> Timers { get; set; } = [];
    public List<GoalLadder> Goals { get; set; } = [];

    /// <summary>When the last live platform went offline (null while live); a new stream starts 15 minutes or more later.</summary>
    public DateTimeOffset? OfflineSince { get; set; }

    /// <summary>Whether a platform was live when last seen (so a restart mid-stream is not a new stream).</summary>
    public bool WasLive { get; set; }
}

public enum HistoryKind
{
    /// <summary>A stream event (sub, tip, follow...) or an emulated one.</summary>
    Event,

    /// <summary>Time or points added or taken away by hand (the app, a sub-action, the API).</summary>
    Manual,

    /// <summary>"Set time left" or "Set points": the difference it made.</summary>
    Set,
}

/// <summary>One change to a timer (seconds) or goal (points), with what caused it, so it can be reviewed and corrected.</summary>
public sealed class SubathonHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    /// <summary>The timer's or goal's id.</summary>
    public string OwnerId { get; set; } = "";

    public bool IsGoal { get; set; }

    /// <summary>The timer's or goal's <c>Run</c> when it happened.</summary>
    public int Run { get; set; } = 1;

    public DateTimeOffset Time { get; set; }
    public HistoryKind Kind { get; set; }
    public string User { get; set; } = "";

    /// <summary>The platform or service it came from (Twitch, Throne, Ko-fi...); empty for changes by hand.</summary>
    public string Source { get; set; } = "";

    /// <summary>What it was: "Subscription", "Donation", "Follow", a reward's title, "Manual", a note...</summary>
    public string Reason { get; set; } = "";

    /// <summary>Money, Bits, Kicks, Jewels, Subscription, GiftSubscription, Follow, Raid, Redemption; empty for changes by hand.</summary>
    public string Unit { get; set; } = "";

    /// <summary>Bits, subs, gift subs, raiders... (1 for a follow).</summary>
    public double Quantity { get; set; }

    /// <summary>What it was worth in the streamer's currency (Top Spenders), 0 when it was not a purchase.</summary>
    public double Amount { get; set; }

    public string Currency { get; set; } = "";
    public double Multiplier { get; set; } = 1;

    /// <summary>The seconds or points it added (negative: taken away), after any correction.</summary>
    public double Value { get; set; }

    /// <summary>The value and amount before the first correction (null when never changed).</summary>
    public double? OriginalValue { get; set; }

    public double? OriginalAmount { get; set; }

    /// <summary>Taken back: it no longer counts in the totals.</summary>
    public bool Removed { get; set; }

    /// <summary>What removing it actually took off the clock or goal (less when the time had already run), put back on restore.</summary>
    public double Reversed { get; set; }

    public DateTimeOffset? ChangedAt { get; set; }
    public string Note { get; set; } = "";
    public bool IsTest { get; set; }
}

/// <summary>subathon-history.json: every change to every timer and goal, oldest first.</summary>
public sealed class SubathonHistoryFile
{
    public List<SubathonHistoryEntry> Entries { get; set; } = [];
}

/// <summary>Which history entries to list, total or remove. Empty fields match everything.</summary>
public sealed record HistoryFilter(
    string? Owner = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    string? User = null,
    string? Source = null,
    bool TestOnly = false,
    bool IncludeRemoved = true);

/// <summary>One source's share of a history total.</summary>
public sealed record HistorySourceTotal(string Source, int Count, double Amount, double Value);

/// <summary>What the entries matching a filter add up to (removed ones left out).</summary>
public sealed record HistorySummary(int Count, int Removed, double Value, double Amount, IReadOnlyList<HistorySourceTotal> BySource);
