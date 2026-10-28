using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.StreamApps.Lumia;
using Cuebot.StreamApps.Tests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.StreamApps.Tests;

public sealed class LumiaProtocolTests
{
    [Fact]
    public void Colours_map_to_hex_or_rgb_requests()
    {
        Assert.Equal(("hex-color", (object)"#FF4076"), LumiaProtocol.Color("#ff4076"));
        Assert.Equal(("hex-color", (object)"#FF4076"), LumiaProtocol.Color("#80FF4076"));   // alpha dropped
        Assert.Equal(("hex-color", (object)"#AABBCC"), LumiaProtocol.Color("abc"));
        var (type, value) = LumiaProtocol.Color("20, 100, 10");
        Assert.Equal("rgb-color", type);
        Assert.Equal("""{"r":20,"g":100,"b":10}""", JsonSerializer.Serialize(value));
        Assert.Throws<ArgumentException>(() => LumiaProtocol.Color("blue"));
        Assert.Throws<ArgumentException>(() => LumiaProtocol.Color("1,2,300"));
    }

    [Fact]
    public void Send_bodies_match_the_documented_shape()
    {
        Assert.Equal("""{"type":"to-default"}""", LumiaProtocol.Send("to-default"));
        Assert.Equal("""{"type":"chat-command","params":{"value":"blue","hold":true,"extraSettings":{"username":"lumia"}}}""",
            LumiaProtocol.Send("chat-command", LumiaProtocol.CommandParams("blue", true, new Dictionary<string, string> { ["username"] = "lumia" })));
        var lights = LumiaProtocol.ResolveLights("Hue go, nanoleaf:167", [new("19", "Hue go", "hue")]);
        Assert.Equal("""{"type":"hex-color","params":{"value":"#FF4076","brightness":100,"duration":5000,"lights":[{"type":"hue","value":"19"},{"type":"nanoleaf","value":"167"}]}}""",
            LumiaProtocol.Send("hex-color", LumiaProtocol.ColorParams("#FF4076", 120, null, 5000, false, lights)));
        Assert.Throws<StreamAppRequestException>(() => LumiaProtocol.ResolveLights("Desk lamp", []));
        Assert.Equal("""{"type":"toggle-stream-mode","params":{"value":null}}""",
            LumiaProtocol.Send("toggle-stream-mode", new Dictionary<string, object?> { ["value"] = null }));
    }

    [Fact]
    public void Retrieve_payload_parses_commands_lights_and_voices()
    {
        var info = LumiaProtocol.ParseInfo(JsonDocument.Parse(FakeLumiaHttp.Retrieve).RootElement.GetProperty("data"));
        Assert.Equal(["blue", "red"], info.Values("chat-command"));
        Assert.Contains("twitch-follower", info.Values("alert"));
        Assert.Equal(["Agnes", "Brian"], info.Voices);
        Assert.Equal("Hue go", info.Lights.Single(l => l.Id == "19").Name);
        Assert.Equal("nanoleaf:167", info.Lights.Single(l => l.Id == "167").Display);
        Assert.True(info.Premium);
    }

    [Fact]
    public void Pushed_messages_become_events()
    {
        static (string Type, Dictionary<string, object?> Args) Parse(string json) => LumiaProtocol.Event(JsonDocument.Parse(json).RootElement)!.Value;

        var chat = Parse("""
            {"origin":"twitch","type":"chat","data":{"id":"09d9","channel":"#lumiastream","message":"lumia is awesome",
             "extraSettings":{"username":"lumiastream","displayname":"LumiaStream","userId":"605116711","userLevels":{"mod":true,"vip":false},
             "messageId":"8851","time":"18:23:41","timestamp":1783445021000}}}
            """);
        Assert.Equal(LumiaEvents.ChatMessage, chat.Type);
        Assert.Equal("lumia is awesome", chat.Args["message"]);
        Assert.Equal("lumiastream", chat.Args["user"]);
        Assert.Equal("LumiaStream", chat.Args["displayName"]);
        Assert.Equal("8851", chat.Args["messageId"]);
        Assert.Equal(true, chat.Args["lumia.userLevels.mod"]);
        Assert.Equal("twitch", chat.Args["lumia.origin"]);

        var deleted = Parse("""{"origin":"twitch","type":"chat","data":{"shouldRemove":"8851","extraSettings":{"time":"18:24:02"}}}""");
        Assert.Equal((LumiaEvents.ChatMessageDeleted, "8851"), (deleted.Type, deleted.Args["messageId"]));

        var alert = Parse("""
            {"origin":"twitch","type":"alert","event":"twitch-bits","data":{"dynamic":{"value":1000},"replay":true,
             "extraSettings":{"username":"lumiastream","bits":1000}}}
            """);
        Assert.Equal(LumiaEvents.Alert, alert.Type);
        Assert.Equal("twitch-bits", alert.Args["lumia.alert"]);
        Assert.Equal(1000L, alert.Args["lumia.dynamic.value"]);
        Assert.Equal(1000L, alert.Args["lumia.bits"]);
        Assert.Equal(true, alert.Args["lumia.replay"]);

        var states = Parse("""{"type":"states","data":{"on":1,"streamMode":0,"fuze":0,"listenId":"abc123"}}""");
        Assert.Equal((LumiaEvents.StatesChanged, true, false), (states.Type, states.Args["lumia.on"], states.Args["lumia.streamMode"]));

        Assert.Equal(LumiaEvents.Event, Parse("""{"origin":"lumiastream","type":"premium","data":{"premium":true}}""").Type);
        Assert.Null(LumiaProtocol.Event(JsonDocument.Parse("""{"event":"socketapi:response","context":1,"status":200}""").RootElement));
    }
}

public sealed class LumiaServiceTests : IAsyncLifetime
{
    private const string Token = "s3cr3t-T0ken+/=";
    private readonly TestEnvironment _env = new();
    private readonly FakeLumiaHttp _http = new(Token);
    private LumiaService _lumia = null!;
    private FakeSocket _socket = null!;

    public async Task InitializeAsync()
    {
        _env.Settings.Value.Lumia.Token = Token;
        _lumia = new LumiaService(_env.Settings, _env.Bus, _env.Log, new HttpClient(_http), _env.Server.Create, TestTimings.Fast);
        await _lumia.ConnectAsync();
        _socket = await _env.Server.NextAsync();
        await _env.WaitForEventAsync(LumiaEvents.Connected);
        await Wait.UntilAsync(() => _lumia.Info.Lights.Count == 3, "settings retrieved");
    }

    public async Task DisposeAsync()
    {
        await _lumia.DisposeAsync();
        _env.Dispose();
    }

    [Fact]
    public void Connects_with_the_token_and_reads_settings()
    {
        var url = _socket.Url!;
        Assert.Equal("ws://127.0.0.1:39231/api", url.GetLeftPart(UriPartial.Path));
        Assert.Contains("token=" + Uri.EscapeDataString(Token), url.Query);
        Assert.Contains("name=Cuebot", url.Query);
        Assert.Equal("/api/retrieve", _http.Requests.First().Url.AbsolutePath);
        Assert.Contains("slim=true", _http.Requests.First().Url.Query);
        Assert.Contains("2 commands, 3 lights", _lumia.StatusMessage);
        Assert.DoesNotContain(Token, _lumia.StatusMessage);
    }

    [Fact]
    public async Task Sends_commands_colours_and_messages_over_rest()
    {
        await _lumia.SendCommandAsync("blue", hold: true);
        await _lumia.SetColorAsync("#00ff00", brightness: 50, lights: "Hue go");
        await _lumia.SetToDefaultAsync();
        await _lumia.SendChatbotMessageAsync("hi", "YouTube", asSelf: true);
        await _lumia.SetStreamModeAsync(SwitchMode.Toggle);
        Assert.Equal("Kris", await _lumia.GetVariableAsync("twitch_username"));

        var bodies = _http.Requests.Where(r => r.Url.AbsolutePath == "/api/send").Select(r => r.Body).ToArray();
        Assert.Equal(
        [
            """{"type":"chat-command","params":{"value":"blue","hold":true}}""",
            """{"type":"hex-color","params":{"value":"#00FF00","brightness":50,"lights":[{"type":"hue","value":"19"}]}}""",
            """{"type":"to-default"}""",
            """{"type":"chatbot-message","params":{"value":"hi","platform":"youtube","userToChatAs":"self"}}""",
            """{"type":"toggle-stream-mode","params":{"value":null}}""",
            """{"type":"get-variable-value","params":{"name":"twitch_username"}}""",
        ], bodies);
        Assert.All(_http.Requests, r => Assert.Contains("token=", r.Url.Query));
    }

    [Fact]
    public async Task Errors_are_reported_without_the_token()
    {
        var ex = await Assert.ThrowsAsync<StreamAppRequestException>(() => _lumia.SendCommandAsync("blu"));
        Assert.Contains("No chat-command named", ex.Message);
        Assert.Contains("not_found", ex.Message);
        Assert.DoesNotContain(Token, ex.Message);

        _http.Echo = true;   // a server that echoes the URL in its error text
        var echoed = await Assert.ThrowsAsync<StreamAppRequestException>(() => _lumia.SendCommandAsync("blu"));
        Assert.DoesNotContain(Token, echoed.Message);
        Assert.DoesNotContain(Uri.EscapeDataString(Token), echoed.Message);
        Assert.Contains("***", echoed.Message);
        Assert.All(_env.Log.Recent(), e => Assert.DoesNotContain(Token, e.Message));
    }

    [Fact]
    public async Task Pushed_events_reach_the_bus_and_triggers_filter_them()
    {
        _socket.Push("""{"origin":"twitch","type":"alert","event":"twitch-follower","data":{"dynamic":{"value":"kris"},"replay":false,"extraSettings":{"username":"kris"}}}""");
        _socket.Push("""{"origin":"kick","type":"command","data":{"username":"kris","command":"blue","extraSettings":{}}}""");
        _socket.Push("""{"type":"states","data":{"on":1,"streamMode":1,"fuze":0}}""");
        _socket.Push("""{"event":"socketapi:subscribed","channels":"all"}""");
        var alert = await _env.WaitForEventAsync(LumiaEvents.Alert);
        var command = await _env.WaitForEventAsync(LumiaEvents.Command);
        await _env.WaitForEventAsync(LumiaEvents.StatesChanged);
        Assert.Equal((true, true, false), _lumia.States);
        Assert.Equal("127.0.0.1", alert.Args["lumia.host"]);

        var triggers = new LumiaTriggers(_lumia).GetTriggers().ToDictionary(t => t.TypeId);
        Assert.True(Matches(triggers[LumiaEvents.Alert], alert, new() { ["alert"] = "twitch-follower, twitch-raid" }));
        Assert.False(Matches(triggers[LumiaEvents.Alert], alert, new() { ["replays"] = "Only" }));
        Assert.True(Matches(triggers[LumiaEvents.Command], command, new() { ["command"] = "Blue", ["origin"] = "kick" }));
        Assert.False(Matches(triggers[LumiaEvents.Command], command, new() { ["origin"] = "twitch" }));
        Assert.Empty(_env.Events(LumiaEvents.Event));   // socket replies are not events
    }

    [Fact]
    public async Task Sub_actions_run_through_the_engine()
    {
        var handlers = LumiaSubActions.Create(_lumia);
        Assert.Equal(LumiaSubActions.TypeIds.Order(), handlers.Select(h => h.TypeId).Order());
        var (ok, args, error) = await ActionHarness.RunAsync(handlers,
            ("lumia.sendCommand", new() { ["command"] = "red", ["commandType"] = "twitch-points", ["extraSettings"] = "username=kris" }),
            ("lumia.setColor", new() { ["color"] = "1,2,3", ["default"] = "False" }),
            ("lumia.setColor", new() { ["default"] = "True" }),
            ("lumia.getVariable", new() { ["name"] = "twitch_username" }),
            ("lumia.send", new() { ["type"] = "studio-scene", ["params"] = """{"value":"Sunset"}""" }));
        Assert.True(ok, error);
        Assert.Equal("Kris", args["lumia.variableValue"]);
        var bodies = _http.Requests.Where(r => r.Url.AbsolutePath == "/api/send").Select(r => r.Body).ToArray();
        Assert.Equal(
        [
            """{"type":"twitch-points","params":{"value":"red","extraSettings":{"username":"kris"}}}""",
            """{"type":"rgb-color","params":{"value":{"r":1,"g":2,"b":3}}}""",
            """{"type":"to-default"}""",
            """{"type":"get-variable-value","params":{"name":"twitch_username"}}""",
            """{"type":"studio-scene","params":{"value":"Sunset"}}""",
        ], bodies);
    }

    [Fact]
    public async Task Without_a_token_it_does_not_connect_and_says_why()
    {
        var env = new TestEnvironment();
        env.Settings.Value.Lumia.AutoConnect = true;
        await using var lumia = new LumiaService(env.Settings, env.Bus, env.Log, new HttpClient(_http), env.Server.Create, TestTimings.Fast);
        await lumia.StartAsync();
        Assert.False(lumia.IsRunning);
        await lumia.ConnectAsync();
        await Wait.UntilAsync(() => lumia.State == ConnectionState.Error && lumia.StatusMessage.Contains("token"), "no-token status");
        Assert.Empty(env.Server.Attempts);
        await Assert.ThrowsAsync<StreamAppNotConnectedException>(() => lumia.SetToDefaultAsync());
        env.Dispose();
    }

    private static bool Matches(ITriggerType trigger, BotEvent e, Dictionary<string, string> settings) =>
        trigger.Matches(e, new TriggerDefinition(), ParameterValues.From(trigger.Parameters, settings), new Dictionary<string, object?>());
}

/// <summary>Lumia's REST API as documented: retrieve, send (structured 404 for unknown commands), get-variable-value.</summary>
internal sealed class FakeLumiaHttp(string token) : HttpMessageHandler
{
    public const string Retrieve = """
        {"status":200,"data":{"premium":true,"types":["alert","chat-command"],
         "options":{"alert":{"values":["twitch-follower","twitch-bits"]},"chat-command":{"values":["blue","red"]},
                    "clear-queue":null,"tts":{"values":{"voices":[{"id":"Agnes","label":"Agnes"},{"id":"Brian","label":"Brian"}]}},
                    "twitch-points":{"values":["red"]}},
         "lights":[{"id":"17","name":"Hue globe","type":"hue"},{"id":"19","name":"Hue go","type":"hue"},{"id":"167","type":"nanoleaf"}]}}
        """;

    public ConcurrentQueue<(Uri Url, string Body)> Requests { get; } = new();

    public bool Echo { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        var url = request.RequestUri!;
        Requests.Enqueue((url, body));
        if (!url.Query.Contains("token=" + Uri.EscapeDataString(token))) return Json(HttpStatusCode.Unauthorized, """{"status":401,"message":"bad token"}""");
        if (url.AbsolutePath == "/api/retrieve") return Json(HttpStatusCode.OK, Retrieve);
        if (url.AbsolutePath != "/api/send") return Json(HttpStatusCode.NotFound, "Not found");
        if (body.Contains("\"blu\""))
            return Echo
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent($"bad request {url}") }
                : Json(HttpStatusCode.NotFound, """{"status":404,"code":"not_found","message":"No chat-command named \"blu\" exists."}""");
        if (body.Contains("get-variable-value")) return Json(HttpStatusCode.OK, """{"message":"Kris","status":200}""");
        return Json(HttpStatusCode.OK, """{"message":true,"status":200}""");
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
}

public sealed class TitsLumiaRegistrationTests
{
    [Fact]
    public async Task Both_apps_register_statuses_triggers_sub_actions_and_script_functions()
    {
        var dir = Directory.CreateTempSubdirectory("cuebot-streamapps-tl-").FullName;
        await using (var services =
                         new ServiceCollection().AddCuebotCore(new AppPaths(dir)).AddStreamApps().BuildServiceProvider())
        {
            T Get<T>() where T : notnull => services.GetRequiredService<T>();
            var statuses = services.GetServices<IConnectionStatus>().Select(s => s.Name).ToArray();
            Assert.Contains("T.I.T.S.", statuses);
            Assert.Contains("Lumia Stream", statuses);

            var registry = Get<Registry>();
            foreach (var id in Cuebot.StreamApps.Tits.TitsSubActions.TypeIds.Concat(LumiaSubActions.TypeIds)) Assert.NotNull(registry.GetSubAction(id));
            foreach (var id in new[] { "tits.itemHit", "tits.triggerActivated", "tits.triggerEnded", "tits.connected", "tits.eventsConnected",
                         "tits.websocketDisconnected", "lumia.alert", "lumia.chatMessage", "lumia.command", "lumia.statesChanged" })
                Assert.NotNull(registry.GetTrigger(id));

            var functions = Get<Cuebot.Core.Scripting.ScriptApi>().Functions.Select(f => f.Name).ToArray();
            foreach (var name in new[] { "tits.throwItemByName", "tits.activateTriggerByName", "tits.throwItems", "lumia.sendCommand", "lumia.setToDefault",
                         "lumia.setColor", "meldstudio.showScene" })
                Assert.Contains(name, functions);

            var settings = Get<Cuebot.Core.Storage.JsonFile<StreamAppsSettings>>().Value;
            Assert.Equal(42069, settings.Tits.Port);
            Assert.Equal(39231, settings.Lumia.Port);
            Assert.False(settings.Tits.AutoConnect);
            Assert.False(settings.Lumia.AutoConnect);
            await Get<Cuebot.StreamApps.Tits.TitsService>().StartAsync();
            await Get<LumiaService>().StartAsync();
            Assert.False(Get<Cuebot.StreamApps.Tits.TitsService>().IsRunning);
            Assert.False(Get<LumiaService>().IsRunning);

            // Disconnected sub-actions fail cleanly.
            var engine = Get<ActionEngine>();
            await engine.RunAsync(new ActionDefinition { Name = "t", SubActions = [new SubActionDefinition { Type = "tits.throwItemByName", Parameters = new() { ["item"] = "apple" } }] });
            await engine.RunAsync(new ActionDefinition { Name = "l", SubActions = [new SubActionDefinition { Type = "lumia.setToDefault" }] });
            Assert.Contains(engine.History.Entries, e => e.Error?.Contains("T.I.T.S. is not connected") == true);
            Assert.Contains(engine.History.Entries, e => e.Error?.Contains("Lumia Stream is not connected") == true);
        }
        try { Directory.Delete(dir, recursive: true); } catch (IOException) { /* best effort */ }
    }
}
