using Cuebot.Core.Actions;

namespace Cuebot.Integrations.HeartRate;

/// <summary>One heart rate measurement.</summary>
public sealed record HeartRateReading(string Source, int Bpm, DateTimeOffset Time);

/// <summary>
/// Latest heart rate of each source (Pulsoid, HypeRate) for the UI, the "Get heart rate" sub-action and scripts.
/// Also mirrors them into temporary global variables: <c>heartRate</c>, <c>heartRateSource</c>, <c>pulsoidHeartRate</c>
/// and <c>hyperateHeartRate</c> (usable as ~heartRate~ anywhere).
/// </summary>
public sealed class HeartRateHub(GlobalVariables? globals, TimeProvider clock)
{
    public const string PulsoidSource = "Pulsoid";
    public const string HypeRateSource = "HypeRate";

    private readonly object _gate = new();
    private readonly Dictionary<string, HeartRateReading> _latest = new(StringComparer.OrdinalIgnoreCase);
    private HeartRateReading? _newest;

    /// <summary>Raised (background thread) for every reading.</summary>
    public event Action<HeartRateReading>? Updated;

    /// <summary>The newest reading of any source.</summary>
    public HeartRateReading? Latest
    {
        get { lock (_gate) return _newest; }
    }

    /// <summary>The newest reading of <paramref name="source"/> ("Pulsoid", "HypeRate"); empty/"Latest" means any.</summary>
    public HeartRateReading? Get(string? source)
    {
        if (string.IsNullOrWhiteSpace(source) || source.Equals("Latest", StringComparison.OrdinalIgnoreCase)) return Latest;
        lock (_gate) return _latest.GetValueOrDefault(source.Trim());
    }

    /// <summary>Stores a reading; returns the previous one of the same source.</summary>
    internal HeartRateReading? Report(string source, int bpm)
    {
        var reading = new HeartRateReading(source, bpm, clock.GetLocalNow());
        HeartRateReading? previous;
        lock (_gate)
        {
            previous = _latest.GetValueOrDefault(source);
            _latest[source] = reading;
            _newest = reading;
        }
        if (globals is not null)
        {
            globals.SetValue(source.ToLowerInvariant() + "HeartRate", bpm, persist: false);
            globals.SetValue("heartRate", bpm, persist: false);
            globals.SetValue("heartRateSource", source, persist: false);
        }
        Updated?.Invoke(reading);
        return previous;
    }
}
