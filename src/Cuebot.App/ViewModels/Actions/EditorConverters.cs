using Avalonia.Data.Converters;

namespace Cuebot.App.ViewModels.Actions;

public static class EditorConverters
{
    /// <summary>Dims disabled items.</summary>
    public static readonly IValueConverter EnabledOpacity =
        new FuncValueConverter<bool, double>(enabled => enabled ? 1.0 : 0.45);

    /// <summary>
    /// True for 0, false otherwise, including when nothing is selected (a negated <c>SelectedAction?.X</c> binding fails on
    /// null, which happens when the selected action is deleted from outside the page).
    /// </summary>
    public static readonly IValueConverter IsZero = new FuncValueConverter<int?, bool>(n => n == 0);

    /// <summary>True for false only; null (nothing selected) is false too. See <see cref="IsZero"/>.</summary>
    public static readonly IValueConverter IsFalse = new FuncValueConverter<bool?, bool>(b => b == false);
}
