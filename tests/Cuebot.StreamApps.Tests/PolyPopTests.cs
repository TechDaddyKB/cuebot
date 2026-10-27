using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.StreamApps.Common;
using Cuebot.StreamApps.PolyPop;
using Cuebot.StreamApps.Tests.Support;

namespace Cuebot.StreamApps.Tests;

public sealed class PolyPopProtocolTests
{
    [Fact]
    public void Alert_message_matches_the_plugin_format()
    {
        var text = PolyPopProtocol.Alert("Follow", new Dictionary<string, string> { ["username"] = "Kris", ["amount"] = "5" });
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.Equal("ALERT", root.GetProperty("type").GetString());
        Assert.Equal("Follow", root.GetProperty("title").GetString());
        var vars = root.GetProperty("variables").GetProperty("text");
        Assert.Equal("Kris", vars.GetProperty("username").GetString());
        Assert.Equal("5", vars.GetProperty("amount").GetString());
    }

    [Fact]
    public void Parses_plugin_sends_with_the_instance_id_suffix()
    {
        var trigger = PolyPopProtocol.Parse("""{"type":"TRIGGER","name":"Raid","data":"big"}:4711""");
        Assert.Equal(("TRIGGER", "Raid", "big", "4711"), (trigger.Type, trigger.Name, trigger.Data, trigger.SourceId));

        var unload = PolyPopProtocol.Parse("disconnect:4711:unload");
        Assert.Equal(("disconnect", "4711", "unload"), (unload.Type, unload.SourceId, unload.Data));

        var plain = PolyPopProtocol.Parse("""{"type":"PING"}""");
        Assert.Equal(("PING", ""), (plain.Type, plain.SourceId));
    }

    [Fact]
    public void Variables_parse_from_lines_or_json()
    {
        Assert.Equal(new Dictionary<string, string> { ["user"] = "Kris", ["msg"] = "a=b" }, Steps.ParsePairs("user = Kris\n\nmsg=a=b\n"));
        Assert.Equal(new Dictionary<string, string> { ["n"] = "5", ["s"] = "x" }, Steps.ParsePairs("""{"n":5,"s":"x"}"""));
        Assert.Throws<ArgumentException>(() => Steps.ParsePairs("no equals"));
    }
}

public sealed class PolyPopServerTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private PolyPopService _polyPop = null!;

    public async Task InitializeAsync()
    {
        _env.Settings.Value.PolyPop.Port = 0;   // any free port
        _polyPop = new PolyPopService(_env.Settings, _env.Bus, _env.Log, _env.Server.Create, TestTimings.Fast);
        await _polyPop.ConnectAsync();
        await Wait.UntilAsync(() => _polyPop.ListeningPort > 0, "listening");
    }

    public async Task DisposeAsync()
    {
        await _polyPop.DisposeAsync();
        _env.Dispose();
    }

    private Uri Url(string path = "/") => new($"ws://127.0.0.1:{_polyPop.ListeningPort}{path}");

    /// <summary>Connects the way the PolyPop plugin does: a plain websocket client, no Origin header.</summary>
    private async Task<ClientWebSocket> PluginAsync(string path = "/", string? origin = null)
    {
        var client = new ClientWebSocket();
        if (origin is not null) client.Options.SetRequestHeader("Origin", origin);
        using var cts = new CancellationTokenSource(Wait.Timeout);
        await client.ConnectAsync(Url(path), cts.Token);
        return client;
    }

    private static async Task<string> ReceiveAsync(ClientWebSocket client)
    {
        var buffer = new byte[64 * 1024];
        using var cts = new CancellationTokenSource(Wait.Timeout);
        var result = await client.ReceiveAsync(buffer, cts.Token);
        return Encoding.UTF8.GetString(buffer, 0, result.Count);
    }

    [Fact]
    public async Task Waits_for_the_plugin_then_sends_alerts_and_receives_messages()
    {
        Assert.Equal(ConnectionState.Connecting, _polyPop.State);
        Assert.Contains("Waiting for PolyPop", _polyPop.StatusMessage);
        await Assert.ThrowsAsync<StreamAppNotConnectedException>(() => _polyPop.TriggerAlertAsync("Follow", new Dictionary<string, string>()));

        using var plugin = await PluginAsync();
        await _env.WaitForEventAsync(PolyPopService.ConnectedEvent);
        await Wait.UntilAsync(() => _polyPop.State == ConnectionState.Connected, "connected");
        Assert.True(_polyPop.IsConnected);

        Assert.Equal(1, await _polyPop.TriggerAlertAsync("Follow", new Dictionary<string, string> { ["username"] = "Kris" }));
        using (var alert = JsonDocument.Parse(await ReceiveAsync(plugin)))
        {
            Assert.Equal("Follow", alert.RootElement.GetProperty("title").GetString());
            Assert.Equal("Kris", alert.RootElement.GetProperty("variables").GetProperty("text").GetProperty("username").GetString());
        }

        await plugin.SendAsync(Encoding.UTF8.GetBytes("""{"type":"TRIGGER","name":"Raid","data":"big"}:99"""), WebSocketMessageType.Text, true, CancellationToken.None);
        var message = await _env.WaitForEventAsync(PolyPopService.MessageEvent);
        Assert.Equal("TRIGGER", message.Args["polypop.type"]);
        Assert.Equal("Raid", message.Args["polypop.name"]);
        Assert.Equal("big", message.Args["polypop.data"]);

        await plugin.CloseAsync(WebSocketCloseStatus.NormalClosure, "unload", CancellationToken.None);
        await _env.WaitForEventAsync(PolyPopService.DisconnectedEvent);
        await Wait.UntilAsync(() => _polyPop.State == ConnectionState.Connecting, "waiting again");
    }

    [Fact]
    public async Task Two_plugin_instances_both_get_alerts()
    {
        using var a = await PluginAsync();
        using var b = await PluginAsync();
        await Wait.UntilAsync(() => _polyPop.ClientCount == 2, "two clients");
        Assert.Single(_env.Events(PolyPopService.ConnectedEvent));
        Assert.Equal(2, await _polyPop.TriggerAlertAsync("Sub", new Dictionary<string, string>()));
        Assert.Contains("Sub", await ReceiveAsync(a));
        Assert.Contains("Sub", await ReceiveAsync(b));
    }

    [Fact]
    public async Task Wrong_path_and_foreign_web_pages_are_refused()
    {
        await Assert.ThrowsAnyAsync<WebSocketException>(() => PluginAsync("/other"));
        await Assert.ThrowsAnyAsync<WebSocketException>(() => PluginAsync("/", origin: "https://evil.example"));
        using var local = await PluginAsync("/", origin: "http://localhost:8080");
        await Wait.UntilAsync(() => _polyPop.IsConnected, "local page accepted");
    }

    [Fact]
    public async Task Stop_closes_the_listener()
    {
        var port = _polyPop.ListeningPort;
        await _polyPop.DisconnectAsync();
        Assert.Equal(ConnectionState.Disconnected, _polyPop.State);
        using var client = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<Exception>(() => client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None));
    }
}

public sealed class PolyPopClientModeTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private PolyPopService _polyPop = null!;

    public Task InitializeAsync()
    {
        _env.Settings.Value.PolyPop.Mode = PolyPopMode.Client;
        _polyPop = new PolyPopService(_env.Settings, _env.Bus, _env.Log, _env.Server.Create, TestTimings.Fast);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _polyPop.DisposeAsync();
        _env.Dispose();
    }

    [Fact]
    public async Task Connects_to_address_port_endpoint_sends_alerts_and_reconnects()
    {
        await _polyPop.ConnectAsync();
        var socket = await _env.Server.NextAsync();
        Assert.Equal(new Uri("ws://127.0.0.1:9652/"), socket.Url);
        await Wait.UntilAsync(() => _polyPop.State == ConnectionState.Connected, "connected");

        await _polyPop.TriggerAlertAsync("Cheer", new Dictionary<string, string> { ["bits"] = "100" });
        using (var sent = JsonDocument.Parse(await socket.NextSentAsync()))
            Assert.Equal("100", sent.RootElement.GetProperty("variables").GetProperty("text").GetProperty("bits").GetString());

        socket.Push("""{"type":"TRIGGER","name":"Hello"}:1""");
        await _env.WaitForEventAsync(PolyPopService.MessageEvent, e => (string?)e.Args["polypop.name"] == "Hello");

        socket.Close();
        await _env.WaitForEventAsync(PolyPopService.DisconnectedEvent);
        var again = await _env.Server.NextAsync();
        Assert.NotSame(socket, again);
        await Wait.UntilAsync(() => _polyPop.IsConnected, "reconnected");
    }
}
