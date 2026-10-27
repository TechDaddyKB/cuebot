using System.Collections.Concurrent;
using System.Globalization;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Variables;

/// <summary>
/// Clock, randomness, culture and error reporting used when resolving text. The app's instance belongs to
/// <see cref="Actions.GlobalVariables.Text"/>; tests create their own with a fixed clock and seeded random.
/// </summary>
public sealed class TextEnvironment
{
    private const int MaxReported = 512;
    private readonly ConcurrentDictionary<string, byte> _reported = new(StringComparer.Ordinal);
    private CultureInfo _culture = DefaultCulture;

    /// <summary>Used when no environment is attached (system clock, shared random, no logging).</summary>
    public static TextEnvironment Default { get; } = new();

    /// <summary>
    /// Invariant culture with "$" as the currency symbol (invariant's own is the generic sign), so
    /// <c>%amount:c2%</c> reads "$5.00" out of the box. Everything else is culture-neutral.
    /// </summary>
    public static CultureInfo DefaultCulture { get; } = CreateDefaultCulture();

    public TimeProvider Clock { get; init; } = TimeProvider.System;

    /// <summary>Source for random helpers. <see cref="System.Random.Shared"/> by default (thread-safe).</summary>
    public Random Random { get; init; } = Random.Shared;

    /// <summary>Culture for format specifiers and %date%/%time%. Numbers without a format are always invariant.</summary>
    public CultureInfo Culture
    {
        get => Volatile.Read(ref _culture);
        set => Volatile.Write(ref _culture, value ?? DefaultCulture);
    }

    public AppLog? Log { get; init; }

    /// <summary>Raised for every resolution problem, including repeats that are logged only once.</summary>
    public event Action<string>? Error;

    /// <summary>Logs a resolution problem once per distinct message so a bad template cannot flood the log.</summary>
    public void ReportError(string message)
    {
        Error?.Invoke(message);
        if (Log is null) return;
        if (_reported.Count >= MaxReported) _reported.Clear();
        if (_reported.TryAdd(message, 0)) Log.Warn("Variables", message);
    }

    public DateTimeOffset LocalNow() => Clock.GetLocalNow();

    private static CultureInfo CreateDefaultCulture()
    {
        var culture = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        culture.NumberFormat.CurrencySymbol = "$";
        return CultureInfo.ReadOnly(culture);
    }
}
