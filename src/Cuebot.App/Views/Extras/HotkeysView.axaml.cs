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

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is HotkeysViewModel { IsRecording: true } vm && vm.OnKey(e.Key, e.KeyModifiers)) e.Handled = true;
    }
}
