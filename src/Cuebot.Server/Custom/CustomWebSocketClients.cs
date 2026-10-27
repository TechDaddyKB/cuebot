using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Server.Custom;

/// <summary>
/// The user's outgoing WebSocket connections (servers.json "customClients"), addressed by index or name. Each connected
/// client runs a receive loop; with AutoReconnect a dropped connection is retried every ReconnectSeconds.
/// </summary>
public sealed class CustomWebSocketClients(Func<ServerSettings> settings, EventBus bus, AppLog log) : IAsyncDisposable
{
    private const string LogSource = "WebSocket clients";
    private readonly ConcurrentDictionary<Guid, Connection> _connections = new();
    private readonly EventBus _bus = bus;
    private readonly AppLog _log = log;

    public event Action? Changed;

    public IReadOnlyList<CustomWebSocketClientDefinition> Definitions => settings().CustomClients;

    public CustomWebSocketClientDefinition? Find(string connection) =>
        CustomWebSocketServers.Lookup(Definitions, connection, d => d.Id, d => d.Name);

    public int IndexOf(string name)
    {
        var list = Definitions;
        for (var i = 0; i < list.Count; i++)
        {
            if (string.Equals(list[i].Name, name, StringComparison.OrdinalIgnoreCase)) return i;
        }
        return -1;
    }

    public ConnectionState State(Guid id) => _connections.TryGetValue(id, out var c) ? c.State : ConnectionState.Disconnected;

    public bool IsConnected(Guid id) => State(id) == ConnectionState.Connected;

    /// <summary>Starts connecting (returns once the first attempt finished). Does nothing when already started.</summary>
    public async Task ConnectAsync(CustomWebSocketClientDefinition client, CancellationToken ct = default)
    {
        // A connection whose loop has ended (no auto-reconnect) is replaced.
        if (_connections.TryGetValue(client.Id, out var old) && old.State == ConnectionState.Disconnected
            && _connections.TryRemove(new KeyValuePair<Guid, Connection>(client.Id, old)))
        {
            await old.DisposeAsync().ConfigureAwait(false);
        }
        var connection = new Connection(this, client);
        if (!_connections.TryAdd(client.Id, connection))
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            return;
        }
        await connection.StartAsync(ct).ConfigureAwait(false);
    }

    public async Task DisconnectAsync(Guid id)
    {
        if (_connections.TryRemove(id, out var connection)) await connection.DisposeAsync().ConfigureAwait(false);
        Changed?.Invoke();
    }

    public async Task<bool> SendAsync(Guid id, string data, CancellationToken ct = default) =>
        _connections.TryGetValue(id, out var connection) && await connection.SendAsync(data, ct).ConfigureAwait(false);

    public async Task ConnectAutoAsync(CancellationToken ct = default)
    {
        foreach (var client in Definitions.Where(d => d.AutoConnect).ToArray()) await ConnectAsync(client, ct).ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var id in _connections.Keys.ToArray()) await DisconnectAsync(id).ConfigureAwait(false);
    }

    private Dictionary<string, object?> Args(CustomWebSocketClientDefinition client) => new()
    {
        ["clientName"] = client.Name,
        ["clientIndex"] = Definitions.ToList().FindIndex(d => d.Id == client.Id),
        ["clientId"] = client.Id.ToString(),
        ["url"] = client.Url,
    };

    private sealed class Connection(CustomWebSocketClients owner, CustomWebSocketClientDefinition client) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private ClientWebSocket? _socket;
        private Task? _loop;

        public ConnectionState State { get; private set; } = ConnectionState.Connecting;

        public async Task StartAsync(CancellationToken ct)
        {
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _loop = Task.Run(() => RunAsync(first));
            using var registration = ct.Register(() => first.TrySetCanceled());
            await first.Task.ConfigureAwait(false);
        }

        private async Task RunAsync(TaskCompletionSource first)
        {
            var ct = _stop.Token;
            while (!ct.IsCancellationRequested)
            {
                var opened = false;
                try
                {
                    using var socket = new ClientWebSocket();
                    _socket = socket;
                    SetState(ConnectionState.Connecting);
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(10));
                        await socket.ConnectAsync(new Uri(client.Url), timeout.Token).ConfigureAwait(false);
                    }
                    opened = true;
                    SetState(ConnectionState.Connected);
                    owner._log.Info(LogSource, $"'{client.Name}' connected to {client.Url}");
                    owner._bus.Publish(new BotEvent(CustomWebSocketEvents.ClientOpen, owner.Args(client)));
                    first.TrySetResult();
                    await ReceiveAsync(socket, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // Disconnect requested.
                }
                catch (Exception ex) when (ex is WebSocketException or IOException or UriFormatException or ArgumentException
                                               or OperationCanceledException or InvalidOperationException)
                {
                    if (!opened) owner._log.Warn(LogSource, $"'{client.Name}' could not connect to {client.Url}: {ex.Message}");
                    SetState(ConnectionState.Error);
                }
                finally
                {
                    _socket = null;
                }

                if (opened)
                {
                    var args = owner.Args(client);
                    args["reason"] = ct.IsCancellationRequested ? "disconnected" : "closed";
                    owner._bus.Publish(new BotEvent(CustomWebSocketEvents.ClientClose, args));
                }
                first.TrySetResult();
                if (ct.IsCancellationRequested || !client.AutoReconnect) break;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, client.ReconnectSeconds)), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
            SetState(ConnectionState.Disconnected);
        }

        private async Task ReceiveAsync(ClientWebSocket socket, CancellationToken ct)
        {
            var buffer = new byte[16 * 1024];
            using var message = new MemoryStream();
            while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
            {
                var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (message.Length > Hosting.WebSocketConnection.MaxMessageBytes) throw new InvalidOperationException("Message too big");
                if (!result.EndOfMessage) continue;
                var args = owner.Args(client);
                args["message"] = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                owner._bus.Publish(new BotEvent(CustomWebSocketEvents.ClientMessage, args));
            }
        }

        public async Task<bool> SendAsync(string data, CancellationToken ct)
        {
            var socket = _socket;
            if (socket is null || socket.State != WebSocketState.Open) return false;
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(Encoding.UTF8.GetBytes(data), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException)
            {
                return false;
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private void SetState(ConnectionState state)
        {
            if (State == state) return;
            State = state;
            owner.Changed?.Invoke();
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync().ConfigureAwait(false);
            if (_socket is { State: WebSocketState.Open } socket)
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
                {
                }
            }
            if (_loop is not null)
            {
                try { await _loop.ConfigureAwait(false); }
                catch (OperationCanceledException) { }
            }
            _stop.Dispose();
            _sendLock.Dispose();
        }
    }
}
