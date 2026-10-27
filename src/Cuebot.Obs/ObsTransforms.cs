using System.Globalization;
using System.Text.Json;

namespace Cuebot.Obs;

/// <summary>Pure helpers for scene item transforms, colors, volume and file formats.</summary>
public static class ObsTransforms
{
    // obs_alignment bits.
    private const int AlignLeft = 1;
    private const int AlignRight = 2;
    private const int AlignTop = 4;
    private const int AlignBottom = 8;

    /// <summary>
    /// The transform patch that mirrors an item along <paramref name="axis"/> while keeping it where it is on screen:
    /// the scale is negated and, for edge-aligned items, the position moves by the item's size (rotation is ignored).
    /// </summary>
    public static Dictionary<string, object> Flip(JsonElement transform, FlipAxis axis)
    {
        var alignment = transform.GetIntOrNull("alignment") ?? (AlignLeft | AlignTop);
        var horizontal = axis == FlipAxis.Horizontal;
        var scale = transform.GetDoubleOrNull(horizontal ? "scaleX" : "scaleY") ?? 1;
        var position = transform.GetDoubleOrNull(horizontal ? "positionX" : "positionY") ?? 0;
        var sourceSize = transform.GetDoubleOrNull(horizontal ? "sourceWidth" : "sourceHeight") ?? 0;
        var size = Math.Abs(sourceSize * scale);
        var (nearEdge, farEdge) = horizontal ? (AlignLeft, AlignRight) : (AlignTop, AlignBottom);
        var direction = scale >= 0 ? 1 : -1;
        var shift = (alignment & nearEdge) != 0 ? size * direction : (alignment & farEdge) != 0 ? -size * direction : 0;
        return new Dictionary<string, object>
        {
            [horizontal ? "scaleX" : "scaleY"] = -scale,
            [horizontal ? "positionX" : "positionY"] = position + shift,
        };
    }

    /// <summary>
    /// Parses "#RRGGBB", "#AARRGGBB" (also without '#') or a decimal number into the ABGR integer OBS color sources
    /// store (0xAABBGGRR). Six-digit colors are opaque.
    /// </summary>
    public static long ParseColor(string value)
    {
        var text = value.Trim();
        // Six or eight characters (or a leading '#') are hex; any other run of digits is OBS's decimal value.
        if (!text.StartsWith('#') && text.Length is not (6 or 8) &&
            long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return number;
        var hex = text.TrimStart('#');
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var argb))
            throw new ArgumentException($"Color must be #RRGGBB or #AARRGGBB (got '{value}').");
        if (hex.Length == 6) argb |= 0xFF000000;
        uint a = argb >> 24 & 0xFF, r = argb >> 16 & 0xFF, g = argb >> 8 & 0xFF, b = argb & 0xFF;
        return a << 24 | b << 16 | g << 8 | r;
    }

    /// <summary>Converts a volume multiplier to decibels (OBS uses -100 dB for silence).</summary>
    public static double MulToDb(double mul) => mul <= 0 ? -100 : 20 * Math.Log10(mul);

    /// <summary>Clamps a decibel volume to the range obs-websocket accepts (-100 to 26 dB).</summary>
    public static double ClampDb(double db) => Math.Clamp(db, -100, 26);

    /// <summary>The obs-websocket image format for a screenshot path ("png" when the extension is unknown).</summary>
    public static string ImageFormatFor(string path) => Path.GetExtension(path).TrimStart('.').ToLowerInvariant() switch
    {
        "jpg" or "jpeg" => "jpg",
        "bmp" => "bmp",
        "webp" => "webp",
        "tga" => "tga",
        _ => "png",
    };
}
