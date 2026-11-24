using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cuebot.App.ViewModels.Speech;

namespace Cuebot.App.Views.Speech;

public partial class SpeechEnginesView : UserControl
{
    private static readonly FilePickerFileType VoiceFiles = new("Voices and voice packages")
    {
        Patterns = ["*.onnx", "*.onnx.json", "*.json", "*.tar.bz2", "*.tbz2", "*.tar.gz", "*.tgz", "*.bz2", "*.gz"],
    };

    public SpeechEnginesView() => InitializeComponent();

    /// <summary>Picks voice files for the voice library panel to import (a picker needs the window, so it lives here).</summary>
    private async void OnImportVoices(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: VoiceLibraryPanelViewModel panel } || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import voices", AllowMultiple = true, FileTypeFilter = [VoiceFiles, FilePickerFileTypes.All],
        });
        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        foreach (var file in files) file.Dispose();
        await panel.ImportAsync(paths);
    }
}
