using Cuebot.Core.Logging;
using Cuebot.Core.Variables;

namespace Cuebot.Tests.Variables;

/// <summary>A clock frozen at 2026-03-04 05:06:07 UTC, with UTC as the local time zone.</summary>
public sealed class FixedClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;
}

internal static class TestEnv
{
    public static TextEnvironment Create(AppLog? log = null, int seed = 42) =>
        new() { Clock = new FixedClock(), Random = new Random(seed), Log = log };
}
