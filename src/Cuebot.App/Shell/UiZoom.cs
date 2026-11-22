namespace Cuebot.App.Shell;

/// <summary>
/// The interface zoom's rules: its range, the steps of the zoom keys, and how much room the scaled page gets.
/// The window applies the result with a layout transform, so every page and popup scales without per-view work.
/// </summary>
public static class UiZoom
{
    public const double Min = 0.8;
    public const double Max = 2.0;
    public const double Default = 1.0;

    /// <summary>
    /// The smallest logical size the shell is laid out at. A zoomed window smaller than this scrolls instead of
    /// squeezing the pages until their controls overlap.
    /// </summary>
    public const double MinContentWidth = 860;
    public const double MinContentHeight = 520;

    /// <summary>The preset buttons under Settings > General > Appearance.</summary>
    public static IReadOnlyList<double> Presets { get; } = [0.9, 1.0, 1.25, 1.5, 2.0];

    /// <summary>The zoom in use, for windows other than the main one (the Get input prompt).</summary>
    public static double Current { get; set; } = Default;

    /// <summary>A usable zoom from any stored value: in range and on a whole 5%.</summary>
    public static double Normalize(double scale) =>
        double.IsFinite(scale) ? Math.Clamp(Math.Round(scale * 20) / 20, Min, Max) : Default;

    /// <summary>The next 10% step up (125% goes to 130%).</summary>
    public static double In(double scale) => Normalize((Math.Floor(Normalize(scale) * 10 + 1e-6) + 1) / 10);

    /// <summary>The next 10% step down (125% goes to 120%).</summary>
    public static double Out(double scale) => Normalize((Math.Ceiling(Normalize(scale) * 10 - 1e-6) - 1) / 10);

    public static string Label(double scale) => $"{Math.Round(Normalize(scale) * 100):0}%";

    /// <summary>The logical size to lay the shell out at inside a viewport.</summary>
    public static (double Width, double Height) ContentSize(double viewportWidth, double viewportHeight, double scale)
    {
        var s = Normalize(scale);
        return (Math.Max(viewportWidth / s, MinContentWidth), Math.Max(viewportHeight / s, MinContentHeight));
    }
}
