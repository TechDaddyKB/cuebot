namespace Cuebot.Core.Actions;

/// <summary>Randomness used by the engine (random actions, random groups). Replace in tests for determinism.</summary>
public interface IRandomSource
{
    /// <summary>A value in [0, 1).</summary>
    double NextDouble();
}

public sealed class SystemRandomSource : IRandomSource
{
    public static readonly SystemRandomSource Instance = new();
    public double NextDouble() => Random.Shared.NextDouble();
}

public static class WeightedRandom
{
    /// <summary>
    /// Picks one item with probability proportional to its weight. Items with weight 0 or less (or NaN) are never
    /// picked, unless all are, in which case every item is equally likely. Returns default for an empty list.
    /// </summary>
    public static T? Pick<T>(IReadOnlyList<T> items, Func<T, double> weight, IRandomSource random)
    {
        if (items.Count == 0) return default;
        var weights = items.Select(i => weight(i) is var w && double.IsFinite(w) && w > 0 ? w : 0).ToArray();
        var total = weights.Sum();
        if (total <= 0)
        {
            Array.Fill(weights, 1);
            total = weights.Length;
        }

        var roll = Math.Clamp(random.NextDouble(), 0, 1) * total;
        for (var i = 0; i < items.Count; i++)
        {
            if (weights[i] <= 0) continue;
            if (roll < weights[i]) return items[i];
            roll -= weights[i];
        }
        // Rounding (or a source returning 1): fall back to the last pickable item.
        for (var i = items.Count - 1; i >= 0; i--)
        {
            if (weights[i] > 0) return items[i];
        }
        return default;
    }
}
