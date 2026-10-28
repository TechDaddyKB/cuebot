using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Cuebot.App.ViewModels.StreamApps;
using Cuebot.App.Views.StreamApps;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps;
using Cuebot.StreamApps.Mtion;
using Cuebot.StreamApps.Obsbot;
using Cuebot.UiTests.Speech;

namespace Cuebot.UiTests.StreamApps;

/// <summary>
/// The OBSBOT, mtion and StreamFog tabs render without binding errors in both themes; the mtion tab lists the open
/// clubhouse's triggers (from an in-memory mtion API) and fires one from its button
/// (screenshots tests/Cuebot.UiTests/screenshots/streamapps-*.png).
/// </summary>
public sealed class FogMtionObsbotViewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-ui-fog-mtion-obsbot-").FullName;

    [AvaloniaFact]
    public async Task Tabs_render_and_mtion_fires_a_trigger_in_both_themes()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        var log = new AppLog();
        using var settings = new JsonFile<StreamAppsSettings>(Path.Combine(_dir, "streamapps.json"), log);
        var bus = new EventBus(log);
        var api = new FakeMtionApi();
        await using var mtion = new MtionService(settings, bus, log, new HttpClient(api) { Timeout = TimeSpan.FromSeconds(5) });
        await using var obsbot = new ObsbotService(settings, bus, log);
        try
        {
            TestApp.BindingErrors.Clear();
            var mtionVm = new MtionViewModel(mtion);
            var obsbotVm = new ObsbotViewModel(obsbot);
            var tabs = new TabControl
            {
                Items =
                {
                    new TabItem { Header = "OBSBOT", Content = new ObsbotView { DataContext = obsbotVm } },
                    new TabItem { Header = "mtion", Content = new MtionView { DataContext = mtionVm } },
                    new TabItem { Header = "StreamFog", Content = new StreamFogView() },
                },
            };
            var window = new Window
            {
                Content = tabs, Width = 1000, Height = 1200,
                Background = app.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
            };
            window.Show();
            UiPump.Pump();

            Assert.Equal("127.0.0.1", obsbotVm.Host);
            Assert.Equal(16284, obsbotVm.Port);
            Assert.True(obsbotVm.IsUdp);
            Assert.False(obsbotVm.CanSend);
            Assert.True(obsbotVm.HasNoDevices);
            obsbotVm.TransportIndex = 1;
            Assert.Equal(ObsbotTransport.Tcp, settings.Value.Obsbot.Transport);
            Assert.False(obsbotVm.IsUdp);
            obsbotVm.TransportIndex = 0;

            tabs.SelectedIndex = 1;
            UiPump.Pump();
            Assert.True(mtionVm.HasNoTriggers);
            await mtionVm.CheckCommand.ExecuteAsync(null);
            UiPump.Pump();
            Assert.Equal("Kongo's Clubhouse", mtionVm.Clubhouse);
            var row = Assert.Single(mtionVm.Triggers);
            Assert.Equal("Confetti", row.Name);
            Assert.Equal("Amount (number)", row.Details);
            await row.FireCommand.ExecuteAsync(null);
            Assert.Equal("Fired \"Confetti\".", mtionVm.Info);
            Assert.Contains(api.Requests, r => r == "PATCH external-trigger/fire-trigger/abc123");

            foreach (var (theme, name) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                app.RequestedThemeVariant = theme;
                foreach (var (index, tab) in new[] { (0, "obsbot"), (1, "mtion"), (2, "streamfog") })
                {
                    tabs.SelectedIndex = index;
                    UiPump.Pump();
                    UiPump.Save(window, $"streamapps-{tab}-{name}");
                }
            }
            Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>mtion's developer API with one clubhouse holding one external trigger.</summary>
    private sealed class FakeMtionApi : HttpMessageHandler
    {
        private const string Trigger = """{"id":"abc123","name":"Confetti","output_parameters":[{"parameter_index":0,"name":"Amount","data_type":"number"}]}""";

        public ConcurrentQueue<string> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var path = request.RequestUri!.AbsolutePath.TrimStart('/');
            Requests.Enqueue($"{request.Method.Method} {path}");
            var json = path switch
            {
                "active-clubhouse" => $$"""{"id":"MCLB-1","name":"Kongo's Clubhouse","description":"","external_trigger_datas":[{{Trigger}}]}""",
                "external-trigger/triggers" => $"[{Trigger}]",
                "external-trigger/fire-trigger/abc123" => Trigger,
                _ => null,
            };
            return Task.FromResult(json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
