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
    private static readonly FilePickerFileType Package = new("Cuebot overlay package") { Patterns = ["*" + Cuebot.Core.Overlays.Sharing.OverlayPackages.FileExtension] };

    private async void OnImportPackage(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not OverlaysViewModel page || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import an overlay package", AllowMultiple = false, FileTypeFilter = [Package, FilePickerFileTypes.All],
        });
        if (files.Count == 0) return;
        await ImportPackage(page, files[0], null);
    }

    private static async Task ImportPackage(OverlaysViewModel page, IStorageFile file, Guid? overlayId)
    {
        try
        {
            await using var stream = await file.OpenReadAsync();
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            copy.Position = 0;
            await page.ImportPackageAsync(copy, overlayId);
        }
        catch (IOException ex)
        {
            page.Status = "Could not read the file: " + ex.Message;
        }
    }

    /// <summary>Saves an overlay (or one widget) as a package; Tag "custom" means the user confirmed they may share its code.</summary>
    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control control || DataContext is not OverlaysViewModel page) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var (overlayId, layerId, name) = control.DataContext switch
        {
            OverlayRowViewModel row => (row.Id, (Guid?)null, row.Name),
            LayerRowViewModel layer => (layer.OverlayId, (Guid?)layer.Id, layer.Name),
            _ => (Guid.Empty, null, ""),
        };
        if (overlayId == Guid.Empty) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save the package", SuggestedFileName = SafeName(name) + Cuebot.Core.Overlays.Sharing.OverlayPackages.FileExtension,
            DefaultExtension = Cuebot.Core.Overlays.Sharing.OverlayPackages.FileExtension.TrimStart('.'), FileTypeChoices = [Package],
        });
        if (file is null) return;
        try
        {
            using var buffer = new MemoryStream();
            page.Export(overlayId, layerId, buffer, includeCustomCode: control.Tag as string == "custom");
            buffer.Position = 0;
            await using var output = await file.OpenWriteAsync();
            output.SetLength(0);
            await buffer.CopyToAsync(output);
        }
        catch (IOException ex)
        {
            page.Status = "Could not save the package: " + ex.Message;
        }
    }

    private static string SafeName(string name)
    {
        var safe = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c).ToArray()).Trim();
        return safe.Length > 0 ? safe : "overlay";
    }

    private async void OnAddWidget(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: OverlayRowViewModel row } || DataContext is not OverlaysViewModel page) return;
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Add a widget", AllowMultiple = false, FileTypeFilter = [WidgetZip, Package, FilePickerFileTypes.All],
        });
        if (files.Count == 0) return;
        if (files[0].Name.EndsWith(Cuebot.Core.Overlays.Sharing.OverlayPackages.FileExtension, StringComparison.OrdinalIgnoreCase))
        {
            await ImportPackage(page, files[0], row.Id);
            return;
        }
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
