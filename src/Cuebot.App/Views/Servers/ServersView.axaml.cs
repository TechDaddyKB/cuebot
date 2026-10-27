using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Cuebot.App.ViewModels.Servers;

namespace Cuebot.App.Views.Servers;

public partial class ServersView : UserControl
{
    public ServersView() => InitializeComponent();

    private async void OnCopyUrl(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: DockUrlViewModel dock } || DataContext is not ServersViewModel page) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try
        {
            await clipboard.SetTextAsync(dock.Url);
            page.Message = $"Copied {dock.Url}";
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            page.Message = "Could not copy the URL.";
        }
    }
}
