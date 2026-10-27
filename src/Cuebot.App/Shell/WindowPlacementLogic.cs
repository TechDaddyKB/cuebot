using Avalonia;

namespace Cuebot.App.Shell;

/// <summary>A screen's usable area in physical pixels and its scale factor.</summary>
public readonly record struct ScreenArea(PixelRect WorkingArea, double Scaling);

/// <summary>Where and how big the main window opens.</summary>
public readonly record struct PlacementResult(double Width, double Height, PixelPoint? Position, bool Maximized);

/// <summary>
/// Pure placement rules, so they can be tested without a window: a saved size is clamped to the minimum size and to
/// the screen it was on; a saved position is used only when the window's title bar would land on a connected screen
/// (otherwise the window is centred). Wayland compositors do not let clients place windows, so positions are optional.
/// </summary>
public static class WindowPlacementLogic
{
    public const double DefaultWidth = 1360;
    public const double DefaultHeight = 860;
    public const double MinWidth = 960;
    public const double MinHeight = 600;

    /// <summary>Visible part of the title bar that must be on screen for a saved position to be reused.</summary>
    private const int GrabWidth = 120, GrabHeight = 32;

    public static PlacementResult Resolve(WindowPlacement? saved, IReadOnlyList<ScreenArea> screens)
    {
        if (saved is null || saved.Width <= 0 || saved.Height <= 0 || double.IsNaN(saved.Width) || double.IsNaN(saved.Height))
            return Fit(DefaultWidth, DefaultHeight, null, false, screens);
        PixelPoint? position = saved.X is int x && saved.Y is int y ? new PixelPoint(x, y) : null;
        return Fit(saved.Width, saved.Height, position, saved.Maximized, screens);
    }

    private static PlacementResult Fit(double width, double height, PixelPoint? position, bool maximized, IReadOnlyList<ScreenArea> screens)
    {
        ScreenArea? target = null;
        if (position is { } p)
        {
            var grab = new PixelRect(p.X, p.Y, GrabWidth, GrabHeight);
            foreach (var screen in screens)
            {
                if (screen.WorkingArea.Intersects(grab))
                {
                    target = screen;
                    break;
                }
            }
            if (target is null) position = null;   // the monitor it was on is gone
        }
        target ??= screens.Count > 0 ? screens[0] : null;

        width = Math.Max(MinWidth, width);
        height = Math.Max(MinHeight, height);
        if (target is { } t && t.Scaling > 0)
        {
            var maxWidth = t.WorkingArea.Width / t.Scaling;
            var maxHeight = t.WorkingArea.Height / t.Scaling;
            if (maxWidth >= MinWidth) width = Math.Min(width, maxWidth);
            if (maxHeight >= MinHeight) height = Math.Min(height, maxHeight);

            if (position is { } q)
            {
                // Pull the window back so that its top-left stays inside the working area.
                var area = t.WorkingArea;
                var nx = Math.Clamp(q.X, area.X, Math.Max(area.X, area.Right - GrabWidth));
                var ny = Math.Clamp(q.Y, area.Y, Math.Max(area.Y, area.Bottom - GrabHeight));
                position = new PixelPoint(nx, ny);
            }
        }
        return new PlacementResult(width, height, position, maximized);
    }

    /// <summary>Builds what to save from the window's last normal (not maximized/minimized) bounds.</summary>
    public static WindowPlacement Capture(double normalWidth, double normalHeight, PixelPoint? normalPosition, bool maximized) => new()
    {
        Width = Math.Round(normalWidth),
        Height = Math.Round(normalHeight),
        X = normalPosition?.X,
        Y = normalPosition?.Y,
        Maximized = maximized,
    };
}
