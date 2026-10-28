using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels.StreamDeck;
using Cuebot.App.Views.StreamDeck;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Server;
using Cuebot.Server.Api;
using Cuebot.StreamDeck;
using Cuebot.UiTests.Speech;

namespace Cuebot.UiTests.StreamDeck;

/// <summary>
/// The Stream Deck tab: setup steps (REST API, pairing details, plugin file) and the connected Stream Deck apps with their
/// devices and keys, fed by an in-memory plugin connection (screenshots tests/Cuebot.UiTests/screenshots/streamdeck-*.png).
/// </summary>
public sealed class StreamDeckPageTests : IAsyncDisposable
{
    private const string Token = "cb_ui-test-token-1234567890";
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly ProfileService _profile;
    private readonly GlobalVariables _globals;
    private readonly ActionEngine _engine;
    private readonly ServerManager _servers;
    private readonly StreamDeckHub _hub;
    private readonly string _pluginDir = Directory.CreateTempSubdirectory("cuebot-ui-streamdeck-").FullName;

    public StreamDeckPageTests()
    {
        _bus = new EventBus(_log);
        _profile = new ProfileService(null, _log);
        _globals = new GlobalVariables(null, _log);
        var registry = new Registry([], []);
        _engine = new ActionEngine(_bus, _profile, registry, _globals, _log);
        _engine.Start();
        ApiOperations? ops = null;
        var services = new ServerServices { Engine = _engine, Profile = _profile, Globals = _globals, Bus = _bus, Log = _log, Registry = registry };
        _servers = new ServerManager(null, services);
        _servers.Settings.Api.Port = 0;
        _hub = new StreamDeckHub(_bus, _log, () => ops ??= new ApiOperations(services), () => _servers.Settings.Api.Enabled, _profile, _globals);
        File.WriteAllText(Path.Combine(_pluginDir, StreamDeckViewModel.PluginFileName), "zip");
    }

    private sealed class Peer : IStreamDeckPeer
    {
        public string Id => "ui-peer";
        public string Remote => "127.0.0.1:52000";
        public Task<bool> SendAsync(string text, CancellationToken ct = default) => Task.FromResult(true);
    }

    private static Window Show(StreamDeckViewModel vm)
    {
        var window = new Window
        {
            Content = new StreamDeckView { DataContext = vm },
            Width = 1000,
            Height = 1500,
            Background = Application.Current!.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
        };
        window.Show();
        UiPump.Pump();
        return window;
    }

    [AvaloniaFact]
    public async Task Page_walks_through_setup_and_lists_connected_apps_in_both_themes()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        try
        {
            TestApp.BindingErrors.Clear();
            var vm = new StreamDeckViewModel(_hub, _servers, [_pluginDir]);
            var window = Show(vm);
            Assert.False(vm.ApiRunning);
            Assert.True(vm.HasNoInstances);
            Assert.True(vm.HasPluginFile);
            Assert.Contains("REST API is off", vm.StatusMessage);
            Assert.Equal("(created when the REST API is turned on)", vm.TokenDisplay);
            UiPump.Save(window, "streamdeck-empty");

            // Step 1: turn the REST API on from the page.
            await vm.EnableApiCommand.ExecuteAsync(null);
            Assert.True(vm.ApiRunning, vm.Message);
            Assert.True(_servers.Settings.Api.Enabled);
            Assert.True(vm.HasToken);
            Assert.DoesNotContain(_servers.Settings.Api.Token, vm.TokenDisplay);
            Assert.Equal(_servers.Settings.Api.Token, vm.CopyText("token"));
            Assert.Equal("127.0.0.1", vm.CopyText("host"));
            Assert.Equal(_servers.Api.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), vm.CopyText("port"));
            vm.ShowToken = true;
            Assert.Equal(_servers.Settings.Api.Token, vm.TokenDisplay);
            vm.ShowToken = false;

            // A plugin connects with two devices and two keys.
            await _hub.OpenAsync(new Peer());
            await _hub.HandleAsync("ui-peer", """
                {"type":"hello","protocol":1,"plugin":{"uuid":"io.github.cuebot.streamdeck","version":"0.1.0"},
                 "app":{"version":"7.1.0","platform":"windows","language":"en"},
                 "devices":[{"id":"D1","name":"Desk XL","type":2,"size":{"columns":8,"rows":4}},
                            {"id":"D2","name":"Plus","type":7,"size":{"columns":4,"rows":2}}],
                 "buttons":[{"context":"c1","buttonId":"go-live","kind":"run","controller":"Keypad","device":"D1","row":0,"column":1,"state":0},
                            {"context":"c2","buttonId":"volume","kind":"trigger","controller":"Encoder","device":"D2","row":0,"column":2,"state":0}]}
                """);
            UiPump.Pump();
            Assert.True(vm.IsConnected);
            var instance = Assert.Single(vm.Instances);
            Assert.Equal("Stream Deck 7.1.0 on Windows", instance.Title);
            Assert.Equal(2, instance.Devices.Count);
            Assert.Contains("Desk XL · Stream Deck XL (8×4)", instance.Devices);
            Assert.Equal(["go-live", "volume"], instance.Buttons.Select(b => b.ButtonId));
            Assert.Equal("Cuebot trigger (dial)", instance.Buttons[1].Kind);
            Assert.Equal("Plus · row 1, column 3", instance.Buttons[1].Where);

            UiPump.Pump();
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
            Assert.Contains(texts, t => t == "go-live");
            Assert.DoesNotContain(texts, t => t.Contains(_servers.Settings.Api.Token, StringComparison.Ordinal));

            foreach (var (theme, name) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                app.RequestedThemeVariant = theme;
                UiPump.Pump();
                UiPump.Save(window, $"streamdeck-{name}");
            }

            _hub.Close("ui-peer");
            UiPump.Pump();
            Assert.True(vm.HasNoInstances);
            Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _servers.DisposeAsync();
        _engine.Dispose();
        _profile.Dispose();
        _globals.Dispose();
        _log.Dispose();
        try { Directory.Delete(_pluginDir, recursive: true); }
        catch (IOException) { }
    }
}
