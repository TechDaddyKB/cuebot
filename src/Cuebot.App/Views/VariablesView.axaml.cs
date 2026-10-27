using Avalonia.Controls;
using Avalonia.Interactivity;
using Cuebot.App.ViewModels;

namespace Cuebot.App.Views;

public partial class VariablesView : UserControl
{
    public VariablesView() => InitializeComponent();

    private void OnDeleteRow(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: VariableRow row } && DataContext is VariablesViewModel vm) vm.DeleteCommand.Execute(row);
    }
}
