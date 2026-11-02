using Avalonia.Controls;
using Avalonia.Data.Converters;

namespace Cuebot.App.Views.Extras;

public partial class TopSpendersView : UserControl
{
    /// <summary>The clear button's text: a second click is needed to clear.</summary>
    public static readonly IValueConverter ClearText =
        new FuncValueConverter<bool, string>(confirm => confirm ? "Click again to clear all history" : "Clear all history");

    public TopSpendersView() => InitializeComponent();
}
