using Avalonia;
using Avalonia.Media;
using Avalonia.Styling;

namespace Cuebot.App.Shell;

/// <summary>
/// Keeps the shared <c>Cuebot*</c> brushes (Styles/Cuebot.axaml) in step with the app's actual theme variant by copying
/// each variant's <c>Cuebot*Color</c> into the brush. Mutating the shared instances, rather than swapping resources,
/// also updates views that took the brushes with <c>StaticResource</c>.
/// </summary>
public static class ThemeBrushes
{
    public static readonly IReadOnlyList<string> Keys =
    [
        "CuebotGround", "CuebotRail", "CuebotRaised", "CuebotLine", "CuebotText", "CuebotAccentFill", "CuebotAccentSoft",
        "CuebotWarn", "CuebotSidebar", "CuebotPanel", "CuebotCard", "CuebotCardBorder", "CuebotMuted",
        "CuebotAccent", "CuebotGood", "CuebotBad", "CuebotSelected", "CuebotOverlay",
    ];

    public static void Attach(Application app)
    {
        Sync(app);
        app.ActualThemeVariantChanged += (_, _) => Sync(app);
    }

    public static void Sync(Application app)
    {
        var variant = app.ActualThemeVariant;
        // A custom or Default variant inherits from Light or Dark.
        while (variant.InheritVariant is { } parent && variant != ThemeVariant.Light && variant != ThemeVariant.Dark) variant = parent;
        foreach (var key in Keys)
        {
            if (app.TryGetResource(key, null, out var brush) && brush is SolidColorBrush solid
                && app.TryGetResource(key + "Color", variant, out var color) && color is Color c)
                solid.Color = c;
        }
    }
}
