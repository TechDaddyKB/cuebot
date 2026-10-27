using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cuebot.App.ViewModels.Transfer;
using Cuebot.Core.Transfer;

namespace Cuebot.App.Views.Transfer;

public partial class ImportExportView : UserControl
{
    private static readonly FilePickerFileType CuebotType = new("Cuebot bundle") { Patterns = ["*" + TransferCodec.FileExtension] };
    private static readonly FilePickerFileType TextType = new("Import string") { Patterns = ["*.txt", "*.sb"], MimeTypes = ["text/plain"] };

    public ImportExportView() => InitializeComponent();

    private ImportExportViewModel? Vm => DataContext as ImportExportViewModel;

    private async void OnOpenFile(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open import", AllowMultiple = false, FileTypeFilter = [CuebotType, TextType, FilePickerFileTypes.All],
        });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            if (stream.CanSeek && stream.Length > TransferCodec.MaxInputChars)
            {
                vm.ImportStatus = "Cannot import: the file is too large.";
                return;
            }
            using var reader = new StreamReader(stream);
            vm.LoadText(await reader.ReadToEndAsync(), files[0].Name);
        }
        catch (IOException ex)
        {
            vm.ImportStatus = "Could not read the file: " + ex.Message;
        }
    }

    private async void OnSaveFile(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        if (vm.ExportFileContent() is not { } content) return;
        var name = string.IsNullOrWhiteSpace(vm.ExportName) ? "export" : string.Concat(vm.ExportName.Split(Path.GetInvalidFileNameChars()));
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save export", SuggestedFileName = name + TransferCodec.FileExtension, FileTypeChoices = [CuebotType],
            DefaultExtension = TransferCodec.FileExtension,
        });
        if (file is null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(content);
            vm.ReportSaved(file.Name);
        }
        catch (IOException ex)
        {
            vm.ExportStatus = "Save failed: " + ex.Message;
        }
    }
}
