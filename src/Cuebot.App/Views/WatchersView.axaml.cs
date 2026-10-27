using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cuebot.App.ViewModels;

namespace Cuebot.App.Views;

public partial class WatchersView : UserControl
{
    public WatchersView() => InitializeComponent();

    private async void OnBrowseFolder(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: FileWatcherItemViewModel item } || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder to watch", AllowMultiple = false });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            item.Folder = path;
            item.Flush();
        }
    }

    private async void OnBrowseFile(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: FileTailItemViewModel item } || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
            return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "File to follow", AllowMultiple = false });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            item.FilePath = path;
            item.Flush();
        }
    }
}
