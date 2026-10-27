using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Cuebot.Server.Tests;

/// <summary>A test WebSocket client that collects every received JSON message.</summary>
internal sealed class WsClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly Channel<JsonObject> _messages = Channel.CreateUnbounded<JsonObject>();
    private readonly List<JsonObject> _backlog = [];
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;
    private int _nextId;

    public static async Task<WsClient> ConnectAsync(int port, string path = "/", string? origin = null)
    {
        var client = new WsClient();
        if (origin is not null) client._socket.Options.SetRequestHeader("Origin", origin);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client._socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{path}"), timeout.Token);
        client._loop = Task.Run(client.ReceiveLoopAsync);
        return client;
    }

    public WebSocketState State => _socket.State;

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(buffer, _stop.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(message.ToArray());
                message.SetLength(0);
                JsonNode? node;
                try { node = JsonNode.Parse(text); }
                catch (System.Text.Json.JsonException) { continue; }
                if (node is JsonObject obj) await _messages.Writer.WriteAsync(obj);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
        _messages.Writer.TryComplete();
    }

    public Task SendRawAsync(string text) =>
        _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Sends <c>{request, id, ...fields}</c> and returns the response with that id.</summary>
    public async Task<JsonObject> RequestAsync(string request, object? fields = null)
    {
        var id = "req-" + Interlocked.Increment(ref _nextId);
        var message = fields is null ? new JsonObject() : JsonSerializer.SerializeToNode(fields)!.AsObject();
        message["request"] = request;
        message["id"] = id;
        await SendRawAsync(message.ToJsonString());
        return await NextAsync(m => m["id"]?.GetValue<string>() == id);
    }

    /// <summary>The next message matching <paramref name="match"/> (earlier non-matching ones are kept).</summary>
    public async Task<JsonObject> NextAsync(Func<JsonObject, bool>? match = null, TimeSpan? timeout = null)
    {
        lock (_backlog)
        {
            var found = _backlog.FirstOrDefault(m => match?.Invoke(m) ?? true);
            if (found is not null)
            {
                _backlog.Remove(found);
                return found;
            }
        }
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        while (true)
        {
            var message = await _messages.Reader.ReadAsync(cts.Token);
            if (match?.Invoke(message) ?? true) return message;
            lock (_backlog) _backlog.Add(message);
        }
    }

    /// <summary>True when no matching message arrives within <paramref name="wait"/>.</summary>
    public async Task<bool> NothingAsync(Func<JsonObject, bool> match, TimeSpan wait)
    {
        try
        {
            await NextAsync(match, wait);
            return false;
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    public static Func<JsonObject, bool> IsEvent(string source, string type) =>
        m => m["event"]?["source"]?.GetValue<string>() == source && m["event"]?["type"]?.GetValue<string>() == type;

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }
        _stop.Cancel();
        if (_loop is not null) await _loop;
        _socket.Dispose();
        _stop.Dispose();
    }
}
