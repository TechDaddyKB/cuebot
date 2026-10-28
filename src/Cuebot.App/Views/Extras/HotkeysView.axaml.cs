using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Cuebot.App.ViewModels.Extras;

namespace Cuebot.App.Views.Extras;

public partial class HotkeysView : UserControl
{
    public HotkeysView()
    {
        InitializeComponent();
        // Tunnel so a recording sees the keys before any focused text box or button handles them.
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Refreshes the "Set Action" and "Group" lists each time a hotkey's context menu opens.</summary>
    private void OnHotkeyMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is ContextMenu { DataContext: HotkeyItemViewModel item }) item.RefreshMenus();
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is HotkeysViewModel { IsRecording: true } vm && vm.OnKey(e.Key, e.KeyModifiers)) e.Handled = true;
    }
}
