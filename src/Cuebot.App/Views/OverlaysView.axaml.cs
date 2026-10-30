using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cuebot.App.ViewModels;

namespace Cuebot.App.Views;

public partial class OverlaysView : UserControl
{
    public OverlaysView() => InitializeComponent();

    private static readonly FilePickerFileType WidgetZip = new("StreamElements widget (.zip)") { Patterns = ["*.zip"] };

    private async void OnAddWidget(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: OverlayRowViewModel row } || DataContext is not OverlaysViewModel page) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add a widget", AllowMultiple = false, FileTypeFilter = [WidgetZip, FilePickerFileTypes.All],
        });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            copy.Position = 0;
            page.AddWidget(row.Id, copy, files[0].Name);
        }
        catch (IOException ex)
        {
            page.Status = "Could not read the file: " + ex.Message;
        }
    }

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
