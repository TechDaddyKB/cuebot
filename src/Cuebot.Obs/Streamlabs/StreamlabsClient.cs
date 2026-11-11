using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Cuebot.Obs.Streamlabs;

/// <summary>The connection to Streamlabs Desktop failed, was lost, or a call failed.</summary>
public class StreamlabsException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Streamlabs Desktop rejected the API token.</summary>
public sealed class StreamlabsAuthenticationException(string message) : StreamlabsException(message);

/// <summary>
/// One session with the Streamlabs Desktop Remote Control API: JSON-RPC 2.0 over a SockJS websocket. Calls address a
/// resource ("ScenesService", or a model's <c>resourceId</c> such as <c>SceneItem["…"]</c>) and a method. Events
/// arrive for subscriptions made with <see cref="SubscribeAsync"/>. A closed client cannot be reused.
/// </summary>
public sealed class StreamlabsClient : IAsyncDisposable
{
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonElement Null = JsonDocument.Parse("null").RootElement.Clone();

    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();

    /// <summary>Promise results (by the promise's resourceId) still waiting for their completion EVENT.</summary>
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _promises = new(StringComparer.Ordinal);
    private readonly TimeSpan _promiseTimeout;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly TimeSpan _requestTimeout;
    private ClientWebSocket? _socket;
    private Task? _receiveLoop;
    private volatile string? _closeReason;
    private int _nextId;
    private volatile bool _authenticated;
    private int _disposed;

    public StreamlabsClient(TimeSpan? requestTimeout = null, TimeSpan? promiseTimeout = null)
    {
        _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);
        _promiseTimeout = promiseTimeout ?? TimeSpan.FromSeconds(30);
    }

    /// <summary>Raised on the receive thread for every subscribed event: (resourceId such as "ScenesService.sceneSwitched", data).</summary>
    public event Action<string, JsonElement>? EventReceived;

    /// <summary>Raised once, on the receive thread, when an authenticated session ends.</summary>
    public event Action<string>? Disconnected;

    public bool IsConnected => _receiveLoop is { IsCompleted: false } && _closeReason is null;

    /// <summary>Opens the SockJS session under <paramref name="apiUri"/> (ws://host:port/api) and authenticates.</summary>
    public async Task ConnectAsync(Uri apiUri, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new StreamlabsAuthenticationException("Streamlabs Desktop needs its API token (Settings → Remote Control → Show details).");
        var socket = new ClientWebSocket();
        _socket = socket;
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        handshake.CancelAfter(HandshakeTimeout);
        try
        {
            await socket.ConnectAsync(SockJsFraming.SessionUri(apiUri), handshake.Token).ConfigureAwait(false);
            var open = await ReceiveTextAsync(socket, handshake.Token).ConfigureAwait(false);
            if (open is null || SockJsFraming.Decode(open).Type != 'o')
                throw new StreamlabsException("Streamlabs Desktop did not open the session.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            socket.Dispose();
            throw new StreamlabsException("Streamlabs Desktop did not answer in time.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(socket));

        try
        {
            await CallAsync("TcpServerService", "auth", [token], ct).ConfigureAwait(false);
            _authenticated = true;
        }
        catch (StreamlabsException ex) when (ex is not StreamlabsAuthenticationException && _closeReason is null)
        {
            await DisposeAsync().ConfigureAwait(false);
            throw new StreamlabsAuthenticationException($"Streamlabs Desktop rejected the API token ({ex.Message}).");
        }
    }

    /// <summary>
    /// Calls <paramref name="method"/> on <paramref name="resource"/> and returns its <c>result</c>. A method that returns
    /// a promise (e.g. <c>toggleStreaming</c>) first answers <c>{_type: "SUBSCRIPTION", emitter: "PROMISE"}</c>; then this
    /// waits for the promise's EVENT and returns its data, or throws when <c>isRejected</c> is true. When no EVENT comes
    /// within the promise timeout the acknowledgement is returned (Streamlabs Desktop accepted the call).
    /// </summary>
    /// <exception cref="StreamlabsException">Not connected, the session ended, or Streamlabs Desktop returned an error.</exception>
    public async Task<JsonElement> CallAsync(string resource, string method, object?[]? args = null, CancellationToken ct = default)
    {
        var result = await CallCoreAsync(resource, method, args, ct).ConfigureAwait(false);
        if (!IsPromise(result, out var promiseId)) return result;
        if (!_promises.TryGetValue(promiseId, out var completion)) return result;
        try
        {
            return await completion.Task.WaitAsync(_promiseTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return result;
        }
        finally
        {
            _promises.TryRemove(promiseId, out _);
        }
    }

    private static bool IsPromise(JsonElement result, out string promiseId)
    {
        promiseId = "";
        if (result.ValueKind != JsonValueKind.Object || result.GetStringOrNull("_type") != "SUBSCRIPTION" ||
            result.GetStringOrNull("emitter") != "PROMISE") return false;
        promiseId = result.GetStringOrNull("resourceId") ?? "";
        return promiseId.Length > 0;
    }

    private async Task<JsonElement> CallCoreAsync(string resource, string method, object?[]? args, CancellationToken ct)
    {
        var socket = _socket;
        if (socket is null || _receiveLoop is null || _closeReason is not null)
            throw new StreamlabsException(_closeReason ?? "Not connected to Streamlabs Desktop.");

        var id = Interlocked.Increment(ref _nextId);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = response;
        try
        {
            if (_closeReason is { } reason) throw new StreamlabsException(reason);
            var request = JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0", id, method, @params = new { resource, args = args ?? [] },
            });
            await SendAsync(socket, SockJsFraming.Encode(request), ct).ConfigureAwait(false);
            return await response.Task.WaitAsync(_requestTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new StreamlabsException($"Streamlabs Desktop did not answer {resource}.{method} in time.");
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            throw new StreamlabsException(_closeReason ?? "The connection to Streamlabs Desktop was lost.", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Subscribes to an event such as ScenesService.sceneSwitched (<paramref name="resource"/>, <paramref name="eventName"/>).</summary>
    public Task SubscribeAsync(string resource, string eventName, CancellationToken ct = default) =>
        CallAsync(resource, eventName, null, ct);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        var socket = _socket;
        if (socket is not null && socket.State == WebSocketState.Open)
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Already gone.
            }
        }
        _shutdown.CancelAfter(TimeSpan.FromSeconds(3));
        if (_receiveLoop is { } loop) await loop.ConfigureAwait(false);
        else socket?.Dispose();
        _shutdown.Dispose();
    }

    private async Task SendAsync(ClientWebSocket socket, string text, CancellationToken ct)
    {
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    private async Task ReceiveLoopAsync(ClientWebSocket socket)
    {
        string reason;
        try
        {
            while (true)
            {
                var text = await ReceiveTextAsync(socket, _shutdown.Token).ConfigureAwait(false);
                if (text is null)
                {
                    reason = _disposed == 1 ? "Disconnected from Streamlabs Desktop." : "Streamlabs Desktop closed the connection.";
                    break;
                }
                var frame = SockJsFraming.Decode(text);
                if (frame.Type == 'c')
                {
                    reason = frame.CloseReason ?? "Streamlabs Desktop closed the connection.";
                    break;
                }
                foreach (var message in frame.Messages) Dispatch(message);
            }
        }
        catch (Exception) when (_disposed == 1)
        {
            reason = "Disconnected from Streamlabs Desktop.";
        }
        catch (Exception ex)
        {
            reason = $"The connection to Streamlabs Desktop was lost: {ex.Message}";
        }

        _closeReason = reason;
        foreach (var id in _pending.Keys)
            if (_pending.TryRemove(id, out var pending)) pending.TrySetException(new StreamlabsException(reason));
        foreach (var id in _promises.Keys)
            if (_promises.TryRemove(id, out var promise)) promise.TrySetException(new StreamlabsException(reason));
        socket.Dispose();
        if (_authenticated) Disconnected?.Invoke(reason);   // a failed handshake is reported by ConnectAsync instead
    }

    private void Dispatch(string message)
    {
        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(message);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;   // not JSON-RPC; ignore
        }
        if (root.ValueKind != JsonValueKind.Object) return;

        var result = root.TryGetProperty("result", out var r) ? r : Null;
        if (result.ValueKind == JsonValueKind.Object && result.GetStringOrNull("_type") == "EVENT")
        {
            var resourceId = result.GetStringOrNull("resourceId") ?? "";
            var data = result.TryGetProperty("data", out var d) ? d : Null;
            if (_promises.TryGetValue(resourceId, out var promise))
            {
                if (result.GetBoolOrNull("isRejected") == true) promise.TrySetException(new StreamlabsException(RejectionMessage(data)));
                else promise.TrySetResult(data);
                return;
            }
            EventReceived?.Invoke(resourceId, data);
            return;
        }
        if (!root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number ||
            !idElement.TryGetInt32(out var id)) return;
        if (!_pending.TryRemove(id, out var pending)) return;
        if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            pending.TrySetException(new StreamlabsException(error.GetStringOrNull("message") ?? "Streamlabs Desktop returned an error."));
            return;
        }
        // Registered here, on the receive thread, so the promise's EVENT (which may follow at once) finds it.
        if (IsPromise(result, out var promiseId))
            _promises.TryAdd(promiseId, new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously));
        pending.TrySetResult(result);
    }

    private static string RejectionMessage(JsonElement data) => data.ValueKind switch
    {
        JsonValueKind.String => data.GetString() ?? "Streamlabs Desktop rejected the call.",
        JsonValueKind.Object => data.GetStringOrNull("message") ?? data.GetStringOrNull("error") ?? $"Streamlabs Desktop rejected the call: {data.GetRawText()}",
        _ => "Streamlabs Desktop rejected the call.",
    };
}
