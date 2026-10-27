using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cuebot.App.ViewModels.Speech;

namespace Cuebot.App.Views.Speech;

public partial class SpeechGateView : UserControl
{
    public SpeechGateView() => InitializeComponent();

    private async void OnBrowseSaveFolder(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SpeechGateViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
            return;
        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Folder for spoken audio" });
        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) vm.SaveFolder = path;
    }
}
