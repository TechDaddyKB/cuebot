using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Scripting;

namespace Cuebot.Server.Custom;

/// <summary>Triggers for custom WebSocket servers and clients, filtered by server/client name (empty: any).</summary>
public sealed class CustomWebSocketTriggers(ServerManager servers) : ITriggerProvider
{
    public const string Category = "WebSocket";

    public IEnumerable<ITriggerType> GetTriggers()
    {
        var server = new ParameterDescriptor("server", "Server") { Suggestions = () => servers.Settings.CustomServers.Select(s => s.Name), Help = "Empty: any server" };
        var client = new ParameterDescriptor("client", "Client") { Suggestions = () => servers.Settings.CustomClients.Select(c => c.Name), Help = "Empty: any client" };
        return
        [
            Trigger("websocket.serverOpen", "Custom server: connection opened", CustomWebSocketEvents.ServerOpen, server, "serverName"),
            Trigger("websocket.serverMessage", "Custom server: message", CustomWebSocketEvents.ServerMessage, server, "serverName"),
            Trigger("websocket.serverClose", "Custom server: connection closed", CustomWebSocketEvents.ServerClose, server, "serverName"),
            Trigger("websocket.clientOpen", "Client: connected", CustomWebSocketEvents.ClientOpen, client, "clientName"),
            Trigger("websocket.clientMessage", "Client: message", CustomWebSocketEvents.ClientMessage, client, "clientName"),
            Trigger("websocket.clientClose", "Client: disconnected", CustomWebSocketEvents.ClientClose, client, "clientName"),
        ];
    }

    private static EventTriggerType Trigger(string id, string name, string eventType, ParameterDescriptor filter, string argument) =>
        new(id, Category, name, eventType, [filter], (e, settings, _) =>
        {
            var wanted = settings[filter.Key].Trim();
            return wanted.Length == 0 || string.Equals(e.Args.GetValueOrDefault(argument)?.ToString(), wanted, StringComparison.OrdinalIgnoreCase);
        });
}

/// <summary>Sends a message to a custom server's sessions (all, or the one in Session id).</summary>
public sealed class CustomServerSendSubAction(ServerManager servers) : ISubActionHandler
{
    public string TypeId => "websocket.serverSend";
    public string Category => CustomWebSocketTriggers.Category;
    public string DisplayName => "Custom server: send message";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("server", "Server") { Suggestions = () => servers.Settings.CustomServers.Select(s => s.Name), Help = "Name or index" },
        new("message", "Message", ParameterKind.Multiline),
        new("sessionId", "Session id") { Help = "Empty sends to every session; %sessionId% answers the sender" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        var server = servers.CustomServers.Find(parameters["server"]) ?? throw new InvalidOperationException($"No WebSocket server '{parameters["server"]}'");
        context.Args["sentCount"] = await servers.CustomServers.BroadcastAsync(server.Id, parameters["message"], parameters["sessionId"].Trim()).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>Starts or stops a custom server.</summary>
public sealed class CustomServerStateSubAction(ServerManager servers) : ISubActionHandler
{
    public string TypeId => "websocket.serverState";
    public string Category => CustomWebSocketTriggers.Category;
    public string DisplayName => "Custom server: start / stop";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("server", "Server") { Suggestions = () => servers.Settings.CustomServers.Select(s => s.Name), Help = "Name or index" },
        new("state", "State", ParameterKind.Choice) { Choices = ["Start", "Stop"], DefaultValue = "Start" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        var server = servers.CustomServers.Find(parameters["server"]) ?? throw new InvalidOperationException($"No WebSocket server '{parameters["server"]}'");
        if (parameters["state"].Equals("Stop", StringComparison.OrdinalIgnoreCase)) await servers.CustomServers.StopAsync(server.Id).ConfigureAwait(false);
        else await servers.CustomServers.StartAsync(server, ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>Sends a message through a custom client connection.</summary>
public sealed class CustomClientSendSubAction(ServerManager servers) : ISubActionHandler
{
    public string TypeId => "websocket.clientSend";
    public string Category => CustomWebSocketTriggers.Category;
    public string DisplayName => "Client: send message";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("client", "Client") { Suggestions = () => servers.Settings.CustomClients.Select(c => c.Name), Help = "Name or index" },
        new("message", "Message", ParameterKind.Multiline),
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        var client = servers.CustomClients.Find(parameters["client"]) ?? throw new InvalidOperationException($"No WebSocket client '{parameters["client"]}'");
        if (!await servers.CustomClients.SendAsync(client.Id, parameters["message"], ct).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"'{client.Name}' is not connected");
        }
        return SubActionResult.Next;
    }
}

/// <summary>Connects or disconnects a custom client.</summary>
public sealed class CustomClientStateSubAction(ServerManager servers) : ISubActionHandler
{
    public string TypeId => "websocket.clientState";
    public string Category => CustomWebSocketTriggers.Category;
    public string DisplayName => "Client: connect / disconnect";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("client", "Client") { Suggestions = () => servers.Settings.CustomClients.Select(c => c.Name), Help = "Name or index" },
        new("state", "State", ParameterKind.Choice) { Choices = ["Connect", "Disconnect"], DefaultValue = "Connect" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        var client = servers.CustomClients.Find(parameters["client"]) ?? throw new InvalidOperationException($"No WebSocket client '{parameters["client"]}'");
        if (parameters["state"].Equals("Disconnect", StringComparison.OrdinalIgnoreCase)) await servers.CustomClients.DisconnectAsync(client.Id).ConfigureAwait(false);
        else await servers.CustomClients.ConnectAsync(client, ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>Broadcasts to every client of the Streamer.bot-compatible WebSocket server (event General.Custom).</summary>
public sealed class WebSocketBroadcastSubAction(ServerManager servers) : ISubActionHandler
{
    public string TypeId => "websocket.broadcast";
    public string Category => CustomWebSocketTriggers.Category;
    public string DisplayName => "WebSocket server: broadcast";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("message", "Message", ParameterKind.Multiline) { Help = "JSON is sent as an object, other text as a string" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        var text = parameters["message"];
        context.Args["sentCount"] = await servers.WebSocket.BroadcastCustomAsync(text, LooksLikeJson(text)).ConfigureAwait(false);
        return SubActionResult.Next;
    }

    internal static bool LooksLikeJson(string text)
    {
        var t = text.TrimStart();
        if (!t.StartsWith('{') && !t.StartsWith('[')) return false;
        try
        {
            using var _ = JsonDocument.Parse(text);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}

/// <summary>Script functions behind CPH's WebSocket methods (also usable from Cb.Call and Python).</summary>
public sealed class WebSocketScriptFunctions(ServerManager servers) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("websocket.broadcast", "Send data to every WebSocket API client as a General.Custom event (json=true parses it)", ["data", "json"],
            async (_, a, _) => (object?)await servers.WebSocket.BroadcastCustomAsync(a.GetString("data"), a.GetBool("json")).ConfigureAwait(false)),
        new("websocket.clientConnect", "Connect a custom client (index or name)", ["connection"], async (_, a, ct) =>
        {
            await servers.CustomClients.ConnectAsync(Client(a), ct).ConfigureAwait(false);
            return null;
        }),
        new("websocket.clientDisconnect", "Disconnect a custom client", ["connection"], async (_, a, _) =>
        {
            await servers.CustomClients.DisconnectAsync(Client(a).Id).ConfigureAwait(false);
            return null;
        }),
        new("websocket.clientIsConnected", "Whether a custom client is connected", ["connection"],
            (_, a, _) => Task.FromResult<object?>(servers.CustomClients.IsConnected(Client(a).Id))),
        new("websocket.clientSend", "Send through a custom client; returns whether it was sent", ["connection", "data"],
            async (_, a, ct) => (object?)await servers.CustomClients.SendAsync(Client(a).Id, a.GetString("data"), ct).ConfigureAwait(false)),
        new("websocket.clientIndex", "Index of a custom client by name (-1 if missing)", ["name"],
            (_, a, _) => Task.FromResult<object?>(servers.CustomClients.IndexOf(a.GetString("name")))),
        new("websocket.serverStart", "Start a custom server", ["connection"], async (_, a, ct) =>
        {
            await servers.CustomServers.StartAsync(Server(a), ct).ConfigureAwait(false);
            return null;
        }),
        new("websocket.serverStop", "Stop a custom server", ["connection"], async (_, a, _) =>
        {
            await servers.CustomServers.StopAsync(Server(a).Id).ConfigureAwait(false);
            return null;
        }),
        new("websocket.serverIsListening", "Whether a custom server is running", ["connection"],
            (_, a, _) => Task.FromResult<object?>(servers.CustomServers.IsListening(Server(a).Id))),
        new("websocket.serverBroadcast", "Send to a custom server's sessions (or one sessionId); returns how many got it",
            ["connection", "data", "sessionId"],
            async (_, a, _) => (object?)await servers.CustomServers.BroadcastAsync(Server(a).Id, a.GetString("data"), a.GetString("sessionId")).ConfigureAwait(false)),
        new("websocket.serverCloseSession", "Close one session of a custom server", ["connection", "sessionId"],
            async (_, a, _) => (object?)await servers.CustomServers.CloseSessionAsync(Server(a).Id, a.GetString("sessionId")).ConfigureAwait(false)),
        new("websocket.serverCloseAll", "Close every session of a custom server; returns how many", ["connection"],
            async (_, a, _) => (object?)await servers.CustomServers.CloseAllAsync(Server(a).Id).ConfigureAwait(false)),
        new("websocket.serverIndex", "Index of a custom server by name (-1 if missing)", ["name"],
            (_, a, _) => Task.FromResult<object?>(servers.CustomServers.IndexOf(a.GetString("name")))),
    ];

    private CustomWebSocketClientDefinition Client(JsonElement a) =>
        servers.CustomClients.Find(Connection(a)) ?? throw new ScriptException($"No WebSocket client '{Connection(a)}'");

    private CustomWebSocketServerDefinition Server(JsonElement a) =>
        servers.CustomServers.Find(Connection(a)) ?? throw new ScriptException($"No WebSocket server '{Connection(a)}'");

    private static string Connection(JsonElement a) => a.GetString("connection", "0");
}
