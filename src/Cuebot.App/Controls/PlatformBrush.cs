using System.Globalization;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace Cuebot.App.Controls;

/// <summary>A connection's chip colour (platform brand colours; the accent for everything else) and its initial.</summary>
public sealed class PlatformBrush : IValueConverter
{
    public static readonly PlatformBrush Instance = new();

    public static readonly FuncValueConverter<string?, string> Initial =
        new(name => string.IsNullOrEmpty(name) ? "" : name[..1].ToUpperInvariant());

    private static readonly IReadOnlyDictionary<string, IBrush> Brands = new Dictionary<string, IBrush>(StringComparer.OrdinalIgnoreCase)
    {
        ["Twitch"] = new SolidColorBrush(Color.Parse("#9146FF")),
        ["YouTube"] = new SolidColorBrush(Color.Parse("#E5002D")),
        ["Kick"] = new SolidColorBrush(Color.Parse("#2E9E0B")),
        ["Rumble"] = new SolidColorBrush(Color.Parse("#5E9E2C")),
        ["OBS"] = new SolidColorBrush(Color.Parse("#4B5563")),
    };

    private static readonly IBrush Default = new SolidColorBrush(Color.Parse("#0079BC"));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value as string ?? "";
        foreach (var (brand, brush) in Brands)
            if (name.StartsWith(brand, StringComparison.OrdinalIgnoreCase)) return brush;
        return Default;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
