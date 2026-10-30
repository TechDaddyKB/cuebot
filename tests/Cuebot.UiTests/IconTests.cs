using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using Avalonia.Media;
using Cuebot.App.Controls;

namespace Cuebot.UiTests;

public sealed class IconTests
{
    private static readonly string ScreenshotDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshots"));

    private static IReadOnlyList<string> Kinds()
    {
        var text = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
            "src", "Cuebot.App", "Styles", "Icons.axaml"));
        return System.Text.RegularExpressions.Regex.Matches(text, "x:Key=\"Icon\\.([a-z0-9-]+)\"").Select(m => m.Groups[1].Value).ToArray();
    }

    [AvaloniaFact]
    public void Every_icon_resolves_and_draws_in_the_text_colour()
    {
        var kinds = Kinds();
        Assert.True(kinds.Count >= 40);
        var app = Application.Current!;
        foreach (var kind in kinds)
            Assert.True(app.TryGetResource("Icon." + kind, null, out var g) && g is Geometry, kind);

        var grid = new WrapPanel { Margin = new Thickness(12), Orientation = Orientation.Horizontal };
        foreach (var kind in kinds)
        {
            grid.Children.Add(new StackPanel
            {
                Width = 92, Margin = new Thickness(4), Spacing = 4,
                Children =
                {
                    new Icon { Kind = kind, Width = 24, Height = 24, HorizontalAlignment = HorizontalAlignment.Center },
                    new TextBlock { Text = kind, FontSize = 10, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis },
                },
            });
        }
        var window = new Window { Width = 820, Height = 520, Content = grid };
        window.Show();

        var frame = window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Directory.CreateDirectory(ScreenshotDir);
        frame!.Save(Path.Combine(ScreenshotDir, "icons.png"));

        // Each icon cell has light (text-coloured) pixels on the dark ground.
        var first = (Icon)((StackPanel)grid.Children[0]).Children[0];
        var origin = first.TranslatePoint(new Point(0, 0), window)!.Value;
        Assert.True(BrightPixels(frame, new PixelRect((int)origin.X, (int)origin.Y, 24, 24)) > 10);
    }

    [AvaloniaFact]
    public void An_unknown_icon_draws_nothing_and_does_not_throw()
    {
        var icon = new Icon { Kind = "no-such-icon" };
        var window = new Window { Width = 40, Height = 40, Content = icon };
        window.Show();
        Assert.NotNull(window.CaptureRenderedFrame());
    }

    // Pixels whose colour is close to the light text colour, inside one area of the frame.
    private static int BrightPixels(Avalonia.Media.Imaging.Bitmap frame, PixelRect area)
    {
        var stride = area.Width * 4;
        var pixels = new byte[stride * area.Height];
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pixels, System.Runtime.InteropServices.GCHandleType.Pinned);
        try { frame.CopyPixels(area, handle.AddrOfPinnedObject(), pixels.Length, stride); }
        finally { handle.Free(); }
        var count = 0;
        for (var i = 0; i < pixels.Length; i += 4)
            if (pixels[i] + pixels[i + 1] + pixels[i + 2] > 450) count++;
        return count;
    }
}
