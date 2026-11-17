using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cuebot.App.ViewModels.Extras;

namespace Cuebot.App.Views.Extras;

public partial class StreamDataView : UserControl
{
    private static readonly FilePickerFileType JsonType = new("Stream data JSON") { Patterns = ["*.json"], MimeTypes = ["application/json"] };

    /// <summary>Buttons that need a second click say so.</summary>
    public static readonly IValueConverter ResetText =
        new FuncValueConverter<bool, string>(confirm => confirm ? "Click again to reset this stream's totals" : "Reset this stream's totals now");

    public static readonly IValueConverter RemoveTestsText =
        new FuncValueConverter<bool, string>(confirm => confirm ? "Click again to remove all test events" : "Remove all test events");

    public static readonly IValueConverter RemoveShownText =
        new FuncValueConverter<bool, string>(confirm => confirm ? "Click again to remove the events shown" : "Remove the events shown");

    public StreamDataView() => InitializeComponent();

    private StreamDataViewModel? Vm => DataContext as StreamDataViewModel;

    private async void OnExport(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export stream data", SuggestedFileName = $"stream-data-{DateTime.Now:yyyy-MM-dd}.json", FileTypeChoices = [JsonType],
            DefaultExtension = ".json",
        });
        if (file is null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(vm.Export());
            vm.ReportExport(file.Name);
        }
        catch (IOException ex)
        {
            vm.Status = "Export failed: " + ex.Message;
        }
    }

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import stream data", AllowMultiple = false, FileTypeFilter = [JsonType, FilePickerFileTypes.All],
        });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            vm.Import(await reader.ReadToEndAsync(), files[0].Name);
        }
        catch (IOException ex)
        {
            vm.Status = "Import failed: " + ex.Message;
        }
    }
}
