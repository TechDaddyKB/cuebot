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

    /// <summary>Subscriptions anywhere else (Rumble, Ko-fi, Patreon, Fourthwall...).</summary>
    public double OtherSub { get; set; }

    /// <summary>Gift subs count like subs (one per sub gifted); off: gift subs do not count.</summary>
    public bool CountGiftSubs { get; set; } = true;

    public double Per100Bits { get; set; }
    public double Per100Kicks { get; set; }
    public double Per100Jewels { get; set; }

    /// <summary>Tips, Super Chats, rants, donations and shop orders: per 1 of the chosen currency.</summary>
    public double PerCurrencyUnit { get; set; }

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
