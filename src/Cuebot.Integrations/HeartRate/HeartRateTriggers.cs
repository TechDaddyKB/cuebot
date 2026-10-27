using System.Collections.Concurrent;
using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Integrations.Common;

namespace Cuebot.Integrations.HeartRate;

/// <summary>Bot event types of a heart rate source ("pulsoid" or "hyperate").</summary>
public sealed record HeartRateEventTypes(string Prefix)
{
    public static HeartRateEventTypes Pulsoid { get; } = new("pulsoid");
    public static HeartRateEventTypes HypeRate { get; } = new("hyperate");

    public string Connected => Prefix + ".connected";
    public string Disconnected => Prefix + ".disconnected";

    /// <summary>Every reading (Streamer.bot's "Heart Rate Pulse"). Args: heartRate, previousHeartRate, source.</summary>
    public string HeartRate => Prefix + ".heartRate";

    /// <summary>HypeRate only: a Twitch clip was created through HypeRate.</summary>
    public string ClipCreated => Prefix + ".clipCreated";
}

/// <summary>Pure threshold logic with hysteresis.</summary>
public static class HeartRateThreshold
{
    /// <summary>
    /// "Above": fires once when <paramref name="bpm"/> reaches <paramref name="threshold"/>, then re-arms only after it fell
    /// below threshold − hysteresis. "Below" mirrors it. Returns whether to fire and the new "beyond threshold" state.
    /// </summary>
    public static (bool Fire, bool Beyond) Evaluate(bool beyond, double bpm, double threshold, double hysteresis, bool above)
    {
        hysteresis = Math.Max(0, hysteresis);
        if (above)
        {
            if (!beyond && bpm >= threshold) return (true, true);
            if (beyond && bpm < threshold - hysteresis) return (false, false);
        }
        else
        {
            if (!beyond && bpm <= threshold) return (true, true);
            if (beyond && bpm > threshold + hysteresis) return (false, false);
        }
        return (false, beyond);
    }

    internal static double Bpm(BotEvent e) =>
        e.Args.TryGetValue("heartRate", out var v) ? v switch
        {
            int i => i,
            long l => l,
            double d => d,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) => p,
            _ => double.NaN,
        } : double.NaN;
}

/// <summary>"Heart rate above/below threshold": state is kept per trigger instance so each one fires once per crossing.</summary>
internal sealed class HeartRateThresholdTrigger(string category, string eventType, string typeId, bool above) : ITriggerType
{
    private readonly ConcurrentDictionary<Guid, bool> _beyond = new();

    public string TypeId => typeId;
    public string Category => category;
    public string DisplayName => above ? "Heart rate above threshold" : "Heart rate below threshold";
    public IReadOnlyCollection<string> EventTypes { get; } = [eventType];

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("threshold", "Threshold (BPM)", ParameterKind.Number) { DefaultValue = above ? "120" : "60" },
        new("hysteresis", "Hysteresis (BPM)", ParameterKind.Number)
        {
            DefaultValue = "5",
            Help = above ? "Fires again only after the rate dropped this far below the threshold"
                : "Fires again only after the rate rose this far above the threshold",
        },
    ];

    public bool Matches(BotEvent e, TriggerDefinition trigger, ParameterValues settings, IDictionary<string, object?> args)
    {
        var bpm = HeartRateThreshold.Bpm(e);
        if (double.IsNaN(bpm)) return false;
        var threshold = settings.GetDouble("threshold", above ? 120 : 60);
        var fire = false;
        _beyond.AddOrUpdate(trigger.Id, _ =>
        {
            var (f, b) = HeartRateThreshold.Evaluate(false, bpm, threshold, settings.GetDouble("hysteresis", 5), above);
            fire = f;
            return b;
        }, (_, beyond) =>
        {
            var (f, b) = HeartRateThreshold.Evaluate(beyond, bpm, threshold, settings.GetDouble("hysteresis", 5), above);
            fire = f;
            return b;
        });
        if (fire) args["heartRateThreshold"] = threshold;
        return fire;
    }
}

/// <summary>"Heart rate changed": throttled by a minimum interval and a minimum change, per trigger instance.</summary>
internal sealed class HeartRateChangedTrigger(string category, string eventType, string typeId, TimeProvider clock) : ITriggerType
{
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset Time, double Bpm)> _last = new();
    private readonly object _gate = new();

    public string TypeId => typeId;
    public string Category => category;
    public string DisplayName => "Heart rate changed";
    public IReadOnlyCollection<string> EventTypes { get; } = [eventType];

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("minInterval", "Minimum seconds between runs", ParameterKind.Number) { DefaultValue = "5" },
        new("minChange", "Minimum change (BPM)", ParameterKind.Number) { DefaultValue = "1" },
    ];

    public bool Matches(BotEvent e, TriggerDefinition trigger, ParameterValues settings, IDictionary<string, object?> args)
    {
        var bpm = HeartRateThreshold.Bpm(e);
        if (double.IsNaN(bpm)) return false;
        var now = clock.GetUtcNow();
        lock (_gate)
        {
            if (_last.TryGetValue(trigger.Id, out var last))
            {
                if (now - last.Time < TimeSpan.FromSeconds(Math.Max(0, settings.GetDouble("minInterval", 5)))) return false;
                if (Math.Abs(bpm - last.Bpm) < Math.Max(0, settings.GetDouble("minChange", 1))) return false;
                args["lastTriggeredHeartRate"] = last.Bpm;
                args["heartRateChange"] = bpm - last.Bpm;
            }
            _last[trigger.Id] = (now, bpm);
            return true;
        }
    }
}

/// <summary>Heart rate triggers of one source (category "Pulsoid" or "HypeRate").</summary>
internal sealed class HeartRateTriggers(string category, HeartRateEventTypes events, TimeProvider clock, bool clips) : ITriggerProvider
{
    private readonly ITriggerType[] _triggers =
    [
        Steps.Trigger(category, events.Connected, "Connected"),
        Steps.Trigger(category, events.Disconnected, "Disconnected"),
        Steps.Trigger(category, events.HeartRate, "Heart rate pulse"),
        new HeartRateChangedTrigger(category, events.HeartRate, events.Prefix + ".heartRateChanged", clock),
        new HeartRateThresholdTrigger(category, events.HeartRate, events.Prefix + ".heartRateAbove", above: true),
        new HeartRateThresholdTrigger(category, events.HeartRate, events.Prefix + ".heartRateBelow", above: false),
        .. clips ? new ITriggerType[] { Steps.Trigger(category, events.ClipCreated, "Twitch clip created") } : [],
    ];

    public IEnumerable<ITriggerType> GetTriggers() => _triggers;
}
