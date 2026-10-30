using System.IO.Compression;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels.Transfer;
using Cuebot.App.Views.Transfer;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Transfer;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.UiTests.Transfer;

/// <summary>
/// The Import / Export page: export builds a string from ticked items, import previews it as a tree with conflict
/// markers and imports the chosen items; a Streamer.bot string shows its report. Screenshots are saved to
/// tests/Cuebot.UiTests/screenshots with the "transfer-" prefix.
/// </summary>
public sealed class ImportExportPageTests : IAsyncDisposable
{
    private static readonly string ScreenshotDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshots"));

    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-transfer-").FullName;
    private readonly ServiceProvider _services;

    public ImportExportPageTests()
    {
        _services = new ServiceCollection().AddCuebotCore(new AppPaths(_dataDir)).AddTransferFeatures().BuildServiceProvider();
        Directory.CreateDirectory(ScreenshotDir);
        TestApp.BindingErrors.Clear();
    }

    private T Get<T>() where T : notnull => _services.GetRequiredService<T>();

    private ImportExportViewModel NewViewModel() =>
        new(Get<TransferService>(), Get<ProfileService>(), Get<CommandStore>(), Get<GlobalVariables>());

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void ExpandAll(Window window)
    {
        for (var pass = 0; pass < 3; pass++)
        {
            foreach (var item in window.GetVisualDescendants().OfType<TreeViewItem>()) item.IsExpanded = true;
            Pump();
        }
    }

    private static void SaveBothThemes(Window window, string name)
    {
        var app = Avalonia.Application.Current!;
        var original = app.RequestedThemeVariant;
        try
        {
            foreach (var (theme, suffix) in new[] { (ThemeVariant.Dark, ""), (ThemeVariant.Light, "-light") })
            {
                app.RequestedThemeVariant = theme;
                Pump();
                TestApp.SaveFrame(window, Path.Combine(ScreenshotDir, $"transfer-{name}{suffix}.png"));
            }
        }
        finally
        {
            app.RequestedThemeVariant = original;
            Pump();
        }
    }

    private static void AssertNoBindingErrors() =>
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));

    private void Seed()
    {
        var profile = Get<ProfileService>();
        profile.Profile.Queues.Add(new QueueDefinition { Name = "Alerts" });
        profile.Profile.Actions.Add(new ActionDefinition
        {
            Name = "Shoutout", Group = "Chat", Queue = "Alerts",
            Triggers = [new TriggerDefinition { Type = "command.triggered", Settings = { ["command"] = "Shoutout" } }],
            SubActions =
            [
                new SubActionDefinition { Type = "chat.send", Parameters = { ["message"] = "Go follow %input0%!" } },
                new SubActionDefinition { Type = "core.runAction", Parameters = { ["action"] = "Celebrate" } },
            ],
        });
        profile.Profile.Actions.Add(new ActionDefinition { Name = "Celebrate", SubActions = [new SubActionDefinition { Type = "core.delay" }] });
        profile.Profile.Timers.Add(new TimerDefinition { Name = "Socials" });
        profile.NotifyChanged();
        Get<CommandStore>().Save(new CommandDefinition { Name = "Shoutout", Commands = ["!so"] });
        Get<GlobalVariables>().SetValue("deaths", 3L, persist: true);
    }

    [AvaloniaFact]
    public async Task Export_then_import_shows_conflicts_and_imports_copies()
    {
        Seed();
        var vm = NewViewModel();
        var view = new ImportExportView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 860 };
        window.Show();
        Pump();

        Assert.Equal(2, vm.ExportActions.Count);
        Assert.Single(vm.ExportCommands);
        Assert.Single(vm.ExportGlobals);
        vm.ExportActions.Single(a => a.Name == "Shoutout").IsSelected = true;
        vm.ExportGlobals[0].IsSelected = true;
        vm.ExportName = "Shoutout pack";
        vm.ExportAuthor = "Kris";
        vm.BuildExportCommand.Execute(null);
        Assert.StartsWith(TransferCodec.StringPrefix, vm.ExportString);
        Assert.Contains("2 action(s)", vm.ExportStatus);
        Assert.Contains("\"name\": \"Shoutout pack\"", vm.ExportFileContent());

        view.FindControl<TabControl>("Tabs")!.SelectedIndex = 1;
        Pump();
        SaveBothThemes(window, "export");

        view.FindControl<TabControl>("Tabs")!.SelectedIndex = 0;
        vm.ImportText = vm.ExportString;
        vm.PreviewCommand.Execute(null);
        Pump();
        Assert.True(vm.HasPlan);
        Assert.Contains("Shoutout pack", vm.PreviewHeader);
        Assert.Contains("conflict", vm.ImportStatus);
        var actions = vm.PreviewGroups.Single(g => g.Title == "Actions");
        Assert.All(actions.Items, i => Assert.True(i.HasConflict));
        ExpandAll(window);
        Assert.True(window.GetVisualDescendants().OfType<TreeViewItem>().Count() >= 5);
        SaveBothThemes(window, "import-preview");

        foreach (var item in actions.Items) item.Mode = "Add as new";
        vm.PreviewGroups.Single(g => g.Title == "Commands").IncludeAll = false;
        await vm.ImportAsync();
        Assert.False(vm.HasPlan);
        Assert.StartsWith("Imported", vm.ImportStatus);
        var snapshot = Get<ProfileService>().Snapshot;
        Assert.Equal(4, snapshot.Actions.Count);
        Assert.Equal("Celebrate (2)", snapshot.FindAction("Shoutout (2)")!.SubActions[1].Parameters["action"]);
        Assert.Single(Get<CommandStore>().Snapshot);
        Pump();
        Assert.Equal(4, vm.ExportActions.Count); // the export list follows profile changes
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public void Streamer_bot_string_shows_the_import_report()
    {
        var vm = NewViewModel();
        var window = new Window { Content = new ImportExportView { DataContext = vm }, Width = 1280, Height = 860 };
        window.Show();
        Pump();

        const string json = """
            { "meta": { "name": "SB pack", "author": "Someone" },
              "data": { "actions": [ { "id": "6f0c2a1e-8a3f-4b55-9c1e-0a7d9b3e2f10", "name": "Raid alert", "group": "Alerts",
                "triggers": [ { "type": "Raid", "min": 10 }, { "type": "Hypetrain" } ],
                "subActions": [
                  { "type": "SendMessage", "text": "Welcome raiders!", "useBot": true },
                  { "type": "ObsSetScene", "sceneName": "Raid" },
                  { "type": "ExecuteCode", "code": "public class CPHInline { public bool Execute() { return true; } }" },
                  { "type": "SomethingNew", "value": 1 } ] } ] } }
            """;
        using var buffer = new MemoryStream();
        buffer.Write("SBAE"u8);
        using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true)) gzip.Write(Encoding.UTF8.GetBytes(json));
        vm.LoadText(Convert.ToBase64String(buffer.ToArray()), "raid.txt");
        Pump();

        Assert.True(vm.HasPlan);
        Assert.Contains("from Streamer.bot", vm.PreviewHeader);
        Assert.Equal(2, vm.ReportPlaceholders.Count);
        Assert.True(vm.HasReportWarnings);
        Assert.Contains(vm.ReportMapped, l => l.Contains("twitch.raid"));
        var item = Assert.Single(vm.PreviewGroups.Single(g => g.Title == "Actions").Items);
        Assert.True(item.ContainsCode);
        Assert.True(item.HasPlaceholders);
        ExpandAll(window);
        SaveBothThemes(window, "streamerbot-report");

        vm.ImportText = "not an import";
        vm.PreviewCommand.Execute(null);
        Assert.False(vm.HasPlan);
        Assert.StartsWith("Cannot import", vm.ImportStatus);
        Pump();
        AssertNoBindingErrors();
        window.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
