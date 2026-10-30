using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Cuebot.App.Controls;

/// <summary>
/// A Lucide icon (Styles/Icons.axaml) drawn as round-capped strokes in the current text colour, so it follows the
/// theme and hover/selection states like text does. <c>&lt;c:Icon Kind="house" /&gt;</c>; 16 px by default.
/// </summary>
public sealed class Icon : Control
{
    private const double Grid = 24;   // Lucide's design grid

    public static readonly StyledProperty<string?> KindProperty =
        AvaloniaProperty.Register<Icon, string?>(nameof(Kind));

    /// <summary>Lucide name, e.g. "house" (resource "Icon.house").</summary>
    public string? Kind
    {
        get => GetValue(KindProperty);
        set => SetValue(KindProperty, value);
    }

    public static readonly StyledProperty<IBrush?> ForegroundProperty =
        TextElement.ForegroundProperty.AddOwner<Icon>();

    public IBrush? Foreground
    {
        get => GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    /// <summary>Stroke width in Lucide grid units (2 = Lucide's default).</summary>
    public static readonly StyledProperty<double> StrokeWidthProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(StrokeWidth), 2);

    public double StrokeWidth
    {
        get => GetValue(StrokeWidthProperty);
        set => SetValue(StrokeWidthProperty, value);
    }

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, ForegroundProperty, StrokeWidthProperty);
        WidthProperty.OverrideDefaultValue<Icon>(16);
        HeightProperty.OverrideDefaultValue<Icon>(16);
    }

    public override void Render(DrawingContext context)
    {
        if (Kind is not { Length: > 0 } kind || Foreground is not { } brush) return;
        if (!this.TryFindResource("Icon." + kind, ActualThemeVariant, out var found) || found is not Geometry geometry) return;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / Grid;
        if (scale <= 0) return;
        var pen = new Pen(brush, StrokeWidth, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        var offset = new Point((Bounds.Width - Grid * scale) / 2, (Bounds.Height - Grid * scale) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset)))
            context.DrawGeometry(null, pen, geometry);
    }
}
