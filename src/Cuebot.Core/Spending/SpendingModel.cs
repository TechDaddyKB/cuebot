using Cuebot.Core.Chat;

namespace Cuebot.Core.Spending;

/// <summary>The time spans a top-spenders list can cover. Day, week, month and year are calendar periods in local time.</summary>
public enum SpendingPeriod { Stream, Day, Week, Month, Year, AllTime }

/// <summary>What the platform units are worth, in US dollars (what a viewer pays for them, roughly), and other choices.</summary>
public sealed class SpendingSettings
{
    /// <summary>ISO code of the currency everything is stored and shown in (e.g. "USD", "EUR", "GBP").</summary>
    public string Currency { get; set; } = "USD";

    public double BitUsd { get; set; } = 0.01;
    public double KickUsd { get; set; } = 0.01;
    public double JewelUsd { get; set; } = 0.01;
    public double TwitchTier1Usd { get; set; } = 4.99;
    public double TwitchTier2Usd { get; set; } = 9.99;
    public double TwitchTier3Usd { get; set; } = 24.99;
    public double KickSubscriptionUsd { get; set; } = 4.99;
    public double YouTubeMembershipUsd { get; set; } = 4.99;

    /// <summary>Subscriptions on any other platform that come without a price.</summary>
    public double OtherSubscriptionUsd { get; set; } = 4.99;

    /// <summary>Charity donations go to the charity, not the streamer, so they are left out unless this is on.</summary>
    public bool IncludeCharity { get; set; }

    /// <summary>Names never listed (anonymous gifters, private Ko-fi supporters...); their purchases still count elsewhere.</summary>
    public List<string> IgnoredUsers { get; set; } = ["Anonymous", "AnAnonymousGifter", "Someone"];

    /// <summary>The first day of a week for "this week" lists.</summary>
    public DayOfWeek WeekStart { get; set; } = DayOfWeek.Monday;
}

/// <summary>
/// One stream. Its exchange rates are fetched when it starts and then never change, so everything bought during the
/// stream keeps the value it had that day.
/// </summary>
public sealed class SpendingSession
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When the last live platform went offline; null while live (or when no platform reported it).</summary>
    public DateTimeOffset? EndedAt { get; set; }

    /// <summary>The currency this stream's values are stored in (the setting when it started).</summary>
    public string Currency { get; set; } = "USD";

    /// <summary>The locked rates: 1 <see cref="Currency"/> = rate × other currency. Empty until fetched.</summary>
    public Dictionary<string, double> Rates { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Where the rates came from ("Frankfurter (European Central Bank)", ...).</summary>
    public string RatesSource { get; set; } = "";

    /// <summary>The date the rates are for, as the provider gives it ("2026-11-01").</summary>
    public string RatesDate { get; set; } = "";
}

/// <summary>One purchase, as recorded.</summary>
public sealed class SpendRecord
{
    public DateTimeOffset Time { get; set; }
    public string SessionId { get; set; } = "";
    public string Platform { get; set; } = "";
    public string User { get; set; } = "";
    public string Label { get; set; } = "";
    public SpendUnit Unit { get; set; }
    public double Quantity { get; set; }

    /// <summary>The currency the viewer paid in (money), or "USD" for platform units priced in dollars.</summary>
    public string PaidCurrency { get; set; } = "";

    /// <summary>What was paid, in <see cref="PaidCurrency"/>: the amount of money, or the units' dollar price that day.</summary>
    public double PaidAmount { get; set; }

    public string Tier { get; set; } = "";

    /// <summary>The value in the stream's currency; null while the stream's exchange rates have not arrived.</summary>
    public double? Value { get; set; }

    /// <summary>The stream's currency (<see cref="SpendingSession.Currency"/>).</summary>
    public string Currency { get; set; } = "";
}

/// <summary>spending.json.</summary>
public sealed class SpendingFile
{
    public SpendingSettings Settings { get; set; } = new();
    public List<SpendingSession> Sessions { get; set; } = [];
    public List<SpendRecord> Entries { get; set; } = [];
}

/// <summary>One person's total in a top-spenders list.</summary>
public sealed record TopSpender(int Rank, string User, string Platform, double Total, int Count, IReadOnlyDictionary<string, double> Breakdown);

/// <summary>A top-spenders list: who, for which period and platform, in which currency.</summary>
public sealed record SpendingList(SpendingPeriod Period, string Platform, string Currency, double Total, IReadOnlyList<TopSpender> Spenders)
{
    /// <summary>Purchases left out because their stream's exchange rates have not arrived yet.</summary>
    public int Pending { get; init; }
}
