using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Cuebot.App.ViewModels;

namespace Cuebot.App.Views;

public partial class OverlaysView : UserControl
{
    public OverlaysView() => InitializeComponent();

    private async void OnCopyLink(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: OverlayRowViewModel row } || DataContext is not OverlaysViewModel page) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try
        {
            await clipboard.SetTextAsync(row.Url);
            page.Copied(row);
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            page.Status = "Could not copy the link.";
        }
    }
}
