using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Cuebot.App.ViewModels.Extras;

namespace Cuebot.App.Views.Extras;

public partial class QuotesView : UserControl
{
    private static readonly FilePickerFileType JsonType = new("Quotes JSON") { Patterns = ["*.json"], MimeTypes = ["application/json"] };
    private static readonly FilePickerFileType CsvType = new("CSV") { Patterns = ["*.csv"], MimeTypes = ["text/csv"] };

    /// <summary>Delete on a quote in the list deletes it (Streamer.bot's Quotes view).</summary>
    public QuotesView()
    {
        InitializeComponent();
        PageKeys.Handle(QuoteList, e => e.Key == Avalonia.Input.Key.Delete && PageKeys.Plain(e)
                                        && PageKeys.Item<QuoteRowViewModel>(e) is { } quote && PageKeys.Run(quote.DeleteCommand));
    }

    private QuotesViewModel? Vm => DataContext as QuotesViewModel;

    private async void OnImport(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import quotes", AllowMultiple = false, FileTypeFilter = [JsonType, CsvType, FilePickerFileTypes.All],
        });
        if (files.Count == 0) return;
        try
        {
            await using var stream = await files[0].OpenReadAsync();
            using var reader = new StreamReader(stream);
            vm.Import(await reader.ReadToEndAsync(), files[0].Name, vm.ReplaceOnImport);
        }
        catch (IOException ex)
        {
            vm.Status = "Import failed: " + ex.Message;
        }
    }

    private void OnExportJson(object? sender, RoutedEventArgs e) => Export("quotes.json", JsonType, vm => vm.ExportJson());

    private void OnExportCsv(object? sender, RoutedEventArgs e) => Export("quotes.csv", CsvType, vm => vm.ExportCsv());

    private async void Export(string name, FilePickerFileType type, Func<QuotesViewModel, string> content)
    {
        if (Vm is not { } vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export quotes", SuggestedFileName = name, FileTypeChoices = [type], DefaultExtension = Path.GetExtension(name),
        });
        if (file is null) return;
        try
        {
            await using var stream = await file.OpenWriteAsync();
            await using var writer = new StreamWriter(stream);
            await writer.WriteAsync(content(vm));
            vm.ReportExport(file.Name);
        }
        catch (IOException ex)
        {
            vm.Status = "Export failed: " + ex.Message;
        }
    }
}
