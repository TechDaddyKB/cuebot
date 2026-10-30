using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Cuebot.App.ViewModels;
using Cuebot.App.Views;
using Cuebot.Core;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Widgets;
using Cuebot.Scripting;
using Cuebot.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.UiTests;

/// <summary>
/// The Overlays &amp; Widgets page creates, renames, re-links and deletes overlays through the store and renders without
/// binding errors (screenshots "overlays*.png").
/// </summary>
public sealed class OverlaysPageTests : IAsyncDisposable
{
    private static readonly string ScreenshotDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshots"));

    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-overlays-").FullName;
    private readonly ServiceProvider _services;

    public OverlaysPageTests()
    {
        _services = new ServiceCollection().AddCuebotCore(new AppPaths(_dataDir)).AddScripting().AddServers().BuildServiceProvider();
        Directory.CreateDirectory(ScreenshotDir);
        TestApp.BindingErrors.Clear();
    }

    private OverlayStore Store => _services.GetRequiredService<OverlayStore>();
    private ServerManager Servers => _services.GetRequiredService<ServerManager>();

    private static Window Open(Control view, object viewModel)
    {
        view.DataContext = viewModel;
        var window = new Window { Content = view, Width = 1200, Height = 700 };
        window.Show();
        Pump();
        return window;
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void AssertNoBindingErrors() =>
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));

    [AvaloniaFact]
    public void Empty_page_says_when_the_overlay_server_is_not_running()
    {
        var vm = new OverlaysViewModel(Store, Servers);
        var window = Open(new OverlaysView(), vm);
        Assert.False(vm.HasOverlays);
        Assert.Equal("The overlay server is not running, so these links will not load yet.", vm.ServerHint);   // on by default, not started here
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public void Overlays_are_created_renamed_relinked_and_deleted()
    {
        var vm = new OverlaysViewModel(Store, Servers);
        var window = Open(new OverlaysView(), vm);

        vm.NewName = "Main scene";
        vm.CreateCommand.Execute(null);
        vm.NewCanvas = OverlaysViewModel.Presets.Single(p => p.Width == 1080 && p.Height == 1920);
        vm.CreateCommand.Execute(null);
        vm.NewCanvas = OverlaysViewModel.Presets.Last();
        Assert.True(vm.IsCustomSize);
        (vm.NewWidth, vm.NewHeight) = (800, 600);
        vm.CreateCommand.Execute(null);
        Pump();

        Assert.Equal(3, vm.Overlays.Count);
        var main = vm.Overlays.Single(o => o.Name == "Main scene");
        Assert.Equal("1920 × 1080", main.Canvas);
        Assert.Contains(vm.Overlays, o => o.Canvas == "1080 × 1920");
        Assert.Contains(vm.Overlays, o => o.Canvas == "800 × 600");
        var stored = Store.Get(main.Id)!;
        Assert.Equal($"http://127.0.0.1:7476/o/{stored.Id}?token={stored.Token}", main.Url);
        Assert.DoesNotContain(stored.Token, main.DisplayUrl);
        Assert.StartsWith($"http://127.0.0.1:7476/o/{stored.Id}?token={stored.Token[..5]}", main.DisplayUrl);
        SaveBothThemes(window);

        main.Name = "Starting soon";
        Assert.Equal("Starting soon", Store.Get(main.Id)!.Name);

        main.NewLinkCommand.Execute(null);
        Pump();
        Assert.NotEqual(stored.Token, Store.Get(main.Id)!.Token);
        Assert.Contains("old one stop working", vm.Status);

        vm.Overlays.Single(o => o.Id == main.Id).DeleteCommand.Execute(null);
        Pump();
        Assert.Null(Store.Get(main.Id));
        Assert.Equal(2, vm.Overlays.Count);
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public void Widgets_are_imported_moved_and_removed()
    {
        var library = _services.GetRequiredService<WidgetLibrary>();
        var opened = new List<string>();
        var vm = new OverlaysViewModel(Store, Servers, library) { OpenUrl = opened.Add };
        var window = Open(new OverlaysView(), vm);
        vm.CreateCommand.Execute(null);
        Pump();
        var row = Assert.Single(vm.Overlays);

        var zip = new MemoryStream();
        using (var archive = new System.IO.Compression.ZipArchive(zip, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        using (var writer = new StreamWriter(archive.CreateEntry("html.txt").Open()))
            writer.Write("<div></div>");
        zip.Position = 0;
        vm.AddWidget(row.Id, zip, "pink_chat_widget.zip");
        Pump();

        row = Assert.Single(vm.Overlays);
        var layer = Assert.Single(row.Layers);
        Assert.Equal("pink chat widget", layer.Name);
        Assert.Equal((1920m, 1080m), (layer.Width, layer.Height));
        layer.X = 40;
        layer.Width = 600;
        var saved = Store.Get(row.Id)!.Layers.Single();
        Assert.Equal((40.0, 600.0), (saved.X, saved.Width));
        SaveBothThemes(window, "overlays-widgets");

        row.AddChatBoxCommand.Execute(null);
        Pump();
        row = Assert.Single(vm.Overlays);
        Assert.Equal(["pink chat widget", "Chat box"], row.Layers.Select(l => l.Name));
        Assert.Contains("chat box", vm.Status);
        SaveBothThemes(window, "overlays-chatbox");
        layer = row.Layers[0];

        row.DesignCommand.Execute(null);
        Assert.Contains("designer runs on the overlay server", vm.Status);
        Assert.Empty(opened);
        layer.EditCommand.Execute(null);
        Assert.Contains("not running", vm.Status);   // the overlay server is not started in this test

        vm.AddWidget(row.Id, new MemoryStream([1, 2, 3]), "broken.zip");
        Assert.StartsWith("Could not add the widget", vm.Status);

        layer.RemoveCommand.Execute(null);
        Pump();
        Assert.Equal("Chat box", Assert.Single(Store.Get(row.Id)!.Layers).Name);
        AssertNoBindingErrors();
        window.Close();
    }

    private static void SaveBothThemes(Window window, string name = "overlays")
    {
        var app = Avalonia.Application.Current!;
        var original = app.RequestedThemeVariant;
        try
        {
            foreach (var (theme, suffix) in new[] { (ThemeVariant.Dark, ""), (ThemeVariant.Light, "-light") })
            {
                app.RequestedThemeVariant = theme;
                Pump();
                TestApp.SaveFrame(window, Path.Combine(ScreenshotDir, $"{name}{suffix}.png"));
            }
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
    }
}
