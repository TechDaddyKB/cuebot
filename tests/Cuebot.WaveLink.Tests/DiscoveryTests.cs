using System.Text.Json;
using Cuebot.Core;
using Cuebot.WaveLink.Tests.Support;

namespace Cuebot.WaveLink.Tests;

public sealed class DiscoveryUnitTests
{
    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Fact]
    public void Classify_tells_the_api_generations_apart_by_their_application_info()
    {
        var legacy = Json("""{"appID":"egwl","appName":"Elgato Wave Link","interfaceRevision":7}""");
        var v3 = Json("""{"appID":"EWL","name":"Elgato Wave Link","interfaceRevision":1}""");
        Assert.Equal(WaveLinkApi.Legacy, WaveLinkDiscovery.Classify(legacy, 1884, WaveLinkApiPreference.Auto));
        Assert.Equal(WaveLinkApi.V3, WaveLinkDiscovery.Classify(v3, 1824, WaveLinkApiPreference.Auto));
        // Only an appID: the port range decides.
        Assert.Equal(WaveLinkApi.V3, WaveLinkDiscovery.Classify(Json("""{"appID":"EWL"}"""), 1890, WaveLinkApiPreference.Auto));
        Assert.Equal(WaveLinkApi.Legacy, WaveLinkDiscovery.Classify(Json("""{"appID":"EWL"}"""), 1830, WaveLinkApiPreference.Auto));
        // A forced API wins; something that is not Wave Link is refused.
        Assert.Equal(WaveLinkApi.Legacy, WaveLinkDiscovery.Classify(v3, 1884, WaveLinkApiPreference.Legacy));
        Assert.Null(WaveLinkDiscovery.Classify(Json("""{"version":"5.1"}"""), 1884, WaveLinkApiPreference.Auto));
        Assert.Null(WaveLinkDiscovery.Classify(default, 1884, WaveLinkApiPreference.Auto));
    }

    [Fact]
    public void Candidates_put_ws_info_first_then_the_v3_range_then_the_legacy_range()
    {
        var auto = WaveLinkDiscovery.Candidates(new WaveLinkSettings(), 1887);
        Assert.Equal(1887, auto[0].Port);
        Assert.Equal(WaveLinkApi.V3, auto[0].Hint);
        Assert.Equal([1887, 1884, 1885, 1886, 1888, 1889, 1890, 1891, 1892, 1893, 1824, 1825, 1826, 1827, 1828, 1829, 1830, 1831, 1832, 1833, 1834],
            auto.Select(c => c.Port));
        Assert.All(auto.Skip(10), c => Assert.Equal(WaveLinkApi.Legacy, c.Hint));

        Assert.Equal(Enumerable.Range(1824, 11), WaveLinkDiscovery.Candidates(new WaveLinkSettings { Api = WaveLinkApiPreference.Legacy }, 1887).Select(c => c.Port));
        Assert.Equal(Enumerable.Range(1884, 10), WaveLinkDiscovery.Candidates(new WaveLinkSettings { Api = WaveLinkApiPreference.V3 }, null).Select(c => c.Port));
        Assert.Equal([1999], WaveLinkDiscovery.Candidates(new WaveLinkSettings { Port = 1999 }, 1887).Select(c => c.Port));
    }

    [Fact]
    public void Ws_info_port_is_read_from_the_first_valid_file()
    {
        var dir = Directory.CreateTempSubdirectory("cuebot-wsinfo-").FullName;
        try
        {
            var broken = Path.Combine(dir, "broken.json");
            File.WriteAllText(broken, "{ not json");
            var good = Path.Combine(dir, "ws-info.json");
            File.WriteAllText(good, """{ "port": 1886 }""");
            Assert.Equal(1886, WaveLinkDiscovery.ReadWsInfoPort([Path.Combine(dir, "missing.json"), broken, good]));
            Assert.Null(WaveLinkDiscovery.ReadWsInfoPort([broken]));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Default_platform_check_matches_the_os()
    {
        var reason = WaveLinkService.DefaultUnsupportedReason();
        if (OperatingSystem.IsLinux()) Assert.Equal("Wave Link is not available on Linux", reason);
        else if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()) Assert.Null(reason);
    }
}

public sealed class ConnectionTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private WaveLinkService _wl = null!;

    public Task InitializeAsync()
    {
        _wl = _env.Service();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _wl.DisposeAsync();
        _env.Dispose();
    }

    [Fact]
    public async Task Prefers_wave_link_3_from_ws_info_over_a_legacy_instance()
    {
        _env.WriteWsInfo(1887);
        _env.Network.Listen(1887, FakeWaveLink.V3);
        _env.Network.Listen(1824, s => FakeWaveLink.Legacy(s));
        await _wl.ConnectAsync();
        await Wait.UntilAsync(() => _wl.IsConnected, "connected");

        Assert.Equal(WaveLinkApi.V3, _wl.Api);
        Assert.Equal(1887, _wl.Port);
        Assert.Equal(1, _wl.InterfaceRevision);
        Assert.Equal("Elgato Wave Link", _wl.AppName);
        Assert.Equal(ConnectionState.Connected, _wl.State);
        Assert.Contains("Wave Link 3 on port 1887", _wl.StatusMessage);
        Assert.Contains("3 channels, 2 mixes", _wl.StatusMessage);
        // Every candidate was probed at once; the legacy probe was closed again.
        Assert.Contains(1834, _env.Network.Attempts);
        var legacy = await _env.Network.AppAsync(1824);
        await Wait.UntilAsync(() => legacy.Socket.Disposed, "legacy probe closed");

        var connected = await _env.WaitForEventAsync(WaveLinkEvents.Connected);
        Assert.Equal("3", connected.Args["waveLinkApi"]);
        Assert.Equal(1887, connected.Args["waveLinkPort"]);
    }

    [Fact]
    public async Task Finds_a_legacy_wave_link_on_the_first_free_port_it_took()
    {
        _env.Network.Listen(1826, s => FakeWaveLink.Legacy(s, revision: 6));
        await _wl.ConnectAsync();
        await Wait.UntilAsync(() => _wl.IsConnected, "connected");

        Assert.Equal(WaveLinkApi.Legacy, _wl.Api);
        Assert.Equal(1826, _wl.Port);
        Assert.Equal(6, _wl.InterfaceRevision);
        Assert.Contains("Wave Link 1.x/2.x on port 1826", _wl.StatusMessage);
        Assert.Contains("2 inputs, 2 mixes", _wl.StatusMessage);
        Assert.Equal("legacy", (await _env.WaitForEventAsync(WaveLinkEvents.Connected)).Args["waveLinkApi"]);
    }

    [Fact]
    public async Task A_fixed_port_and_a_forced_api_are_respected()
    {
        _env.Settings.Value.Port = 1999;
        _env.Settings.Value.Api = WaveLinkApiPreference.Legacy;
        _env.Network.Listen(1884, FakeWaveLink.V3);
        _env.Network.Listen(1999, s => FakeWaveLink.Legacy(s));
        await _wl.ConnectAsync();
        await Wait.UntilAsync(() => _wl.IsConnected, "connected");
        Assert.Equal(1999, _wl.Port);
        Assert.Equal(WaveLinkApi.Legacy, _wl.Api);
        Assert.DoesNotContain(1884, _env.Network.Attempts);
    }

    [Fact]
    public async Task Ignores_a_websocket_that_is_not_wave_link()
    {
        _env.Network.Listen(1884, s => new FakeWaveLink(s, new() { ["getApplicationInfo"] = _ => new System.Text.Json.Nodes.JsonObject { ["hello"] = "world" } }));
        _env.Network.Listen(1885, s => new FakeWaveLink(s, new()));   // answers "Method not found"
        _env.Network.Listen(1830, s => FakeWaveLink.Legacy(s));
        await _wl.ConnectAsync();
        await Wait.UntilAsync(() => _wl.IsConnected, "connected");
        Assert.Equal(1830, _wl.Port);
    }

    [Fact]
    public async Task On_linux_it_reports_not_available_and_never_connects()
    {
        _env.UnsupportedReason = "Wave Link is not available on Linux";
        _env.Settings.Value.AutoConnect = true;
        _env.Network.Listen(1884, FakeWaveLink.V3);
        await _wl.StartAsync();
        Assert.Equal("Wave Link is not available on Linux", _wl.StatusMessage);
        Assert.Equal(ConnectionState.Disconnected, _wl.State);
        await _wl.ConnectAsync();
        Assert.False(_wl.IsRunning);
        Assert.False(_wl.IsAvailable);
        Assert.Empty(_env.Network.Attempts);
        Assert.Equal("Wave Link is not available on Linux", _wl.StatusMessage);
    }

    [Fact]
    public async Task Start_does_nothing_without_auto_connect()
    {
        _env.Network.Listen(1884, FakeWaveLink.V3);
        await _wl.StartAsync();
        Assert.False(_wl.IsRunning);
        Assert.Empty(_env.Network.Attempts);
    }

    [Fact]
    public async Task Keeps_looking_with_backoff_until_wave_link_starts_then_reconnects_after_it_quits()
    {
        await _wl.ConnectAsync();
        await Wait.UntilAsync(() => _wl.StatusMessage.Contains("Wave Link is not running") && _wl.StatusMessage.Contains("retrying"), "not running");
        Assert.Equal(ConnectionState.Connecting, _wl.State);
        Assert.Contains("1884-1893 (Wave Link 3) and 1824-1834 (1.x/2.x)", _wl.StatusMessage);

        _env.Network.Listen(1884, FakeWaveLink.V3);
        await Wait.UntilAsync(() => _wl.IsConnected, "connected after start");

        _env.Network.Stop(1884);
        var disconnected = await _env.WaitForEventAsync(WaveLinkEvents.Disconnected);
        Assert.Contains("closed by Wave Link", (string)disconnected.Args["reason"]!);
        Assert.Equal(WaveLinkApi.None, _wl.Api);
        Assert.Empty(_wl.Snapshot.Channels);

        _env.Network.Listen(1885, FakeWaveLink.V3);
        await Wait.UntilAsync(() => _wl.IsConnected && _wl.Port == 1885, "reconnected");
        Assert.Equal(2, _env.Events(WaveLinkEvents.Connected).Length);
    }

    [Fact]
    public async Task Without_auto_reconnect_it_stops_after_one_search()
    {
        _env.Settings.Value.AutoReconnect = false;
        await _wl.ConnectAsync();
        await Wait.UntilAsync(() => !_wl.IsRunning, "stopped");
        Assert.Equal(ConnectionState.Disconnected, _wl.State);
        Assert.StartsWith("Wave Link is not running", _wl.StatusMessage);
    }

    [Fact]
    public async Task Disconnect_publishes_disconnected_and_commands_then_fail()
    {
        _env.Network.Listen(1884, FakeWaveLink.V3);
        await _wl.ConnectAsync();
        await Wait.UntilAsync(() => _wl.IsConnected, "connected");
        await _wl.DisconnectAsync();
        Assert.Equal("Disconnected", _wl.StatusMessage);
        Assert.Single(_env.Events(WaveLinkEvents.Disconnected));
        await Assert.ThrowsAsync<WaveLinkNotConnectedException>(() => _wl.SetMixMuteAsync("Stream Mix", SwitchMode.On));
    }
}
