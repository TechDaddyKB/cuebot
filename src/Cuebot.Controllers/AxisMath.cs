namespace Cuebot.Controllers;

/// <summary>Pure axis maths: scaling raw values, deadzones and deciding when a change is worth an event.</summary>
public static class AxisMath
{
    /// <summary>
    /// Raw SDL value to -1..1 (sticks, joystick axes) or 0..1 (gamepad triggers), with a scaled deadzone: values inside
    /// the deadzone become 0 and the rest is stretched so the output still reaches ±1 (no jump at the edge).
    /// </summary>
    public static double Normalize(short raw, bool isTrigger, double deadzone)
    {
        var v = isTrigger ? Math.Max(0, (int)raw) / 32767.0 : Math.Clamp(raw / 32767.0, -1, 1);
        return ApplyDeadzone(v, deadzone);
    }

    public static double ApplyDeadzone(double value, double deadzone)
    {
        deadzone = Math.Clamp(deadzone, 0, 0.95);
        var magnitude = Math.Abs(value);
        if (magnitude <= deadzone) return 0;
        var scaled = (magnitude - deadzone) / (1 - deadzone);
        return Math.Round(Math.Sign(value) * Math.Min(1, scaled), 4);
    }

    /// <summary>
    /// True when <paramref name="next"/> differs enough from the last published value: at least
    /// <paramref name="minChange"/>, or it reached rest (0) or an end stop (±1), so triggers always see the final
    /// position even for tiny moves.
    /// </summary>
    public static bool IsSignificant(double previous, double next, double minChange)
    {
        if (next == previous) return false;
        if (next == 0 || Math.Abs(next) >= 1) return true;
        return Math.Abs(next - previous) >= Math.Max(0.001, minChange);
    }

    /// <summary>"Positive", "Negative" or "Centered".</summary>
    public static string Direction(double value) => value > 0 ? "Positive" : value < 0 ? "Negative" : "Centered";

    /// <summary>Is the value past the threshold (0-1) in the wanted direction ("Positive", "Negative" or "Either")?</summary>
    public static bool Beyond(double value, string direction, double threshold)
    {
        threshold = Math.Clamp(threshold, 0, 1);
        return direction.Trim().ToLowerInvariant() switch
        {
            "positive" or "+" or "right" or "down" => value > 0 && value >= threshold,
            "negative" or "-" or "left" or "up" => value < 0 && -value >= threshold,
            _ => value != 0 && Math.Abs(value) >= threshold,
        };
    }
}

/// <summary>
/// Limits axis events per (controller, axis): at most one every <c>interval</c>, the latest value winning, and the
/// last value of a burst is always delivered by <see cref="TakeDue"/> (trailing edge). Not thread-safe; the owner locks.
/// </summary>
internal sealed class AxisThrottle
{
    private readonly Dictionary<(uint Device, int Axis), Slot> _slots = [];

    private sealed class Slot
    {
        public DateTimeOffset LastSent = DateTimeOffset.MinValue;
        public bool HasPending;
    }

    /// <summary>True to publish now; false when it is held back (call <see cref="TakeDue"/> later).</summary>
    public bool Offer(uint device, int axis, DateTimeOffset now, TimeSpan interval)
    {
        if (!_slots.TryGetValue((device, axis), out var slot)) _slots[(device, axis)] = slot = new Slot();
        if (interval <= TimeSpan.Zero || now - slot.LastSent >= interval)
        {
            slot.LastSent = now;
            slot.HasPending = false;
            return true;
        }
        slot.HasPending = true;
        return false;
    }

    /// <summary>Held-back axes whose interval has passed; they count as sent now.</summary>
    public List<(uint Device, int Axis)> TakeDue(DateTimeOffset now, TimeSpan interval)
    {
        var due = new List<(uint, int)>();
        foreach (var (key, slot) in _slots)
        {
            if (!slot.HasPending || now - slot.LastSent < interval) continue;
            slot.HasPending = false;
            slot.LastSent = now;
            due.Add(key);
        }
        return due;
    }

    public bool HasPending => _slots.Values.Any(s => s.HasPending);

    public void Forget(uint device)
    {
        foreach (var key in _slots.Keys.Where(k => k.Device == device).ToList()) _slots.Remove(key);
    }

    public void Clear() => _slots.Clear();
}
