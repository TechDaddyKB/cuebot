using System.Globalization;

namespace Cuebot.Webhooks.Services;

/// <summary>Where a Shopify API version stands today.</summary>
public enum ShopifyVersionState
{
    /// <summary>No version header, or not in the YYYY-MM form (like "unstable").</summary>
    Unknown,

    /// <summary>Supported for more than three more months.</summary>
    Supported,

    /// <summary>Supported, but retired within three months: pick a newer one.</summary>
    RetiringSoon,

    /// <summary>No longer supported: Shopify already sends the oldest supported version's payload instead.</summary>
    Retired,

    /// <summary>Newer than the latest stable version (a release candidate).</summary>
    ReleaseCandidate,
}

/// <summary>The outcome of <see cref="ShopifyApiVersions.Check"/>, with a line for the log and the Webhooks page ("" when fine).</summary>
public sealed record ShopifyVersionCheck(string Version, ShopifyVersionState State, string LatestStable, string Advice);

/// <summary>
/// Shopify's API versions without a network call (https://shopify.dev/docs/api/usage/versioning): a stable version is
/// released at the start of each quarter (YYYY-01, -04, -07, -10) and supported for at least 12 months; when a version
/// is retired, webhooks still set to it "fall forward" to the oldest supported one, so fields an action reads can
/// change without notice. Today's latest stable version is this quarter's; a version is retired 12 months after its
/// release and "retiring soon" in its last 3 months.
/// </summary>
public static class ShopifyApiVersions
{
    /// <summary>The Admin API version Cuebot's own GraphQL calls use (bump it each quarter or two).</summary>
    public const string AdminApiVersion = "2026-10";

    public static bool TryParse(string text, out DateOnly start)
    {
        start = default;
        var t = text.Trim();
        if (t.Length != 7 || t[4] != '-') return false;
        if (!int.TryParse(t.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var year)
            || !int.TryParse(t.AsSpan(5, 2), NumberStyles.None, CultureInfo.InvariantCulture, out var month)
            || year < 2000 || month is < 1 or > 12) return false;
        start = new DateOnly(year, month, 1);
        return true;
    }

    public static string Name(DateOnly start) => start.ToString("yyyy-MM", CultureInfo.InvariantCulture);

    /// <summary>This quarter's version on <paramref name="now"/> (2026-10 from October to December 2026).</summary>
    public static string LatestStable(DateTimeOffset now) => Name(new DateOnly(now.UtcDateTime.Year, (now.UtcDateTime.Month - 1) / 3 * 3 + 1, 1));

    /// <summary>The oldest version still supported on <paramref name="now"/> (three quarters before the latest).</summary>
    public static string OldestSupported(DateTimeOffset now)
    {
        TryParse(LatestStable(now), out var latest);
        return Name(latest.AddMonths(-9));
    }

    public static ShopifyVersionCheck Check(string version, DateTimeOffset now)
    {
        var latest = LatestStable(now);
        if (!TryParse(version, out var start))
            return new(version.Trim(), ShopifyVersionState.Unknown, latest, "");
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var retires = start.AddMonths(12);
        TryParse(latest, out var latestStart);
        if (start > latestStart)
            return new(version.Trim(), ShopifyVersionState.ReleaseCandidate, latest,
                $"Shopify webhook API version {Name(start)} is a release candidate, not a stable version; choose {latest} in the webhook's settings.");
        if (today >= retires)
            return new(version.Trim(), ShopifyVersionState.Retired, latest,
                $"Shopify webhook API version {Name(start)} is retired: Shopify now sends {OldestSupported(now)} payloads instead, and fields your actions read may have changed. Edit the webhook in Shopify (Settings → Notifications → Webhooks) and choose {latest}.");
        if (today >= start.AddMonths(9))
            return new(version.Trim(), ShopifyVersionState.RetiringSoon, latest,
                $"Shopify webhook API version {Name(start)} is retired around {Name(retires)}. Edit the webhook in Shopify (Settings → Notifications → Webhooks) and choose {latest}, then check your actions still work.");
        return new(version.Trim(), ShopifyVersionState.Supported, latest, "");
    }
}
