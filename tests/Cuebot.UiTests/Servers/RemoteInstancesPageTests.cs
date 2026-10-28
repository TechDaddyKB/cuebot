using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels.Servers;
using Cuebot.App.Views.Servers;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Remote;
using Cuebot.Server;
using Cuebot.UiTests.Speech;

namespace Cuebot.UiTests.Servers;

/// <summary>
/// The "Remote instances" card on the Servers page: add an instance, link it to a second in-process Cuebot over loopback,
/// see the status on both sides, remove it; the token stays masked (screenshots tests/Cuebot.UiTests/screenshots/remote-*.png).
/// </summary>
public sealed class RemoteInstancesPageTests : IAsyncDisposable
{
    private const string OtherToken = "cb_ui-remote-token-0987654321";
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly ProfileService _profile;
    private readonly GlobalVariables _globals;
    private readonly ActionEngine _engine;
    private readonly RemoteSettings _settings = new() { InstanceName = "Desk" };
    private readonly RemoteHub _hub;
    private readonly RemoteHub _otherHub;
    private readonly ServerManager _servers;
    private readonly ServerManager _otherServers;

    public RemoteInstancesPageTests()
    {
        _bus = new EventBus(_log);
        _profile = new ProfileService(null, _log);
        _globals = new GlobalVariables(null, _log);
        var registry = new Registry([], []);
        _engine = new ActionEngine(_bus, _profile, registry, _globals, _log);
        _hub = new RemoteHub(_settings, null, _bus, _log, apiEnabled: () => false);
        _otherHub = new RemoteHub(new RemoteSettings { InstanceName = "Studio PC" }, null, new EventBus(_log), _log);
        _servers = new ServerManager(null, Services(registry, _hub));
        _otherServers = new ServerManager(null, Services(registry, _otherHub));
        _otherServers.Settings.Api.Enabled = true;
        _otherServers.Settings.Api.Port = 0;
        _otherServers.Settings.Api.Token = OtherToken;
    }

    private ServerServices Services(Registry registry, RemoteHub hub) => new()
    {
        Engine = _engine, Profile = _profile, Globals = _globals, Bus = _bus, Log = _log, Registry = registry, WebSocketRoutes = [hub],
    };

    private static Window Show(ServersViewModel vm)
    {
        var window = new Window
        {
            Content = new ServersView { DataContext = vm },
            Width = 1000,
            Height = 3600,
            Background = Application.Current!.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
        };
        window.Show();
        UiPump.Pump();
        return window;
    }

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 300 && !condition(); i++)
        {
            await Task.Delay(20);
            UiPump.Pump();
        }
        Assert.True(condition());
    }

    [AvaloniaFact]
    public async Task Card_adds_links_shows_status_and_removes_instances_in_both_themes()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        try
        {
            TestApp.BindingErrors.Clear();
            await _otherServers.StartAsync();
            var remote = new RemoteInstancesViewModel(_hub);
            var vm = new ServersViewModel(_servers, remote);
            var window = Show(vm);
            Assert.Contains("cannot connect to this one", remote.AcceptHint);
            Assert.True(remote.HasNoIncoming);
            Assert.Equal("Desk", remote.SelfName);

            remote.AddInstanceCommand.Execute(null);
            var item = Assert.Single(remote.Instances);
            Assert.Equal("Instance 1", item.Name);
            item.Name = "Studio";
            item.Port = _otherServers.Api.Port;
            item.Token = OtherToken;
            Assert.Equal("Studio", Assert.Single(_settings.Instances).Name);

            await item.ConnectCommand.ExecuteAsync(null);
            await Until(() => item.IsConnected);
            Assert.Contains("Studio PC", item.StatusText);
            await Until(() => _otherHub.Instances.Count == 1);
            Assert.Equal("Desk", _otherHub.Instances[0].Name);

            UiPump.Pump();
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToArray();
            Assert.Contains(texts, t => t == "Remote instances");
            Assert.DoesNotContain(texts, t => t.Contains(OtherToken, StringComparison.Ordinal));
            var tokenBox = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Text == OtherToken);
            Assert.Equal('•', tokenBox.PasswordChar);

            foreach (var (theme, name) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                app.RequestedThemeVariant = theme;
                UiPump.Pump();
                UiPump.Save(window, $"remote-{name}");
            }

            // Editing while connected asks for a reconnect.
            item.Host = "localhost";
            Assert.True(item.HasPendingChanges);

            await item.RemoveCommand.ExecuteAsync(null);
            Assert.Empty(remote.Instances);
            Assert.Empty(_settings.Instances);
            await Until(() => _otherHub.Instances.Count == 0);
            Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }

    [AvaloniaFact]
    public void Servers_page_hides_the_card_without_a_remote_hub()
    {
        TestApp.BindingErrors.Clear();
        var window = Show(new ServersViewModel(_servers));
        Assert.DoesNotContain(window.GetVisualDescendants().OfType<RemoteInstancesView>(), v => v.IsEffectivelyVisible);
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
        window.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await _hub.DisposeAsync();
        await _otherHub.DisposeAsync();
        await _servers.DisposeAsync();
        await _otherServers.DisposeAsync();
        _engine.Dispose();
        _profile.Dispose();
        _globals.Dispose();
        _log.Dispose();
    }
}
