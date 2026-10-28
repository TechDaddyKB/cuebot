using System.Threading.Channels;
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
using Cuebot.StreamApps.Common;
using Cuebot.StreamApps.Lumia;
using Cuebot.StreamApps.Tits;
using Cuebot.UiTests.Speech;

namespace Cuebot.UiTests.StreamApps;

/// <summary>
/// The T.I.T.S. tab lists items/triggers from an in-memory T.I.T.S. and throws from a row; the Lumia tab masks the token
/// and refuses to connect without one. Both render without binding errors in both themes (screenshots tits-*.png, lumia-*.png).
/// </summary>
public sealed class TitsLumiaViewTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-ui-tits-lumia-").FullName;

    [AvaloniaFact]
    public async Task Tits_tab_lists_items_and_throws_in_both_themes()
    {
        var log = new AppLog();
        using var settings = new JsonFile<StreamAppsSettings>(Path.Combine(_dir, "streamapps.json"), log);
        var fake = new FakeTitsTransport.Server();
        await using var tits = new TitsService(settings, new EventBus(log), log, fake.Create,
            new StreamAppsTimings(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(3), TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(200)));
        var vm = new TitsViewModel(tits);
        var window = Show(new TitsView { DataContext = vm });
        await tits.ConnectAsync();
        await PumpUntil(() => vm.Items.Count == 2 && vm.Triggers.Count == 1 && tits.EventsConnected, "lists");
        Assert.True(vm.IsConnected);
        Assert.Contains("events (/events) connected", vm.Sockets);

        vm.ThrowAmount = 3;
        await vm.Items[0].RunCommand.ExecuteAsync(null);
        Assert.Equal("Threw 3 × apple.", vm.Info);
        Assert.Contains(fake.Sent, s => s.Contains("TITSThrowItemsRequest") && s.Contains("\"amountOfThrows\":3"));

        Screenshots(window, "tits");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Lumia_tab_masks_the_token_and_needs_one_to_connect()
    {
        var log = new AppLog();
        using var settings = new JsonFile<StreamAppsSettings>(Path.Combine(_dir, "streamapps-lumia.json"), log);
        await using var lumia = new LumiaService(settings, new EventBus(log), log, new HttpClient());
        var vm = new LumiaViewModel(lumia);
        var view = new LumiaView { DataContext = vm };
        var window = Show(view);

        Assert.True(vm.HasNoToken);
        await vm.ConnectCommand.ExecuteAsync(null);
        Assert.Contains("token", vm.Error);
        Assert.False(lumia.IsRunning);

        var token = view.FindControl<TextBox>("LumiaToken")!;
        Assert.Equal('•', token.PasswordChar);
        vm.Token = "  abc123  ";
        Assert.Equal("abc123", settings.Value.Lumia.Token);
        Assert.False(vm.HasNoToken);
        Assert.Equal("—", vm.States);

        Screenshots(window, "lumia");
        window.Close();
    }

    private static Window Show(Control view)
    {
        TestApp.BindingErrors.Clear();
        var window = new Window
        {
            Content = view,
            Width = 1000,
            Height = 1100,
            Background = Application.Current!.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
        };
        window.Show();
        UiPump.Pump();
        return window;
    }

    private static void Screenshots(Window window, string name)
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        try
        {
            foreach (var (theme, suffix) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                app.RequestedThemeVariant = theme;
                UiPump.Pump();
                UiPump.Save(window, $"{name}-{suffix}");
            }
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
    }

    private static async Task PumpUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}");
            UiPump.Pump();
            await Task.Delay(20);
        }
        UiPump.Pump();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>A T.I.T.S. API in memory: /websocket answers lists and throws, /events stays open.</summary>
    private sealed class FakeTitsTransport(FakeTitsTransport.Server server) : IWebSocketTransport
    {
        private readonly Channel<string?> _incoming = Channel.CreateUnbounded<string?>();
        private Uri? _url;

        public sealed class Server
        {
            public System.Collections.Concurrent.ConcurrentQueue<string> Sent { get; } = new();

            public IWebSocketTransport Create() => new FakeTitsTransport(this);
        }

        public string? CloseReason => null;

        public Task ConnectAsync(Uri url, CancellationToken ct)
        {
            _url = url;
            return Task.CompletedTask;
        }

        public Task SendAsync(string text, CancellationToken ct)
        {
            server.Sent.Enqueue(text);
            var request = System.Text.Json.JsonDocument.Parse(text).RootElement;
            var id = request.GetProperty("requestID").GetString();
            var type = request.GetProperty("messageType").GetString()!;
            var data = type switch
            {
                "TITSItemListRequest" => """{"items":[{"ID":"60c6","name":"apple"},{"ID":"16d7","name":"banana"}]}""",
                "TITSTriggerListRequest" => """{"triggers":[{"ID":"6709","name":"New Follow"}]}""",
                "TITSThrowItemsRequest" => $$"""{"numberOfThrownItems":{{request.GetProperty("data").GetProperty("amountOfThrows").GetInt32()}}}""",
                _ => "{}",
            };
            _incoming.Writer.TryWrite($$"""{"apiName":"TITSPublicApi","apiVersion":"1.0","requestID":"{{id}}","messageType":"{{type.Replace("Request", "Response")}}","data":{{data}}}""");
            return Task.CompletedTask;
        }

        public async Task<string?> ReceiveAsync(CancellationToken ct) => _url is null ? null : await _incoming.Reader.ReadAsync(ct);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
