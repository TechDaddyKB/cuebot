using Avalonia;
using Cuebot.App.Shell;
using Cuebot.Core;

namespace Cuebot.UiTests;

public class WindowPlacementTests
{
    private static readonly ScreenArea Primary = new(new PixelRect(0, 0, 1920, 1040), 1.0);
    private static readonly ScreenArea Right = new(new PixelRect(1920, 0, 2560, 1400), 1.0);

    [Fact]
    public void No_saved_placement_uses_the_default_size_centred()
    {
        var r = WindowPlacementLogic.Resolve(null, [Primary]);
        Assert.Equal(WindowPlacementLogic.DefaultWidth, r.Width);
        Assert.Equal(WindowPlacementLogic.DefaultHeight, r.Height);
        Assert.Null(r.Position);
        Assert.False(r.Maximized);
    }

    [Fact]
    public void Saved_placement_on_a_connected_screen_is_restored()
    {
        var saved = new WindowPlacement { X = 2000, Y = 100, Width = 1500, Height = 900, Maximized = true };
        var r = WindowPlacementLogic.Resolve(saved, [Primary, Right]);
        Assert.Equal(new PixelPoint(2000, 100), r.Position);
        Assert.Equal(1500, r.Width);
        Assert.Equal(900, r.Height);
        Assert.True(r.Maximized);
    }

    [Fact]
    public void Position_on_a_disconnected_monitor_is_dropped()
    {
        var saved = new WindowPlacement { X = 2000, Y = 100, Width = 1200, Height = 800 };
        var r = WindowPlacementLogic.Resolve(saved, [Primary]);
        Assert.Null(r.Position);
        Assert.Equal(1200, r.Width);
    }

    [Fact]
    public void Size_is_clamped_to_the_minimum_and_to_the_screen()
    {
        Assert.Equal(WindowPlacementLogic.MinWidth,
            WindowPlacementLogic.Resolve(new WindowPlacement { Width = 100, Height = 100 }, [Primary]).Width);

        var huge = WindowPlacementLogic.Resolve(new WindowPlacement { Width = 5000, Height = 5000 }, [Primary]);
        Assert.Equal(1920, huge.Width);
        Assert.Equal(1040, huge.Height);

        // HiDPI: 1.5x scaling leaves 1280x693 device-independent pixels; height stays at the minimum it needs.
        var hidpi = new ScreenArea(new PixelRect(0, 0, 1920, 1040), 1.5);
        var r = WindowPlacementLogic.Resolve(new WindowPlacement { Width = 5000, Height = 5000 }, [hidpi]);
        Assert.Equal(1280, r.Width);
        Assert.Equal(1040 / 1.5, r.Height, 3);
    }

    [Fact]
    public void Partly_offscreen_title_bar_is_pulled_back_inside()
    {
        var saved = new WindowPlacement { X = 1850, Y = -10, Width = 1200, Height = 800 };
        var r = WindowPlacementLogic.Resolve(saved, [Primary]);
        Assert.NotNull(r.Position);
        Assert.InRange(r.Position.Value.X, 0, 1920 - 120);
        Assert.Equal(0, r.Position.Value.Y);
    }

    [Fact]
    public void Wayland_style_placement_without_position_keeps_size_and_maximized()
    {
        var saved = new WindowPlacement { Width = 1400, Height = 900, Maximized = true };
        var r = WindowPlacementLogic.Resolve(saved, [Primary]);
        Assert.Null(r.Position);
        Assert.Equal(1400, r.Width);
        Assert.True(r.Maximized);
    }

    [Fact]
    public void Corrupt_values_fall_back_to_defaults()
    {
        var r = WindowPlacementLogic.Resolve(new WindowPlacement { Width = double.NaN, Height = -3 }, [Primary]);
        Assert.Equal(WindowPlacementLogic.DefaultWidth, r.Width);
    }

    [Fact]
    public void Capture_round_trips_through_app_json()
    {
        var root = Directory.CreateTempSubdirectory("cuebot-placement-").FullName;
        try
        {
            var paths = new AppPaths(root);
            var store = new AppSettingsStore(paths);
            store.Settings.Window = WindowPlacementLogic.Capture(1234.4, 777.6, new PixelPoint(10, 20), maximized: true);
            store.Save();

            var reloaded = new AppSettingsStore(paths).Settings.Window;
            Assert.Equal(new WindowPlacement { X = 10, Y = 20, Width = 1234, Height = 778, Maximized = true }, reloaded);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
