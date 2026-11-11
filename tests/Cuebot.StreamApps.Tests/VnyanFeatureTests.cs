using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.StreamApps.Common;
using Cuebot.StreamApps.Obsbot;
using Cuebot.StreamApps.Tests.Support;
using Cuebot.StreamApps.Vnyan;

namespace Cuebot.StreamApps.Tests;

/// <summary>
/// VNyan against fakes: an HTTP handler that answers like VNyan's API ({"result":0}), an in-memory WebSocket receiver,
/// captured OSC packets (and one real UDP socket), and real WebSocket clients playing VNyan's "Websocket Message" node.
/// </summary>
public sealed class VnyanFeatureTests : IAsyncLifetime
{
    private readonly TestEnvironment _env = new();
    private readonly FakeServer _webSocket = new();
    private readonly List<(IPEndPoint Target, byte[] Packet)> _udp = [];
    private readonly Handler _http = new();
    private VnyanService _vnyan = null!;

    public Task InitializeAsync()
    {
        _vnyan = new VnyanService(_env.Settings, _env.Bus, _env.Log, new HttpClient(_http), _webSocket.Create, TestTimings.Fast,
            (target, packet, _) => { lock (_udp) _udp.Add((target, packet)); return Task.CompletedTask; });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _vnyan.DisposeAsync();
        _env.Dispose();
    }

    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public string Answer { get; set; } = """{ "result": 0 }""";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(Answer, Encoding.UTF8, "application/json") };
        }
    }

    private JsonElement LastPayload() => JsonDocument.Parse(_http.Bodies[^1]).RootElement.GetProperty("payload");

    private async Task<(bool Ok, string? Error)> RunAsync(string type, Dictionary<string, string> parameters, Dictionary<string, object?>? args = null)
    {
        await using var runner = new ActionRunner(VnyanSubActions.Create(_vnyan));
        var action = new ActionDefinition { Name = "vnyan test", SubActions = [new SubActionDefinition { Type = type, Parameters = parameters }] };
        var ok = await runner.Engine.RunAsync(action, args);
        return (ok, runner.LastError);
    }

    // ---- Numbers in the API Message payload ----

    [Fact]
    public void Payload_values_are_text_unless_numbers_are_asked_for()
    {
        var pairs = Steps.ParsePairNodes("bits=100\nratio=2.5\nneg=-3\ncode=007\ncomma=1,5\nbig=1e5\nuser=Ann");
        var text = VnyanProtocol.Payload(pairs, VnyanNumbers.Text);
        Assert.Equal("""{"bits":"100","ratio":"2.5","neg":"-3","code":"007","comma":"1,5","big":"1e5","user":"Ann"}""", text.ToJsonString());

        var numbers = VnyanProtocol.Payload(pairs, VnyanNumbers.Numbers);
        Assert.Equal("""{"bits":100,"ratio":2.5,"neg":-3,"code":"007","comma":"1,5","big":"1e5","user":"Ann"}""", numbers.ToJsonString());
    }

    [Fact]
    public void Json_values_keep_the_type_they_were_written_with()
    {
        var pairs = Steps.ParsePairNodes("""{"key1":"value1","key2":2,"f":2.50,"on":true,"s":"5"}""");
        Assert.Equal("""{"key1":"value1","key2":2,"f":2.50,"on":true,"s":"5"}""", VnyanProtocol.Payload(pairs, VnyanNumbers.Text).ToJsonString());
        Assert.Equal("""{"key1":"value1","key2":2,"f":2.50,"on":true,"s":5}""", VnyanProtocol.Payload(pairs, VnyanNumbers.Numbers).ToJsonString());
        // The text form other apps use is unchanged: non-strings become their JSON text.
        Assert.Equal(new Dictionary<string, string> { ["key1"] = "value1", ["key2"] = "2", ["f"] = "2.50", ["on"] = "true", ["s"] = "5" },
            Steps.ParsePairs("""{"key1":"value1","key2":2,"f":2.50,"on":true,"s":"5"}"""));
    }

    [Fact]
    public async Task Send_step_sends_numbers_as_numbers_when_asked()
    {
        var (ok, error) = await RunAsync("vnyan.send", new() { ["action"] = "Cheer", ["payload"] = "bits=%bits%\nuser=%user%", ["numbers"] = "As numbers" },
            new() { ["bits"] = 100, ["user"] = "Ann" });
        Assert.True(ok, error);
        var payload = LastPayload();
        Assert.Equal(JsonValueKind.Number, payload.GetProperty("bits").ValueKind);
        Assert.Equal(100, payload.GetProperty("bits").GetInt32());
        Assert.Equal("Ann", payload.GetProperty("user").GetString());

        (ok, error) = await RunAsync("vnyan.send", new() { ["action"] = "Cheer", ["payload"] = "bits=%bits%" }, new() { ["bits"] = 2.5 });
        Assert.True(ok, error);
        Assert.Equal("2.5", LastPayload().GetProperty("bits").GetString());
    }

    // ---- Variables are filled in after the values are read ----

    [Fact]
    public async Task Chat_text_with_quotes_and_line_breaks_cannot_break_the_values()
    {
        const string message = "He said \"hi\" \\o/\nbits=999";
        var (ok, error) = await RunAsync("vnyan.send", new() { ["action"] = "Say", ["payload"] = "msg=%message%\nuser=%user%" },
            new() { ["message"] = message, ["user"] = "Ann" });
        Assert.True(ok, error);
        var payload = LastPayload();
        Assert.Equal(message, payload.GetProperty("msg").GetString());
        Assert.False(payload.TryGetProperty("bits", out _));

        (ok, error) = await RunAsync("vnyan.send", new() { ["action"] = "Say", ["payload"] = """{"msg":"%message%","n":2}""" },
            new() { ["message"] = message });
        Assert.True(ok, error);
        payload = LastPayload();
        Assert.Equal(message, payload.GetProperty("msg").GetString());
        Assert.Equal(2, payload.GetProperty("n").GetInt32());
    }

    [Fact]
    public async Task Json_that_needs_its_variables_still_works_and_bad_json_says_why()
    {
        var (ok, error) = await RunAsync("vnyan.send", new() { ["action"] = "Cheer", ["payload"] = """{"bits":%bits%}""" }, new() { ["bits"] = 300 });
        Assert.True(ok, error);
        Assert.Equal(300, LastPayload().GetProperty("bits").GetInt32());

        var ex = Assert.Throws<ArgumentException>(() => Steps.ParsePairs("""{"a":}"""));
        Assert.Contains("not a valid JSON object", ex.Message);
        (ok, error) = await RunAsync("vnyan.send", new() { ["action"] = "Cheer", ["payload"] = """{"a":%missing""" });
        Assert.False(ok);
        Assert.Contains("JSON", error);
    }

    [Fact]
    public async Task Success_says_vnyan_got_it_and_a_remote_host_gets_a_hint()
    {
        await _vnyan.SendAsync("Wave", new JsonObject());
        Assert.Equal(ConnectionState.Connected, _vnyan.State);
        Assert.Contains("got 'Wave'", _vnyan.StatusMessage);

        var closed = new VnyanService(_env.Settings, _env.Bus, _env.Log, new HttpClient(new Refusing()));
        _vnyan.Settings.Host = "192.168.1.20";
        var ex = await Assert.ThrowsAsync<StreamAppRequestException>(() => closed.TestAsync());
        Assert.Contains("only name localhost", ex.Message);
        _vnyan.Settings.Host = "127.0.0.1";
        ex = await Assert.ThrowsAsync<StreamAppRequestException>(() => closed.TestAsync());
        Assert.DoesNotContain("only name localhost", ex.Message);
    }

    private sealed class Refusing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            throw new HttpRequestException("Connection refused");
    }

    // ---- WebSocket commands ----

    [Fact]
    public async Task WebSocket_commands_go_to_the_vnyan_path_with_the_value_and_reuse_the_connection()
    {
        var (ok, error) = await RunAsync("vnyan.sendWebSocket", new() { ["command"] = "Throw", ["value"] = "%count%" }, new() { ["count"] = 5 });
        Assert.True(ok, error);
        var socket = await _webSocket.NextAsync();
        Assert.Equal("ws://127.0.0.1:8000/vnyan", socket.Url!.ToString());
        Assert.Equal("Throw 5", await socket.NextSentAsync());

        await _vnyan.SendWebSocketAsync(VnyanProtocol.Command("Wave", ""));
        Assert.Equal("Wave", await socket.NextSentAsync());
        Assert.Single(_webSocket.Attempts);
        Assert.True(_vnyan.IsWebSocketOpen);
        Assert.Contains("WebSocket open", _vnyan.StatusMessage);

        // VNyan restarted: the next command opens a new connection.
        socket.Close();
        await Wait.UntilAsync(() => !_vnyan.IsWebSocketOpen, "closed");
        await _vnyan.SendWebSocketAsync("Wave");
        var again = await _webSocket.NextAsync();
        Assert.Equal("Wave", await again.NextSentAsync());
    }

    [Fact]
    public async Task WebSocket_values_must_be_numbers_and_a_closed_receiver_says_what_to_switch_on()
    {
        Assert.Equal("Throw 2.5", VnyanProtocol.Command("Throw", "2.5"));
        Assert.Equal("Throw 100000", VnyanProtocol.Command(" Throw ", "1e5"));
        Assert.Throws<ArgumentException>(() => VnyanProtocol.Command("Throw", "2,5"));
        Assert.Throws<ArgumentException>(() => VnyanProtocol.Command("Throw", "lots"));
        Assert.Throws<ArgumentException>(() => VnyanProtocol.Command(" ", "1"));

        _webSocket.Reject = _ => new WebSocketException("Connection refused");
        var ex = await Assert.ThrowsAsync<StreamAppRequestException>(() => _vnyan.SendWebSocketAsync("Wave"));
        Assert.Contains("Start Receiver", ex.Message);
        Assert.Equal(ConnectionState.Error, _vnyan.State);
    }

    // ---- OSC parameters ----

    [Fact]
    public async Task Parameters_go_to_the_osc_port_as_float_or_string()
    {
        var (ok, error) = await RunAsync("vnyan.setParameter", new() { ["name"] = "bits", ["value"] = "100" });
        Assert.False(ok);
        Assert.Contains("OSC port", error);
        Assert.Empty(_udp);

        _vnyan.Settings.OscPort = 9123;
        (ok, error) = await RunAsync("vnyan.setParameter", new() { ["name"] = "bits", ["value"] = "%bits%" }, new() { ["bits"] = 2.5 });
        Assert.True(ok, error);
        (ok, error) = await RunAsync("vnyan.setParameter", new() { ["name"] = "donor", ["value"] = "%user%", ["type"] = "Text" }, new() { ["user"] = "Ann" });
        Assert.True(ok, error);
        (ok, error) = await RunAsync("vnyan.setAvatarParameter", new() { ["name"] = "HatOn", ["value"] = "true" });
        Assert.True(ok, error);

        Assert.Equal(3, _udp.Count);
        Assert.All(_udp, u => Assert.Equal(new IPEndPoint(IPAddress.Loopback, 9123), u.Target));
        Assert.Equal(new OscMessage("/VNyan/Param/Float", "bits", 2.5f).ToString(), Assert.Single(Osc.Decode(_udp[0].Packet)).ToString());
        Assert.Equal(new OscMessage("/VNyan/Param/String", "donor", "Ann").ToString(), Assert.Single(Osc.Decode(_udp[1].Packet)).ToString());
        Assert.Equal(new OscMessage("/avatar/parameters/HatOn", 1f).ToString(), Assert.Single(Osc.Decode(_udp[2].Packet)).ToString());

        (ok, error) = await RunAsync("vnyan.setParameter", new() { ["name"] = "bits", ["value"] = "1,5" });
        Assert.False(ok);
        Assert.Contains("must be a number", error);
    }

    [Fact]
    public async Task Several_parameters_go_as_one_bundle()
    {
        _vnyan.Settings.OscPort = 9123;
        var (ok, error) = await RunAsync("vnyan.setParameters", new() { ["values"] = "bits=%bits%\ndonor=%user%\ncode=007" },
            new() { ["bits"] = 300, ["user"] = "Ann \"the\" = best" });
        Assert.True(ok, error);
        var packet = Assert.Single(_udp).Packet;
        Assert.Equal("#bundle\0"u8.ToArray(), packet[..8]);
        var messages = Osc.Decode(packet);
        Assert.Equal(3, messages.Count);
        Assert.Equal(new OscMessage("/VNyan/Param/Float", "bits", 300f).ToString(), messages[0].ToString());
        Assert.Equal(new OscMessage("/VNyan/Param/String", "donor", "Ann \"the\" = best").ToString(), messages[1].ToString());
        Assert.Equal(new OscMessage("/VNyan/Param/String", "code", "007").ToString(), messages[2].ToString());

        _udp.Clear();
        (ok, error) = await RunAsync("vnyan.setParameters", new() { ["values"] = "bits=300", ["type"] = "Text" });
        Assert.True(ok, error);
        Assert.Equal("/VNyan/Param/String", Assert.Single(Osc.Decode(Assert.Single(_udp).Packet)).Address);
    }

    [Fact]
    public async Task Osc_really_goes_out_over_udp()
    {
        using var listener = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        _env.Settings.Value.Vnyan.OscPort = ((IPEndPoint)listener.Client.LocalEndPoint!).Port;
        await using var vnyan = new VnyanService(_env.Settings, _env.Bus, _env.Log, new HttpClient(_http));
        await vnyan.SendOscAsync([VnyanProtocol.Parameter("mood", "3", number: true)]);
        using var cts = new CancellationTokenSource(Wait.Timeout);
        var received = await listener.ReceiveAsync(cts.Token);
        Assert.Equal(new OscMessage("/VNyan/Param/Float", "mood", 3f).ToString(), Assert.Single(Osc.Decode(received.Buffer)).ToString());
    }

    [Fact]
    public void Bundles_encode_like_osc_1_0()
    {
        var bundle = Osc.EncodeBundle([new OscMessage("/a", 1), new OscMessage("/b", "x")]);
        Assert.Equal("#bundle\0"u8.ToArray(), bundle[..8]);
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0, 0, 1 }, bundle[8..16]);
        var first = Osc.Encode(new OscMessage("/a", 1));
        Assert.Equal(new byte[] { 0, 0, 0, (byte)first.Length }, bundle[16..20]);
        Assert.Equal(first, bundle[20..(20 + first.Length)]);
        Assert.Equal(["/a", "/b"], Osc.Decode(bundle).Select(m => m.Address));
    }

    // ---- VNyan → Cuebot ----

    [Fact]
    public void Incoming_text_splits_like_vnyans_websocket_node_and_json_keeps_its_types()
    {
        var text = VnyanProtocol.ParseIncoming("Throw 5");
        Assert.Equal("Throw", text["vnyanCommand"]);
        Assert.Equal(5L, text["vnyanValue"]);
        Assert.Equal(false, text["vnyanJson"]);
        Assert.Equal(2.5, VnyanProtocol.ParseIncoming("Throw 2.5")["vnyanValue"]);
        Assert.Equal("hello there", VnyanProtocol.ParseIncoming("Say hello there")["vnyanValue"]);
        Assert.Equal("", VnyanProtocol.ParseIncoming("Wave")["vnyanValue"]);

        var json = VnyanProtocol.ParseIncoming("""{"event":"Hit","user":"Ann","power":2.5,"count":3,"message":"x"}""");
        Assert.Equal("Hit", json["vnyanCommand"]);
        Assert.Equal("Ann", json["user"]);
        Assert.Equal(2.5, json["power"]);
        Assert.Equal(3L, json["count"]);
        Assert.Equal(true, json["vnyanJson"]);
        Assert.StartsWith("{", (string)json["message"]!);
    }

    [Fact]
    public async Task Vnyans_websocket_message_node_runs_the_trigger()
    {
        var port = FreePort();
        _vnyan.Settings.ListenPort = port;
        _vnyan.Settings.Listen = true;
        await _vnyan.StartAsync();
        await Wait.UntilAsync(() => _vnyan.IsListening, "listening");

        using var client = new ClientWebSocket();
        await client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/anything"), CancellationToken.None);
        await Wait.UntilAsync(() => _vnyan.ListenerClients == 1, "client");
        Assert.Equal(ConnectionState.Connected, _vnyan.State);
        Assert.Contains("VNyan connected", _vnyan.StatusMessage);

        await client.SendAsync("Throw 5"u8.ToArray(), WebSocketMessageType.Text, true, CancellationToken.None);
        var e = await _env.WaitForEventAsync(VnyanEvents.Message);
        Assert.Equal("Throw", e.Args["vnyanCommand"]);
        Assert.Equal(5L, e.Args["vnyanValue"]);

        await client.SendAsync(Encoding.UTF8.GetBytes("""{"event":"Hit","user":"Ann"}"""), WebSocketMessageType.Text, true, CancellationToken.None);
        var hit = await _env.WaitForEventAsync(VnyanEvents.Message, x => x.Text("vnyanCommand") == "Hit");
        Assert.Equal("Ann", hit.Args["user"]);

        var trigger = Assert.Single(new VnyanTriggers().GetTriggers());
        Assert.True(Matches(trigger, hit, "Wave, hit"));
        Assert.True(Matches(trigger, e, ""));
        Assert.False(Matches(trigger, e, "Wave"));

        await _vnyan.StopListeningAsync();
        Assert.False(_vnyan.IsListening);
    }

    private static bool Matches(ITriggerType trigger, BotEvent e, string commands) =>
        trigger.Matches(e, new TriggerDefinition(), ParameterValues.From(trigger.Parameters, new Dictionary<string, string> { ["command"] = commands }),
            new Dictionary<string, object?>());

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    // ---- Names used in actions ----

    [Fact]
    public void Used_action_names_are_listed_and_case_clashes_found()
    {
        var actions = new[]
        {
            new ActionDefinition
            {
                SubActions =
                [
                    new SubActionDefinition { Type = "vnyan.send", Parameters = new() { ["action"] = "Wave" } },
                    new SubActionDefinition
                    {
                        Type = "core.if",
                        Branches = new() { ["true"] = [new SubActionDefinition { Type = "vnyan.send", Parameters = new() { ["action"] = "wave" } }] },
                    },
                    new SubActionDefinition { Type = "vnyan.send", Parameters = new() { ["action"] = "%name%" } },
                    new SubActionDefinition { Type = "vnyan.send", Parameters = new() { ["action"] = "Throw" } },
                ],
            },
        };
        var names = VnyanSubActions.ActionNames(actions);
        Assert.Equal(["Throw", "Wave", "wave"], names);
        Assert.Equal(["Wave / wave"], VnyanService.CaseClashes(names));
    }
}
