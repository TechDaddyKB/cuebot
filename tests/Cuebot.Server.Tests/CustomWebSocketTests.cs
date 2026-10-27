using Cuebot.Core.Actions;
using Cuebot.Server.Custom;

namespace Cuebot.Server.Tests;

public sealed class CustomWebSocketTests
{
    private static TestBot Bot(out CustomWebSocketServerDefinition server)
    {
        server = new CustomWebSocketServerDefinition { Name = "Game", Port = 0, Endpoint = "/game" };
        return new TestBot(new ServerSettings { CustomServers = [server] });
    }

    [Fact]
    public async Task Custom_server_publishes_open_message_close_and_sends()
    {
        await using var bot = Bot(out var server);
        await bot.Manager.CustomServers.StartAsync(server);
        Assert.True(bot.Manager.CustomServers.IsListening(server.Id));
        var port = bot.Manager.CustomServers.Port(server.Id);

        var client = await WsClient.ConnectAsync(port, "/game");
        var open = await bot.WaitForEventAsync(CustomWebSocketEvents.ServerOpen);
        Assert.Equal("Game", open.Args["serverName"]);
        Assert.Equal(0, open.Args["serverIndex"]);
        var session = (string)open.Args["sessionId"]!;

        await client.SendRawAsync("""{"score":5}""");
        var message = await bot.WaitForEventAsync(CustomWebSocketEvents.ServerMessage);
        Assert.Equal("""{"score":5}""", message.Args["message"]);
        Assert.Equal(session, message.Args["sessionId"]);

        Assert.Equal(1, await bot.Manager.CustomServers.BroadcastAsync(server.Id, """{"reply":true}"""));
        var reply = await client.NextAsync(m => m["reply"] is not null);
        Assert.True(reply["reply"]!.GetValue<bool>());

        Assert.True(await bot.Manager.CustomServers.CloseSessionAsync(server.Id, session));
        await bot.WaitForEventAsync(CustomWebSocketEvents.ServerClose);
        await client.DisposeAsync();

        await bot.Manager.CustomServers.StopAsync(server.Id);
        Assert.False(bot.Manager.CustomServers.IsListening(server.Id));
    }

    [Fact]
    public async Task Server_triggers_filter_by_name_and_sub_action_answers_the_sender()
    {
        await using var bot = Bot(out var server);
        var registry = new Registry([], [], [new CustomWebSocketTriggers(bot.Manager)]);
        var trigger = registry.GetTrigger("websocket.serverMessage")!;
        var matching = new Core.Events.BotEvent(CustomWebSocketEvents.ServerMessage, new Dictionary<string, object?> { ["serverName"] = "Game" });
        var other = new Core.Events.BotEvent(CustomWebSocketEvents.ServerMessage, new Dictionary<string, object?> { ["serverName"] = "Other" });
        var settings = ParameterValues.From(trigger.Parameters, new Dictionary<string, string> { ["server"] = "game" });
        Assert.True(trigger.Matches(matching, new TriggerDefinition(), settings, new Dictionary<string, object?>()));
        Assert.False(trigger.Matches(other, new TriggerDefinition(), settings, new Dictionary<string, object?>()));

        await bot.Manager.CustomServers.StartAsync(server);
        await using var client = await WsClient.ConnectAsync(bot.Manager.CustomServers.Port(server.Id), "/game");
        var open = await bot.WaitForEventAsync(CustomWebSocketEvents.ServerOpen);
        var send = new CustomServerSendSubAction(bot.Manager);
        var action = new ActionDefinition { Name = "Reply" };
        action.SubActions.Add(new SubActionDefinition
        {
            Type = send.TypeId,
            Parameters = { ["server"] = "Game", ["message"] = """{"pong":1}""", ["sessionId"] = "%sessionId%" },
        });
        using var engine = new ActionEngine(bot.Bus, bot.Profile, new Registry([send], []), bot.Globals, bot.Log);
        Assert.True(await engine.RunAsync(action, new Dictionary<string, object?> { ["sessionId"] = open.Args["sessionId"] }));
        var pong = await client.NextAsync(m => m["pong"] is not null);
        Assert.Equal(1, pong["pong"]!.GetValue<int>());
    }

    [Fact]
    public async Task Custom_client_connects_receives_sends_and_reconnects_off()
    {
        var server = new CustomWebSocketServerDefinition { Name = "Peer", Port = 0, Endpoint = "/" };
        await using var bot = new TestBot(new ServerSettings { CustomServers = [server] });
        await bot.Manager.CustomServers.StartAsync(server);
        var port = bot.Manager.CustomServers.Port(server.Id);
        var clientDef = new CustomWebSocketClientDefinition { Name = "Out", Url = $"ws://127.0.0.1:{port}/", AutoReconnect = false };
        bot.Manager.Settings.CustomClients.Add(clientDef);

        await bot.Manager.CustomClients.ConnectAsync(clientDef);
        Assert.True(bot.Manager.CustomClients.IsConnected(clientDef.Id));
        var open = await bot.WaitForEventAsync(CustomWebSocketEvents.ClientOpen);
        Assert.Equal("Out", open.Args["clientName"]);

        // The in-process peer (our custom server) talks to the client and back.
        await bot.WaitForEventAsync(CustomWebSocketEvents.ServerOpen);
        await bot.Manager.CustomServers.BroadcastAsync(server.Id, "hello client");
        var received = await bot.WaitForEventAsync(CustomWebSocketEvents.ClientMessage);
        Assert.Equal("hello client", received.Args["message"]);

        Assert.True(await bot.Manager.CustomClients.SendAsync(clientDef.Id, "hello server"));
        var atServer = await bot.WaitForEventAsync(CustomWebSocketEvents.ServerMessage, e => Equals(e.Args["message"], "hello server"));
        Assert.Equal("Peer", atServer.Args["serverName"]);

        await bot.Manager.CustomClients.DisconnectAsync(clientDef.Id);
        Assert.False(bot.Manager.CustomClients.IsConnected(clientDef.Id));
        await bot.WaitForEventAsync(CustomWebSocketEvents.ClientClose);
    }

    [Fact]
    public async Task Client_to_a_closed_port_reports_an_error_without_throwing()
    {
        await using var bot = new TestBot();
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var def = new CustomWebSocketClientDefinition { Name = "Dead", Url = $"ws://127.0.0.1:{port}/", AutoReconnect = false };
        bot.Manager.Settings.CustomClients.Add(def);
        await bot.Manager.CustomClients.ConnectAsync(def);
        Assert.False(bot.Manager.CustomClients.IsConnected(def.Id));
    }

    [Fact]
    public async Task Script_functions_reach_custom_servers_by_index_or_name()
    {
        await using var bot = Bot(out var server);
        var functions = new WebSocketScriptFunctions(bot.Manager).GetFunctions().ToDictionary(f => f.Name);
        var start = functions["websocket.serverStart"];
        await start.Invoke(null!, System.Text.Json.JsonSerializer.SerializeToElement(new { connection = "Game" }), default);
        Assert.True(bot.Manager.CustomServers.IsListening(server.Id));
        var listening = await functions["websocket.serverIsListening"].Invoke(null!, System.Text.Json.JsonSerializer.SerializeToElement(new { connection = 0 }), default);
        Assert.Equal(true, listening);
        var index = await functions["websocket.serverIndex"].Invoke(null!, System.Text.Json.JsonSerializer.SerializeToElement(new { name = "game" }), default);
        Assert.Equal(0, index);
    }
}
