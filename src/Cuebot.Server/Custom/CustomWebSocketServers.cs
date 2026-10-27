using System.Collections.Concurrent;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Server.Hosting;

namespace Cuebot.Server.Custom;

/// <summary>Event types of user-defined WebSocket servers and clients (forwarded as WebsocketCustomServer.* / WebsocketClient.*).</summary>
public static class CustomWebSocketEvents
{
    /// <summary>Args: serverName, serverIndex, serverId, sessionId, remote.</summary>
    public const string ServerOpen = "websocket.serverOpen";

    /// <summary>Args: as <see cref="ServerOpen"/> plus message.</summary>
    public const string ServerMessage = "websocket.serverMessage";

    public const string ServerClose = "websocket.serverClose";

    /// <summary>Args: clientName, clientIndex, clientId, url.</summary>
    public const string ClientOpen = "websocket.clientOpen";

    /// <summary>Args: as <see cref="ClientOpen"/> plus message.</summary>
    public const string ClientMessage = "websocket.clientMessage";

    /// <summary>Args: as <see cref="ClientOpen"/> plus reason.</summary>
    public const string ClientClose = "websocket.clientClose";
}

/// <summary>Runs the user's custom WebSocket servers (servers.json "customServers"), addressed by index or name.</summary>
public sealed class CustomWebSocketServers(Func<ServerSettings> settings, EventBus bus, AppLog log) : IAsyncDisposable
{
    private const string LogSource = "WebSocket servers";
    private readonly ConcurrentDictionary<Guid, WebSocketEndpoint> _running = new();
    private readonly SemaphoreSlim _gate = new(1, 1);

    public event Action? Changed;

    public IReadOnlyList<CustomWebSocketServerDefinition> Definitions => settings().CustomServers;

    public bool IsListening(Guid id) => _running.ContainsKey(id);

    public int ClientCount(Guid id) => _running.TryGetValue(id, out var e) ? e.Connections.Count : 0;

    public int Port(Guid id) => _running.TryGetValue(id, out var e) ? e.Port : 0;

    /// <summary>The definition for an index ("0"), id or name; null when none matches.</summary>
    public CustomWebSocketServerDefinition? Find(string connection) => Lookup(Definitions, connection, d => d.Id, d => d.Name);

    public int IndexOf(string name)
    {
        var list = Definitions;
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    public async Task StartAsync(CustomWebSocketServerDefinition server, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_running.ContainsKey(server.Id)) return;
            var address = NetworkPolicy.BindAddress(server.Address, settings().AllowLan, out var downgraded);
            if (downgraded) log.Warn(LogSource, $"'{server.Name}' listens on 127.0.0.1: LAN access is off");
            var handler = new Handler(this, server);
            var endpoint = await WebSocketEndpoint.StartAsync(address, server.Port, server.Endpoint, handler,
                () => settings().AllowedOrigins, ct).ConfigureAwait(false);
            endpoint.ConnectionsChanged += () => Changed?.Invoke();
            _running[server.Id] = endpoint;
            log.Info(LogSource, $"'{server.Name}' listening on {address}:{endpoint.Port}{NetworkPolicy.NormalizePath(server.Endpoint)}");
        }
        finally
        {
            _gate.Release();
        }
        Changed?.Invoke();
    }

    public async Task StopAsync(Guid id)
    {
        if (!_running.TryRemove(id, out var endpoint)) return;
        await endpoint.DisposeAsync().ConfigureAwait(false);
        Changed?.Invoke();
    }

    /// <summary>Starts every server marked AutoStart; failures are logged.</summary>
    public async Task StartAutoAsync(CancellationToken ct = default)
    {
        foreach (var server in Definitions.Where(d => d.AutoStart).ToArray())
        {
            try
            {
                await StartAsync(server, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Error(LogSource, $"'{server.Name}' could not start on port {server.Port}", ex);
            }
        }
    }

    /// <summary>Sends to every session, or one session. Returns how many received it.</summary>
    public async Task<int> BroadcastAsync(Guid id, string data, string? sessionId = null)
    {
        if (!_running.TryGetValue(id, out var endpoint)) throw new InvalidOperationException("That WebSocket server is not running");
        return await endpoint.BroadcastAsync(data, c => string.IsNullOrEmpty(sessionId) || c.Id == sessionId).ConfigureAwait(false);
    }

    public async Task<bool> CloseSessionAsync(Guid id, string sessionId)
    {
        if (!_running.TryGetValue(id, out var endpoint) || endpoint.Find(sessionId) is not { } connection) return false;
        await connection.CloseAsync().ConfigureAwait(false);
        return true;
    }

    public async Task<int> CloseAllAsync(Guid id)
    {
        if (!_running.TryGetValue(id, out var endpoint)) return 0;
        var all = endpoint.Connections;
        await Task.WhenAll(all.Select(c => c.CloseAsync())).ConfigureAwait(false);
        return all.Count;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _running.Keys.ToArray()) await StopAsync(id).ConfigureAwait(false);
        _gate.Dispose();
    }

    internal static T? Lookup<T>(IReadOnlyList<T> list, string connection, Func<T, Guid> id, Func<T, string> name) where T : class
    {
        var key = connection.Trim();
        if (int.TryParse(key, out var index)) return index >= 0 && index < list.Count ? list[index] : null;
        if (Guid.TryParse(key, out var guid)) return list.FirstOrDefault(d => id(d) == guid);
        return list.FirstOrDefault(d => string.Equals(name(d), key, StringComparison.OrdinalIgnoreCase));
    }

    private Dictionary<string, object?> Args(CustomWebSocketServerDefinition server, WebSocketConnection connection)
    {
        var index = Definitions.ToList().FindIndex(d => d.Id == server.Id);
        return new Dictionary<string, object?>
        {
            ["serverName"] = server.Name,
            ["serverIndex"] = index,
            ["serverId"] = server.Id.ToString(),
            ["sessionId"] = connection.Id,
            ["remote"] = connection.Remote,
        };
    }

    private sealed class Handler(CustomWebSocketServers owner, CustomWebSocketServerDefinition server) : IWebSocketHandler
    {
        public Task OnOpenAsync(WebSocketConnection connection)
        {
            owner.Publish(CustomWebSocketEvents.ServerOpen, owner.Args(server, connection));
            return Task.CompletedTask;
        }

        public Task OnMessageAsync(WebSocketConnection connection, string message)
        {
            var args = owner.Args(server, connection);
            args["message"] = message;
            owner.Publish(CustomWebSocketEvents.ServerMessage, args);
            return Task.CompletedTask;
        }

        public void OnClose(WebSocketConnection connection) =>
            owner.Publish(CustomWebSocketEvents.ServerClose, owner.Args(server, connection));
    }

    private void Publish(string type, Dictionary<string, object?> args) => bus.Publish(new BotEvent(type, args));
}
