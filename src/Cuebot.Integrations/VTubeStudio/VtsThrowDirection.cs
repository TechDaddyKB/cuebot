using System.Globalization;

namespace Cuebot.Integrations.VTubeStudio;

/// <summary>
/// Where thrown items come from. VTube Studio places items anywhere (also off screen), so every direction is Cuebot's own
/// animation: the item starts outside the screen edge (x and y run -1 to 1 across the screen) and flies to the head.
/// </summary>
public static class VtsThrowDirection
{
    /// <summary>How far outside the middle the items start (1 is the screen edge).</summary>
    public const double Edge = 1.35;

    /// <summary>The choices, as stored. "Random" (left or right) is the old default and keeps its meaning.</summary>
    public static readonly IReadOnlyList<string> All =
        ["Random", "Anywhere", "Left", "Right", "Top", "Bottom", "TopLeft", "TopRight", "BottomLeft", "BottomRight", "Front"];

    public static readonly IReadOnlyDictionary<string, string> Labels = new Dictionary<string, string>
    {
        ["Random"] = "Random: left or right",
        ["Anywhere"] = "Random: any direction",
        ["TopLeft"] = "Top left",
        ["TopRight"] = "Top right",
        ["BottomLeft"] = "Bottom left",
        ["BottomRight"] = "Bottom right",
        ["Front"] = "Front (from the camera)",
    };

    /// <summary>
    /// The direction for one throw: (dx, dy) is the side it comes from (-1 to 1; y up), or <c>Front</c>. Accepts the
    /// choices in any case, with or without spaces ("top left"), and a compass angle in degrees (0 top, 90 right).
    /// Unknown text throws.
    /// </summary>
    public static (double Dx, double Dy, bool Front) Pick(string? from, Random random)
    {
        var key = (from ?? "").Replace(" ", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).Trim();
        switch (key.ToLowerInvariant())
        {
            case "" or "random": return (random.Next(2) == 0 ? -1 : 1, 0, false);
            case "anywhere" or "any" or "anydirection": return Angle(random.NextDouble() * 360);
            case "left": return (-1, 0, false);
            case "right": return (1, 0, false);
            case "top" or "up" or "above": return (0, 1, false);
            case "bottom" or "down" or "below": return (0, -1, false);
            case "topleft": return (-1, 1, false);
            case "topright": return (1, 1, false);
            case "bottomleft": return (-1, -1, false);
            case "bottomright": return (1, -1, false);
            case "front" or "camera": return (0, 0, true);
        }
        if (double.TryParse(key.TrimEnd('°'), NumberStyles.Float, CultureInfo.InvariantCulture, out var degrees) && double.IsFinite(degrees))
            return Angle(degrees);
        throw new ArgumentException($"Unknown throw direction '{from}'. Use {string.Join(", ", All)} or an angle in degrees (0 = top, 90 = right).");
    }

    /// <summary>A compass angle as a point on the square around the screen (so the item starts just past an edge).</summary>
    private static (double, double, bool) Angle(double degrees)
    {
        var radians = degrees * Math.PI / 180;
        var (x, y) = (Math.Sin(radians), Math.Cos(radians));
        var scale = Math.Max(Math.Abs(x), Math.Abs(y));
        return (Math.Round(x / scale, 6), Math.Round(y / scale, 6), false);
    }

    /// <summary>How the head reacts: pushed away from where the hit came from (added to the tracking).</summary>
    public static IReadOnlyList<VtsParameterValue> Flinch(double dx, double dy, bool front, double strength)
    {
        var values = new List<VtsParameterValue>();
        void Add(string name, double value)
        {
            if (Math.Abs(value) > 0.0001) values.Add(new VtsParameterValue(name, value, null));
        }
        Add("FaceAngleX", -dx * 25 * strength);
        Add("FaceAngleZ", dx * 15 * strength);
        Add("FacePositionX", -dx * 8 * strength);
        Add("FaceAngleY", (front ? 12 : -dy * 20) * strength);
        Add("FacePositionY", -dy * 5 * strength);
        if (front) Add("FacePositionZ", -6 * strength);
        Add("EyeOpenLeft", -0.6 * strength);
        Add("EyeOpenRight", -0.6 * strength);
        return values;
    }
}
