using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Core.Events;

namespace Cuebot.Server.Tests;

public sealed class SpeakerBotTests : IAsyncLifetime
{
    private TestBot _bot = null!;
    private WsClient _client = null!;

    public async Task InitializeAsync()
    {
        _bot = new TestBot(new ServerSettings
        {
            SpeakerBot = new SpeakerBotSettings { WebSocketEnabled = true, WebSocketPort = 0, UdpEnabled = true, UdpPort = 0 },
        });
        await _bot.Manager.StartAsync();
        _client = await WsClient.ConnectAsync(_bot.Manager.SpeakerBotWebSocket!.Port);
    }

    public async Task DisposeAsync()
    {
        await _client.DisposeAsync();
        await _bot.DisposeAsync();
    }

    private static string? S(JsonNode? node) => node?.GetValue<string>();

    [Fact]
    public async Task Speak_uses_the_voice_alias()
    {
        var response = await _client.RequestAsync("Speak", new { voice = "Narrator", message = "Hello there", badWordFilter = true });
        Assert.Equal("ok", S(response["status"]));
        Assert.True(response["success"]!.GetValue<bool>());
        Assert.Contains("speak:Narrator:Hello there:True:False:False", _bot.Speaker.Log);

        var silent = await _client.RequestAsync("Speak", new { voice = "Robot", message = "x", silent = true });
        Assert.Equal("speech.wav", S(silent["speechFile"]));
        var waited = await _client.RequestAsync("Speak", new { message = "y", wait = true });
        Assert.Equal(1500, waited["duration"]!.GetValue<long>());

        Assert.Equal("error", S((await _client.RequestAsync("Speak", new { message = "" }))["status"]));
    }

    [Fact]
    public async Task Control_requests_change_state()
    {
        await _client.RequestAsync("Stop");
        Assert.Contains("stop", _bot.Speaker.Log);
        await _client.RequestAsync("Clear");
        Assert.Contains("clear", _bot.Speaker.Log);

        Assert.True((await _client.RequestAsync("Pause"))["paused"]!.GetValue<bool>());
        Assert.False((await _client.RequestAsync("Resume"))["paused"]!.GetValue<bool>());
        Assert.False((await _client.RequestAsync("Off"))["enabled"]!.GetValue<bool>());
        Assert.True((await _client.RequestAsync("Enable"))["enabled"]!.GetValue<bool>());
        Assert.False((await _client.RequestAsync("SetEnabled", new { state = "toggle" }))["enabled"]!.GetValue<bool>());
        Assert.True((await _client.RequestAsync("SetPaused", new { state = "Paused" }))["paused"]!.GetValue<bool>());
        Assert.False((await _client.RequestAsync("SetPaused", new { state = "Enabled" }))["paused"]!.GetValue<bool>());
        Assert.False((await _client.RequestAsync("Events", new { state = "off" }))["eventsEnabled"]!.GetValue<bool>());
        Assert.Equal("ok", S((await _client.RequestAsync("Mode", new { mode = "command" }))["status"]));
        Assert.Equal("error", S((await _client.RequestAsync("Mode", new { mode = "sometimes" }))["status"]));
        Assert.True((await _client.RequestAsync("ActivateVoiceGate", new { profile = "Mic" }))["success"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Voices_aliases_and_unknown_requests()
    {
        Assert.Equal(["Narrator", "Robot"], (await _client.RequestAsync("GetAliases"))["aliases"]!.AsArray().Select(S));
        Assert.Equal("Voice One", S((await _client.RequestAsync("GetVoices"))["voices"]![0]!["name"]));
        Assert.Equal("error", S((await _client.RequestAsync("Dance"))["status"]));
    }

    [Fact]
    public async Task Subscribed_clients_receive_speech_events()
    {
        await _client.RequestAsync("Subscribe", new { events = new[] { "SpeechStarted" } });
        _bot.Bus.Publish(new BotEvent("speech.finished", new Dictionary<string, object?> { ["text"] = "skip me" }));
        _bot.Bus.Publish(new BotEvent("speech.started", new Dictionary<string, object?> { ["text"] = "hello", ["alias"] = "Narrator" }));
        var e = await _client.NextAsync(m => m["event"] is not null);
        Assert.Equal("SpeakerBot", S(e["event"]!["source"]));
        Assert.Equal("SpeechStarted", S(e["event"]!["type"]));
        Assert.Equal("hello", S(e["data"]!["text"]));
    }

    [Fact]
    public async Task Udp_commands_drive_speech()
    {
        using var udp = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, _bot.Manager.SpeakerBotUdp!.Port);
        async Task Send(string json)
        {
            await udp.SendAsync(Encoding.UTF8.GetBytes(json), target);
        }

        await Send("""{"command":"speak","voice":"Robot","message":"from udp"}""");
        await Send("""{"command":"pause"}""");
        await Send("""{"command":"set","method":"nickname","username":"ann","nickname":"Annie"}""");
        await Send("""{"command":"reg","mode":"add","user":"bob","id":"7"}""");
        await StreamerBotWebSocketTests.WaitUntil(() => _bot.Speaker.Log.Contains("reg:bob:True"));
        Assert.Contains("speak:Robot:from udp:False:False:False", _bot.Speaker.Log);
        Assert.True(_bot.Speaker.Paused);
        Assert.Contains("nick:ann:Annie", _bot.Speaker.Log);
    }

    [Fact]
    public async Task Udp_single_event_toggles_sticky_and_assign_last()
    {
        using var udp = new UdpClient();
        var target = new IPEndPoint(IPAddress.Loopback, _bot.Manager.SpeakerBotUdp!.Port);
        async Task Send(string json) => await udp.SendAsync(Encoding.UTF8.GetBytes(json), target);

        await Send("""{"command":"events","value":"Follow","state":"off"}""");
        await Send("""{"command":"events","value":"Dance","state":"off"}""");
        await Send("""{"command":"set","method":"sticky","value":false}""");
        await Send("""{"command":"assign","method":"last","username":"ann"}""");
        await StreamerBotWebSocketTests.WaitUntil(() => _bot.Speaker.Log.Contains("assign:ann"));

        Assert.False(_bot.Speaker.EventStates["Follow"]);
        Assert.True(_bot.Speaker.EventsEnabled);
        Assert.False(_bot.Speaker.StickyRandom);
    }

    [Fact]
    public async Task WebSocket_single_event_toggles_sticky_and_assign_last()
    {
        var merch = await _client.RequestAsync("Events", new { value = "Merch", state = "toggle" });
        Assert.False(merch["eventEnabled"]!.GetValue<bool>());
        Assert.True(merch["eventsEnabled"]!.GetValue<bool>());
        Assert.Equal("error", S((await _client.RequestAsync("Events", new { value = "Dance", state = "on" }))["status"]));

        Assert.False((await _client.RequestAsync("Set", new { method = "sticky", value = false }))["sticky"]!.GetValue<bool>());
        Assert.True((await _client.RequestAsync("Set", new { method = "sticky", value = "on" }))["sticky"]!.GetValue<bool>());
        await _client.RequestAsync("Set", new { method = "nickname", username = "bob", nickname = "Bobby" });
        Assert.Contains("nick:bob:Bobby", _bot.Speaker.Log);

        var assigned = await _client.RequestAsync("Assign", new { method = "last", username = "@ann" });
        Assert.True(assigned["success"]!.GetValue<bool>());
        Assert.Equal("ann voice", S(assigned["alias"]));
        Assert.False((await _client.RequestAsync("Assign", new { method = "last", username = "nobody" }))["success"]!.GetValue<bool>());
        Assert.Equal("error", S((await _client.RequestAsync("Assign", new { method = "last" }))["status"]));
    }
}
