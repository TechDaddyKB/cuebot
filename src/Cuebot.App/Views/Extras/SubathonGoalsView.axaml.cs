using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cuebot.App.ViewModels.Extras;

namespace Cuebot.App.Views.Extras;

public partial class SubathonGoalsView : UserControl
{
    private static readonly FilePickerFileType CsvType = new("CSV spreadsheet") { Patterns = ["*.csv"], MimeTypes = ["text/csv"] };

    /// <summary>Buttons that need a second click say so.</summary>
    public static readonly IValueConverter RemoveTestsText =
        new FuncValueConverter<bool, string>(confirm => confirm ? "Click again to take back all test changes" : "Take back all test changes");

    public static readonly IValueConverter RemoveShownText =
        new FuncValueConverter<bool, string>(confirm => confirm ? "Click again to take back the changes shown" : "Take back the changes shown");

    /// <summary>Removed changes are drawn faded.</summary>
    public static readonly IValueConverter RemovedOpacity = new FuncValueConverter<bool, double>(removed => removed ? 0.45 : 1);

    public SubathonGoalsView() => InitializeComponent();

    private async void OnExportHistory(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not SubathonGoalsViewModel { History: var vm } || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export timer and goal history", SuggestedFileName = $"subathon-history-{DateTime.Now:yyyy-MM-dd}.csv", FileTypeChoices = [CsvType],
            DefaultExtension = ".csv",
        });
        if (file is null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(vm.ExportCsv());
            vm.ReportExport(file.Name);
        }
        catch (IOException ex)
        {
            vm.Status = "Export failed: " + ex.Message;
        }
    }
}
