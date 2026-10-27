using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cuebot.App.ViewModels.Actions;

namespace Cuebot.App.Views;

public partial class ActionsView : UserControl
{
    public ActionsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ActionsViewModel vm)
            {
                vm.Picker.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(TypePickerViewModel.IsOpen) && vm.Picker.IsOpen) PickerSearch.Focus();
                };
            }
        };
    }

    private ActionsViewModel? ViewModel => DataContext as ActionsViewModel;

    private void OnItemClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: EditorItemViewModel item }) ViewModel?.SelectCommand.Execute(item);
    }

    private void OnPickerDoubleTapped(object? sender, TappedEventArgs e) => ViewModel?.Picker.ConfirmCommand.Execute(null);

    private void OnPickerKeyDown(object? sender, KeyEventArgs e)
    {
        if (ViewModel?.Picker is not { } picker) return;
        switch (e.Key)
        {
            case Key.Enter:
                picker.ConfirmCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Escape:
                picker.CancelCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Down or Key.Up when picker.Options.Count > 0:
                var index = picker.Selected is null ? -1 : picker.Options.IndexOf(picker.Selected);
                index = Math.Clamp(index + (e.Key == Key.Down ? 1 : -1), 0, picker.Options.Count - 1);
                picker.Selected = picker.Options[index];
                e.Handled = true;
                break;
        }
    }
}
