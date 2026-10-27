using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Cuebot.Obs.Protocol;

/// <summary>
/// One obs-websocket v5 session (RPC version 1). Subscribe to the events, call <see cref="ConnectAsync"/> once,
/// then send requests. A closed client cannot be reused; create a new one to reconnect.
/// </summary>
public sealed class ObsClient : IAsyncDisposable
{
    public const string SubProtocol = "obswebsocket.json";

    /// <summary>Every event category except the high-volume ones (volume meters, active state changes...).</summary>
    internal const int AllLowVolumeEventSubscriptions = (1 << 11) - 1;

    private const int RpcVersion = 1;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();
    private static readonly JsonElement EmptyArray = JsonDocument.Parse("[]").RootElement.Clone();

    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TimeSpan _requestTimeout;
    private ObsSocket? _socket;
    private Task? _receiveLoop;
    private volatile string? _closeReason;
    private int _connectStarted;
    private int _disposed;

    /// <param name="requestTimeout">How long to wait for each request's answer (default 10 s).</param>
    public ObsClient(TimeSpan? requestTimeout = null) => _requestTimeout = requestTimeout ?? TimeSpan.FromSeconds(10);

    /// <summary>Raised on the receive thread for every OBS event. Handlers must not block or throw.</summary>
    public event Action<ObsEvent>? EventReceived;

    /// <summary>Raised once, on the receive thread, when an identified session ends, with a human-readable reason.</summary>
    public event Action<string>? Disconnected;

    /// <summary>The obs-websocket version OBS announced in its Hello message, once connected.</summary>
    public string? ServerVersion { get; private set; }

    /// <summary>True between a successful handshake and the end of the session.</summary>
    public bool IsConnected => _receiveLoop is { IsCompleted: false } && _closeReason is null;

    /// <summary>Opens the WebSocket and completes the Hello / Identify / Identified handshake.</summary>
    /// <exception cref="ObsAuthenticationException">The password is wrong or missing.</exception>
    /// <exception cref="ObsConnectionException">OBS closed the connection, timed out or broke the protocol.</exception>
    public async Task ConnectAsync(Uri uri, string password, CancellationToken ct = default)
    {
        if (Interlocked.Exchange(ref _connectStarted, 1) == 1)
            throw new InvalidOperationException("An ObsClient can only connect once.");

        var webSocket = new ClientWebSocket();
        webSocket.Options.AddSubProtocol(SubProtocol);
        var socket = new ObsSocket(webSocket);
        _socket = socket;
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        handshake.CancelAfter(HandshakeTimeout);
        try
        {
            await webSocket.ConnectAsync(uri, handshake.Token).ConfigureAwait(false);
            ServerVersion = await IdentifyAsync(socket, password, handshake.Token).ConfigureAwait(false);
            if (_disposed == 1) throw new ObsConnectionException("Disconnected from OBS.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && _disposed == 0)
        {
            socket.Dispose();
            throw new ObsConnectionException("OBS did not complete the connection handshake in time.");
        }
        catch
        {
            socket.Dispose();
            throw;
        }
        _receiveLoop = Task.Run(() => ReceiveLoopAsync(socket));
    }

    /// <summary>Sends a request and returns its <c>responseData</c> (an empty object when OBS sends none).</summary>
    /// <param name="requestData">Serialized as-is with System.Text.Json; property names are sent verbatim.</param>
    /// <exception cref="ObsRequestException">OBS reported a failure status.</exception>
    /// <exception cref="ObsConnectionException">The session is not connected or ended before the answer.</exception>
    /// <exception cref="TimeoutException">OBS did not answer in time.</exception>
    public async Task<JsonElement> RequestAsync(string requestType, object? requestData = null, CancellationToken ct = default)
    {
        var socket = _socket;
        if (socket is null || !IsConnected) throw new ObsConnectionException(_closeReason ?? "Not connected to OBS.");

        var requestId = Guid.NewGuid().ToString("N");
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = response;
        try
        {
            // The session may have ended between the check above and registering; fail now instead of timing out.
            if (_closeReason is { } reason) throw new ObsConnectionException(reason);

            var payload = new Dictionary<string, object> { ["requestType"] = requestType, ["requestId"] = requestId };
            if (requestData is not null) payload["requestData"] = requestData;
            await socket.SendAsync(ObsOpCode.Request, payload, ct).ConfigureAwait(false);
            return await response.Task.WaitAsync(_requestTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"OBS did not answer '{requestType}' within {_requestTimeout.TotalSeconds:0.#} s.");
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            throw new ObsConnectionException(_closeReason ?? "The connection to OBS was lost.", ex);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// Sends a RequestBatch (op 8) and returns its <c>results</c> array: one object per request with
    /// <c>requestType</c>, <c>requestStatus</c> and optional <c>responseData</c>. Individual failures do not throw.
    /// </summary>
    /// <param name="requests">Each item is <c>{ "requestType": ..., "requestData": ... }</c>, serialized as-is.</param>
    /// <param name="haltOnFailure">Stop processing the batch at the first failed request.</param>
    public async Task<JsonElement> RequestBatchAsync(IReadOnlyList<object> requests, bool haltOnFailure = false,
        CancellationToken ct = default)
    {
        var socket = _socket;
        if (socket is null || !IsConnected) throw new ObsConnectionException(_closeReason ?? "Not connected to OBS.");

        var requestId = Guid.NewGuid().ToString("N");
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = response;
        try
        {
            if (_closeReason is { } reason) throw new ObsConnectionException(reason);
            var payload = new Dictionary<string, object>
            {
                ["requestId"] = requestId, ["haltOnFailure"] = haltOnFailure, ["requests"] = requests,
            };
            await socket.SendAsync(ObsOpCode.RequestBatch, payload, ct).ConfigureAwait(false);
            return await response.Task.WaitAsync(_requestTimeout * Math.Max(1, requests.Count), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException("OBS did not answer the request batch in time.");
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            throw new ObsConnectionException(_closeReason ?? "The connection to OBS was lost.", ex);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    /// <summary>Closes the session gracefully (aborting it if OBS does not answer) and waits for it to end.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) return;
        if (_receiveLoop is { } loop)
        {
            if (!loop.IsCompleted)
            {
                await _socket!.CloseOutputAsync().ConfigureAwait(false);
                _shutdown.CancelAfter(CloseTimeout);
            }
            await loop.ConfigureAwait(false);
        }
        else
        {
            // Still handshaking, or never connected: abort.
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }
        _shutdown.Dispose();
    }

    private static async Task<string?> IdentifyAsync(ObsSocket socket, string password, CancellationToken ct)
    {
        var hello = await ReceiveHandshakeMessageAsync(socket, ObsOpCode.Hello, ct).ConfigureAwait(false);
        var identify = new Dictionary<string, object>
        {
            ["rpcVersion"] = RpcVersion,
            ["eventSubscriptions"] = AllLowVolumeEventSubscriptions,
        };
        if (hello.Data.TryGetProperty("authentication", out var auth))
        {
            if (string.IsNullOrEmpty(password))
                throw new ObsAuthenticationException("OBS requires a password, but none is configured.");
            identify["authentication"] = ObsAuth.Compute(password,
                auth.GetProperty("salt").GetString() ?? "", auth.GetProperty("challenge").GetString() ?? "");
        }
        await socket.SendAsync(ObsOpCode.Identify, identify, ct).ConfigureAwait(false);
        await ReceiveHandshakeMessageAsync(socket, ObsOpCode.Identified, ct).ConfigureAwait(false);
        return hello.Data.TryGetProperty("obsWebSocketVersion", out var version) && version.ValueKind == JsonValueKind.String
            ? version.GetString()
            : null;
    }

    private static async Task<ObsMessage> ReceiveHandshakeMessageAsync(ObsSocket socket, ObsOpCode expected, CancellationToken ct)
    {
        var message = await socket.ReceiveAsync(ct).ConfigureAwait(false);
        if (message is null)
        {
            var reason = ObsCloseCodes.Describe(socket.CloseStatus, socket.CloseStatusDescription);
            throw (int?)socket.CloseStatus == ObsCloseCodes.AuthenticationFailed
                ? new ObsAuthenticationException(reason)
                : new ObsConnectionException(reason);
        }
        if (message.Op != expected)
            throw new ObsConnectionException($"Unexpected obs-websocket message (op {(int)message.Op}) while waiting for {expected}.");
        return message;
    }

    private async Task ReceiveLoopAsync(ObsSocket socket)
    {
        string reason;
        try
        {
            while (await socket.ReceiveAsync(_shutdown.Token).ConfigureAwait(false) is { } message) Dispatch(message);
            reason = _disposed == 1
                ? "Disconnected from OBS."
                : ObsCloseCodes.Describe(socket.CloseStatus, socket.CloseStatusDescription);
        }
        catch (Exception) when (_disposed == 1)
        {
            reason = "Disconnected from OBS.";
        }
        catch (Exception ex)
        {
            reason = $"The connection to OBS was lost: {ex.Message}";
        }

        _closeReason = reason;
        foreach (var requestId in _pending.Keys)
            if (_pending.TryRemove(requestId, out var pending)) pending.TrySetException(new ObsConnectionException(reason));
        socket.Dispose();
        Disconnected?.Invoke(reason);
    }

    private void Dispatch(ObsMessage message)
    {
        switch (message.Op)
        {
            case ObsOpCode.Event:
                EventReceived?.Invoke(new ObsEvent(
                    message.Data.GetProperty("eventType").GetString() ?? "",
                    message.Data.TryGetProperty("eventData", out var data) ? data : default));
                break;
            case ObsOpCode.RequestResponse:
                CompleteRequest(message.Data);
                break;
            case ObsOpCode.RequestBatchResponse:
                CompleteBatch(message.Data);
                break;
        }
    }

    private void CompleteBatch(JsonElement response)
    {
        var requestId = response.GetProperty("requestId").GetString() ?? "";
        if (!_pending.TryRemove(requestId, out var pending)) return;
        pending.TrySetResult(response.TryGetProperty("results", out var results) ? results : EmptyArray);
    }

    private void CompleteRequest(JsonElement response)
    {
        var requestId = response.GetProperty("requestId").GetString() ?? "";
        if (!_pending.TryRemove(requestId, out var pending)) return; // timed out or cancelled meanwhile

        var status = response.GetProperty("requestStatus");
        if (status.GetProperty("result").GetBoolean())
        {
            pending.TrySetResult(response.TryGetProperty("responseData", out var data) ? data : EmptyObject);
            return;
        }
        pending.TrySetException(new ObsRequestException(
            response.TryGetProperty("requestType", out var type) ? type.GetString() ?? "" : "",
            status.GetProperty("code").GetInt32(),
            status.TryGetProperty("comment", out var comment) ? comment.GetString() : null));
    }
}
