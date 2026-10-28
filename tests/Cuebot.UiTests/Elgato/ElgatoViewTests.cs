using System.Collections.Concurrent;
using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Cuebot.App.ViewModels.Elgato;
using Cuebot.App.Views.Elgato;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Elgato;
using Cuebot.Elgato.CameraHub;
using Cuebot.Elgato.Lights;
using Cuebot.UiTests.Speech;

namespace Cuebot.UiTests.Elgato;

/// <summary>
/// The Elgato tab renders a light (served by an in-memory Key Light API), drives it from the power switch, and shows the
/// Camera Hub and Studio cards without binding errors in both themes (screenshots tests/Cuebot.UiTests/screenshots/elgato-*.png).
/// </summary>
public sealed class ElgatoViewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-ui-elgato-").FullName;

    [AvaloniaFact]
    public async Task Shows_lights_and_drives_power_in_both_themes()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        var log = new AppLog();
        using var settings = new JsonFile<ElgatoSettings>(Path.Combine(_dir, "elgato.json"), log);
        var handler = new FakeKeyLight();
        var bus = new EventBus(log);
        await using var lights = new ElgatoLightsService(settings, bus, log, new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) });
        await using var hub = new CameraHubService(settings, bus, log, backend: null, useDefaultBackend: false);
        try
        {
            TestApp.BindingErrors.Clear();
            await lights.AddLightAsync("192.168.1.40");
            var vm = new ElgatoViewModel(lights, hub);
            var window = new Window
            {
                Content = new ElgatoView { DataContext = vm },
                Width = 1000,
                Height = 1400,
                Background = app.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
            };
            window.Show();
            UiPump.Pump();

            var row = Assert.Single(vm.Lights.Rows);
            Assert.Equal("Desk Key Light", row.Name);
            Assert.True(row.IsOnline);
            Assert.False(row.IsOn);
            Assert.Equal(30, row.Brightness);
            Assert.Equal(4700, row.Temperature);
            Assert.False(vm.CameraHub.IsAvailable);

            row.IsOn = true;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!handler.Bodies.Any(b => b.Contains("\"on\":1")))
            {
                if (DateTime.UtcNow > deadline) throw new TimeoutException("power switch did not reach the light");
                UiPump.Pump();
                await Task.Delay(20);
            }

            foreach (var (theme, name) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                app.RequestedThemeVariant = theme;
                UiPump.Pump();
                UiPump.Save(window, $"elgato-{name}");
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

    /// <summary>Answers like a Key Light at any address.</summary>
    private sealed class FakeKeyLight : HttpMessageHandler
    {
        private bool _on;

        public ConcurrentQueue<string> Bodies { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            if (body.Length > 0) Bodies.Enqueue(body);
            if (request.Method == HttpMethod.Put && body.Contains("\"on\":1")) _on = true;
            var json = request.RequestUri!.AbsolutePath switch
            {
                "/elgato/lights" => $"{{\"numberOfLights\":1,\"lights\":[{{\"on\":{(_on ? 1 : 0)},\"brightness\":30,\"temperature\":213}}]}}",
                "/elgato/accessory-info" => """{"productName":"Elgato Key Light","serialNumber":"CW12K1A00009","displayName":"Desk Key Light","firmwareVersion":"1.0.3","features":["lights"]}""",
                "/elgato/lights/settings" => """{"powerOnBehavior":1,"powerOnBrightness":20,"powerOnTemperature":213}""",
                _ => null,
            };
            return json is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
