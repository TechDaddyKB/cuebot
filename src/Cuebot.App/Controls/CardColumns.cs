using Avalonia;
using Avalonia.Controls;

namespace Cuebot.App.Controls;

/// <summary>
/// Lays cards out in two columns when there is room (about 1280 px of window), one otherwise. Each card goes to the
/// shorter column, in order, so the columns stay even.
/// </summary>
public sealed class CardColumns : Panel
{
    public static readonly StyledProperty<double> TwoColumnWidthProperty =
        AvaloniaProperty.Register<CardColumns, double>(nameof(TwoColumnWidth), 900);

    public static readonly StyledProperty<double> ColumnSpacingProperty =
        AvaloniaProperty.Register<CardColumns, double>(nameof(ColumnSpacing), 12);

    static CardColumns() => AffectsMeasure<CardColumns>(TwoColumnWidthProperty, ColumnSpacingProperty);

    /// <summary>The width from which the cards use two columns.</summary>
    public double TwoColumnWidth
    {
        get => GetValue(TwoColumnWidthProperty);
        set => SetValue(TwoColumnWidthProperty, value);
    }

    public double ColumnSpacing
    {
        get => GetValue(ColumnSpacingProperty);
        set => SetValue(ColumnSpacingProperty, value);
    }

    /// <summary>The number of columns the last layout used.</summary>
    public int Columns { get; private set; } = 1;

    private int ColumnsFor(double width) => !double.IsInfinity(width) && width >= TwoColumnWidth ? 2 : 1;

    private double ColumnWidth(double width, int columns) => columns == 1 ? width : (width - ColumnSpacing) / 2;

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = ColumnsFor(availableSize.Width);
        var columnWidth = ColumnWidth(availableSize.Width, columns);
        var heights = new double[columns];
        var widest = 0.0;
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            child.Measure(new Size(columnWidth, double.PositiveInfinity));
            var column = Shortest(heights);
            heights[column] += child.DesiredSize.Height;
            widest = Math.Max(widest, child.DesiredSize.Width);
        }
        var width = double.IsInfinity(availableSize.Width) ? widest : availableSize.Width;
        return new Size(width, heights.Max());
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Columns = ColumnsFor(finalSize.Width);
        var columnWidth = ColumnWidth(finalSize.Width, Columns);
        var heights = new double[Columns];
        foreach (var child in Children)
        {
            if (!child.IsVisible) continue;
            var column = Shortest(heights);
            var x = column * (columnWidth + ColumnSpacing);
            child.Arrange(new Rect(x, heights[column], columnWidth, child.DesiredSize.Height));
            heights[column] += child.DesiredSize.Height;
        }
        return finalSize;
    }

    private static int Shortest(double[] heights)
    {
        var best = 0;
        for (var i = 1; i < heights.Length; i++)
            if (heights[i] < heights[best]) best = i;
        return best;
    }
}
