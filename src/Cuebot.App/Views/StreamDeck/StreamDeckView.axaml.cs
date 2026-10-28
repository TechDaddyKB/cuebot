using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Cuebot.App.ViewModels.StreamDeck;

namespace Cuebot.App.Views.StreamDeck;

public partial class StreamDeckView : UserControl
{
    public StreamDeckView() => InitializeComponent();

    /// <summary>Copy buttons: the button's Tag names what to copy (host, port or token).</summary>
    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { Tag: string what } || DataContext is not StreamDeckViewModel page) return;
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        try
        {
            await clipboard.SetTextAsync(page.CopyText(what));
            page.Message = what == "token" ? "Copied the token. Paste it into the plugin's API token field." : $"Copied the {what}.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
        {
            page.Message = "Could not copy.";
        }
    }
}
