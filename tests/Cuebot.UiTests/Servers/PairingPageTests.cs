using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels.Servers;
using Cuebot.App.Views.Servers;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Server;
using Cuebot.UiTests.Speech;

namespace Cuebot.UiTests.Servers;

/// <summary>"Pair a phone" on the Servers page: hidden until asked, then the QR code (with the tokens) or what to change first.</summary>
public sealed class PairingPageTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly ServerManager _servers;

    public PairingPageTests()
    {
        var bus = new EventBus(_log);
        var profile = new ProfileService(null, _log);
        var globals = new GlobalVariables(null, _log);
        var registry = new Registry([], []);
        _servers = new ServerManager(null, new ServerServices
        {
            Engine = new ActionEngine(bus, profile, registry, globals, _log), Profile = profile, Globals = globals, Bus = bus, Log = _log, Registry = registry,
        });
    }

    public void Dispose() => _log.Dispose();

    [AvaloniaFact]
    public void Code_is_hidden_until_asked_and_lists_what_to_change()
    {
        var vm = new ServersViewModel(_servers);
        var window = new Window { Content = new ServersView { DataContext = vm }, Width = 1000, Height = 3600 };
        window.Show();
        UiPump.Pump();

        Assert.Null(vm.PairingQr);
        Assert.Equal("", _servers.Settings.Http.DeckToken);

        vm.ShowPairing = true;
        UiPump.Pump();

        Assert.Contains("Allow LAN", vm.PairingProblems);
        Assert.Contains("REST API", vm.PairingProblems);
        var shown = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();
        Assert.Contains(shown, t => t.Contains("Allow LAN", StringComparison.Ordinal));
        if (vm.PairingHosts.Count > 0)
        {
            Assert.NotNull(vm.PairingQr);
            Assert.StartsWith("cb_", _servers.Settings.Http.DeckToken);   // the code carries a deck token
        }
        Assert.DoesNotContain(shown, t => _servers.Settings.Api.Token.Length > 0 && t.Contains(_servers.Settings.Api.Token, StringComparison.Ordinal));

        vm.ShowPairing = false;
        Assert.Null(vm.PairingQr);
        window.Close();
    }

    [AvaloniaFact]
    public void New_deck_token_replaces_the_old_one()
    {
        var vm = new ServersViewModel(_servers);
        var before = _servers.EnsureDeckToken();

        vm.RegenerateDeckTokenCommand.Execute(null);

        Assert.NotEqual(before, _servers.Settings.Http.DeckToken);
        Assert.Contains("Pair your phone again", vm.Message);
    }

    [Fact]
    public void Pairing_code_is_a_png()
    {
        var png = PairingCode.Png("cuebot://pair?v=1&host=192.168.1.20&token=cb_x");
        Assert.Equal([0x89, (byte)'P', (byte)'N', (byte)'G'], png[..4]);
    }
}
