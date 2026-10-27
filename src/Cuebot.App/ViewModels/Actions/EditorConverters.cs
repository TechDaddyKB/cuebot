using Avalonia.Data.Converters;

namespace Cuebot.App.ViewModels.Actions;

public static class EditorConverters
{
    /// <summary>Dims disabled items.</summary>
    public static readonly IValueConverter EnabledOpacity =
        new FuncValueConverter<bool, double>(enabled => enabled ? 1.0 : 0.45);
}
