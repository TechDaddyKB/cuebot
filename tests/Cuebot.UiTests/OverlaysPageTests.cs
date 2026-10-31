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
    public void Alert_queue_buttons_pause_skip_and_mute_every_overlay()
    {
        var queue = _services.GetRequiredService<OverlayAlertQueue>();
        var commands = new List<string>();
        using var feed = _services.GetRequiredService<OverlayFeed>().Subscribe(m => commands.Add(System.Text.Json.Nodes.JsonNode.Parse(m.Json)!["command"]!.GetValue<string>()));
        var vm = new OverlaysViewModel(Store, Servers, alerts: queue);
        var window = Open(new OverlaysView(), vm);
        Assert.True(vm.HasAlertQueue);

        vm.AlertsPaused = true;
        vm.SkipAlertCommand.Execute(null);
        vm.AlertsMuted = true;
        Assert.Equal((true, true), (queue.Paused, queue.Muted));
        queue.Paused = false;   // from an action or the API: the page follows
        Pump();
        Assert.False(vm.AlertsPaused);
        Assert.Equal(["pause", "skip", "mute", "resume"], commands);
        Assert.False(new OverlaysViewModel(Store, Servers).HasAlertQueue);
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public void The_chat_poll_panel_runs_polls_and_predictions()
    {
        var polls = _services.GetRequiredService<Cuebot.Core.Polls.ChatPolls>();
        var points = _services.GetRequiredService<Cuebot.Core.Points.PointsService>();
        var panel = new ChatPollPanelViewModel(polls, _services.GetRequiredService<Cuebot.Core.Polls.PollTracker>());
        var vm = new OverlaysViewModel(Store, Servers, polls: panel);
        var window = Open(new OverlaysView(), vm);
        Assert.True(vm.HasPolls);

        panel.PollTitle = "Next game?";
        panel.PollChoices = "Horror\nRacing";
        panel.PollSeconds = 0;
        panel.StartPollCommand.Execute(null);
        Pump();
        Assert.True(panel.PollRunning);
        Assert.Equal("Poll started; end it when you are ready.", panel.Message);
        panel.StartPollCommand.Execute(null);
        Assert.Contains("already running", panel.Message);
        panel.EndPollCommand.Execute(null);
        Pump();
        Assert.False(panel.PollRunning);

        points.Add("Twitch", "a1", "Ann", 100);
        panel.PredictionTitle = "Win?";
        panel.PredictionOutcomes = "Yes\nNo";
        panel.StartPredictionCommand.Execute(null);
        Pump();
        Assert.Equal(["Yes", "No"], panel.Outcomes);
        Assert.True(panel.CanLock);
        panel.ResolvePredictionCommand.Execute(null);
        Assert.Equal("Pick the winning outcome first.", panel.Message);
        panel.LockPredictionCommand.Execute(null);
        Pump();
        Assert.False(panel.CanLock);
        panel.Winner = "No";
        panel.ResolvePredictionCommand.Execute(null);
        Pump();
        Assert.False(panel.PredictionOpen);
        Assert.Equal("\"No\" won; winners have been paid.", panel.Message);
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_media_requests_page_runs_the_queue()
    {
        using var media = new Cuebot.Core.Media.MediaRequests(null, new Cuebot.Core.Logging.AppLog(), _services.GetRequiredService<Cuebot.Core.Events.EventBus>(),
            _services.GetRequiredService<OverlayFeed>(), new TitleLookup());
        var vm = new MediaRequestsViewModel(media);
        var window = Open(new MediaRequestsView(), vm);
        Assert.Equal(("Nothing is playing.", "The queue is empty."), (vm.NowPlaying, vm.QueueSummary));

        vm.NewLink = "https://youtu.be/dQw4w9WgXcQ";
        await vm.AddCommand.ExecuteAsync(null);
        vm.NewLink = "M7lc1UVf-VE";
        await vm.AddCommand.ExecuteAsync(null);
        Pump();
        Assert.Equal("Video dQw4w9WgXcQ — asked by you (Cuebot)", vm.NowPlaying);
        Assert.Equal("1 waiting", vm.QueueSummary);
        vm.NewLink = "not a link";
        await vm.AddCommand.ExecuteAsync(null);
        Assert.Equal("Not added: that is not a YouTube link.", vm.Status);

        vm.MaxMinutes = 2.5m;
        vm.Cost = 50;
        vm.ModSkipCommand = "!next song";
        Assert.Equal((150, 50L, "!next"), (media.Settings.MaxSeconds, media.Settings.Cost, media.Settings.SkipCommand));
        vm.TogglePauseCommand.Execute(null);
        Pump();
        Assert.True(vm.Paused);
        vm.SkipCommand.Execute(null);
        Pump();
        Assert.Equal(("Nothing is playing.", 1), (vm.NowPlaying, vm.Queue.Count));   // paused: the next waits
        vm.Queue[0].RemoveCommand.Execute(null);
        Pump();
        Assert.Empty(vm.Queue);
        Assert.Equal(["Video dQw4w9WgXcQ — you"], vm.Played);
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Overlays_are_imported_from_the_streamelements_account()
    {
        var source = new FakeStreamElements();
        var vm = new OverlaysViewModel(Store, Servers, _services.GetRequiredService<WidgetLibrary>(), streamElements: source);
        var window = Open(new OverlaysView(), vm);
        Assert.True(vm.CanImportFromStreamElements);

        await vm.ShowStreamElementsOverlaysCommand.ExecuteAsync(null);
        Assert.StartsWith("Connect StreamElements first", vm.Status);
        Assert.False(vm.StreamElementsOpen);

        source.HasToken = true;
        await vm.ShowStreamElementsOverlaysCommand.ExecuteAsync(null);
        Pump();
        Assert.True(vm.StreamElementsOpen);
        var row = Assert.Single(vm.StreamElementsOverlays);
        Assert.Equal(("Stream scene", "1280 × 720"), (row.Name, row.Canvas));
        TestApp.SaveFrame(window, Path.Combine(ScreenshotDir, "overlays-streamelements.png"));

        await row.ImportCommand.ExecuteAsync(null);
        Pump();
        var imported = Assert.Single(vm.Overlays);
        Assert.Equal(("Stream scene", "1280 × 720", 1), (imported.Name, imported.Canvas, imported.Layers.Count));
        Assert.Equal("Imported \"Stream scene\" with 1 widget. Not imported: se-widget-kappagen (not supported). Copy its link into OBS; alert and label settings can be changed with Edit….", vm.Status);

        source.Fail = true;
        await row.ImportCommand.ExecuteAsync(null);
        Assert.Equal("Could not import \"Stream scene\": StreamElements API error 500", vm.Status);
        vm.CloseStreamElementsOverlaysCommand.Execute(null);
        Assert.False(vm.StreamElementsOpen);
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Overlays_and_widgets_are_shared_as_packages()
    {
        var library = _services.GetRequiredService<WidgetLibrary>();
        var packages = _services.GetRequiredService<Cuebot.Core.Overlays.Sharing.OverlayPackages>();
        var source = Store.Create("Main scene", 1920, 1080);
        library.AddAlertBox(source.Id);
        library.AddCustomWidget(source.Id, "Bought chat", new WidgetCode("<p></p>", "", "/* paid */", "{}", "{}"));
        var vm = new OverlaysViewModel(Store, Servers, library, packages: packages);
        var window = Open(new OverlaysView(), vm);
        var row = Assert.Single(vm.Overlays);
        Assert.True((row.CanShare, row.HasCustomCode) == (true, true));
        Assert.Equal([false, true], row.Layers.Select(l => l.HasCustomCode));
        TestApp.SaveFrame(window, Path.Combine(ScreenshotDir, "overlays-share.png"));

        using var package = new MemoryStream();
        vm.Export(row.Id, null, package, includeCustomCode: false);
        Assert.Equal("Saved the package. Left out (code not yours to share): Bought chat.", vm.Status);
        package.Position = 0;
        await vm.ImportPackageAsync(package);
        Pump();
        Assert.Equal("Imported \"Main scene\" with 1 layer. It has its own new link for OBS. Not included: Bought chat (shared without its code; add the widget yourself).", vm.Status);
        Assert.Equal(2, vm.Overlays.Count);

        using var widget = new MemoryStream();
        vm.Export(row.Id, row.Layers[1].Id, widget, includeCustomCode: true);
        widget.Position = 0;
        await vm.ImportPackageAsync(widget, row.Id);
        Pump();
        Assert.Equal("Added the widget to \"Main scene\".", vm.Status);
        Assert.Equal(3, Store.Get(source.Id)!.Layers.Count);

        await vm.ImportPackageAsync(new MemoryStream([1, 2, 3]));
        Assert.Equal("Could not import the package: This file is not a Cuebot overlay package.", vm.Status);
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_gallery_lists_items_with_previews_and_imports_them()
    {
        var library = _services.GetRequiredService<WidgetLibrary>();
        var packages = _services.GetRequiredService<Cuebot.Core.Overlays.Sharing.OverlayPackages>();
        // Two gallery packages made the way an author would: a whole overlay and a single alert box.
        var source = Store.Create("Neon scene", 1280, 720);
        var alert = library.AddAlertBox(source.Id)!;
        library.AddChatBox(source.Id);
        using var overlayPackage = new MemoryStream();
        packages.ExportOverlay(source.Id, overlayPackage, includeCustomCode: false);
        using var widgetPackage = new MemoryStream();
        packages.ExportWidget(source.Id, alert.Id, widgetPackage, includeCustomCode: false);
        Store.Delete(source.Id);

        var png = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==");
        string Entry(string id, string kind, byte[] package) => $$"""
            {"id":"{{id}}","name":"{{id}}","author":"Someone","description":"Shared for the test.","kind":"{{kind}}","license":"CC-BY-4.0",
             "tags":["neon"],"package":"items/{{id}}/package.cuebot-overlay","preview":"items/{{id}}/preview.png","size":{{package.Length}},
             "sha256":"{{Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(package))}}"}
            """;
        var files = new Dictionary<string, byte[]>
        {
            ["index.json"] = System.Text.Encoding.UTF8.GetBytes($$"""{"format":"cuebot-gallery","version":1,"items":[{{Entry("neon-scene", "overlay", overlayPackage.ToArray())}},{{Entry("neon-alerts", "widget", widgetPackage.ToArray())}}]}"""),
            ["items/neon-scene/package.cuebot-overlay"] = overlayPackage.ToArray(),
            ["items/neon-alerts/package.cuebot-overlay"] = widgetPackage.ToArray(),
            ["items/neon-scene/preview.png"] = png,
        };
        var gallery = new Cuebot.Core.Overlays.Sharing.OverlayGallery(new HttpClient(new GalleryHandler(files)), new Uri("https://gallery.test/"));
        var opened = new List<string>();
        var vm = new OverlaysViewModel(Store, Servers, library, packages: packages, gallery: gallery) { OpenUrl = opened.Add };
        var window = Open(new OverlaysView(), vm);
        Assert.True(vm.CanBrowseGallery);

        await vm.BrowseGalleryCommand.ExecuteAsync(null);
        Pump();
        Assert.True(vm.GalleryOpen);
        Assert.Equal("2 items in the community gallery. Every item was reviewed before it was listed.", vm.Status);
        Assert.Equal(["neon-scene", "neon-alerts"], vm.GalleryItems.Select(i => i.Name));
        Assert.Equal(("by Someone", "Overlay · CC-BY-4.0 · " + vm.GalleryItems[0].Details.Split(" · ")[2] + " · #neon"), (vm.GalleryItems[0].Byline, vm.GalleryItems[0].Details));
        Assert.NotNull(vm.GalleryItems[0].Preview);
        Assert.Null(vm.GalleryItems[1].Preview);   // no preview file: still listed
        Assert.Null(vm.GalleryTarget);   // no overlays yet

        await vm.GalleryItems[1].ImportCommand.ExecuteAsync(null);
        Assert.StartsWith("Pick the overlay to add this widget to", vm.Status);

        await vm.GalleryItems[0].ImportCommand.ExecuteAsync(null);
        Pump();
        var imported = Assert.Single(vm.Overlays);
        Assert.Equal(("Neon scene", 2), (imported.Name, imported.Layers.Count));   // the index's name is not used; the package's is
        vm.GalleryTarget = imported;
        await vm.GalleryItems[1].ImportCommand.ExecuteAsync(null);
        Pump();
        Assert.Equal("Added the widget to \"Neon scene\".", vm.Status);
        Assert.Equal(3, vm.Overlays.Single().Layers.Count);
        Assert.Same(vm.Overlays.Single(), vm.GalleryTarget);   // still selected after the page refreshed

        vm.GalleryItems[0].ReportCommand.Execute(null);
        vm.ShareToGalleryCommand.Execute(null);
        Assert.Equal(["https://github.com/TechDaddyKB/cuebot-gallery/issues/new?template=report.yml&item=neon-scene",
            "https://github.com/TechDaddyKB/cuebot-gallery/wiki/Detailed-guide"], opened);
        TestApp.SaveFrame(window, Path.Combine(ScreenshotDir, "overlays-gallery.png"));
        AssertNoBindingErrors();
        window.Close();
    }

    private sealed class GalleryHandler(Dictionary<string, byte[]> files) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(files.TryGetValue(request.RequestUri!.AbsolutePath.TrimStart('/'), out var bytes)
                ? new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                : new HttpResponseMessage(System.Net.HttpStatusCode.NotFound));
    }

    private sealed class FakeStreamElements : IStreamElementsOverlaySource
    {
        public bool HasToken { get; set; }
        public bool Fail { get; set; }

        public Task<IReadOnlyList<StreamElementsOverlaySummary>> ListOverlaysAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<StreamElementsOverlaySummary>>([new("o1", "Stream scene", 1280, 720)]);

        public Task<System.Text.Json.Nodes.JsonObject> GetOverlayAsync(string id, CancellationToken ct) => Fail
            ? throw new Cuebot.Donations.StreamElements.StreamElementsApiException("StreamElements API error 500", 500)
            : Task.FromResult(System.Text.Json.Nodes.JsonNode.Parse("""
                {"name":"Stream scene","settings":{"width":1280,"height":720},"widgets":[
                  {"type":"text","css":{"top":"10px","left":"10px","width":"200px","height":"40px"},"text":{"value":"Hi"}},
                  {"type":"se-widget-kappagen"}]}
                """)!.AsObject());
    }

    private sealed class TitleLookup : Cuebot.Core.Media.IMediaLookup
    {
        public Task<(string Title, string Author)?> FindAsync(string videoId, CancellationToken ct) =>
            Task.FromResult<(string, string)?>(("Video " + videoId, "Channel"));
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
        row.AddAlertBoxCommand.Execute(null);
        Pump();
        row = Assert.Single(vm.Overlays);
        Assert.Equal(["pink chat widget", "Chat box", "Alert box"], row.Layers.Select(l => l.Name));
        Assert.Contains("alert box", vm.Status);
        row.AddBuiltInCommand.Execute("goal");
        Pump();
        row = Assert.Single(vm.Overlays);
        Assert.Equal("Goal", row.Layers.Last().Name);
        Assert.Equal("Added a goal. Use Edit… to choose what it shows and how it looks.", vm.Status);
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
        Assert.Equal(["Chat box", "Alert box", "Goal"], Store.Get(row.Id)!.Layers.Select(l => l.Name));
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
