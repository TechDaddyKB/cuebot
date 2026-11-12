using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;

namespace Cuebot.Obs.Protocol;

/// <summary>How OBS runs the requests of a batch (obs-websocket <c>RequestBatchExecutionType</c>).</summary>
public enum ObsBatchExecution
{
    /// <summary>One after the other, as fast as possible (the default). <c>Sleep</c> uses <c>sleepMillis</c>.</summary>
    SerialRealtime = 0,

    /// <summary>One after the other in step with OBS's video frames: changes in one frame land together. <c>Sleep</c> uses <c>sleepFrames</c>.</summary>
    SerialFrame = 1,

    /// <summary>All at once on separate threads; order is not kept and <c>Sleep</c> is not allowed.</summary>
    Parallel = 2,
}

/// <summary>
/// One obs-websocket v5 session (RPC version 1). Subscribe to the events, call <see cref="ConnectAsync"/> once,
/// then send requests. A closed client cannot be reused; create a new one to reconnect.
/// </summary>
public sealed class ObsClient : IAsyncDisposable
{
    public const string SubProtocol = "obswebsocket.json";

    /// <summary>
    /// Every low-volume event category (General ... Ui, plus Canvases on OBS 32.1+; older servers ignore the unknown
    /// bit). The high-volume ones (volume meters, active/show state, transforms) are added on demand.
    /// </summary>
    internal const int AllLowVolumeEventSubscriptions = (1 << 12) - 1;

    /// <summary>High-volume subscription bits (obs-websocket <c>EventSubscription</c>).</summary>
    public const int InputVolumeMetersSubscription = 1 << 16;
    public const int InputActiveStateChangedSubscription = 1 << 17;
    public const int InputShowStateChangedSubscription = 1 << 18;
    public const int SceneItemTransformChangedSubscription = 1 << 19;

    private const int RpcVersion = 1;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(3);

    /// <summary>
    /// Requests that block in OBS until a long job is done (switching or creating a scene collection or profile loads
    /// every source), so they get more time than the normal request timeout.
    /// </summary>
    internal static readonly TimeSpan BlockingRequestTimeout = TimeSpan.FromSeconds(60);

    private static readonly HashSet<string> BlockingRequests = new(StringComparer.Ordinal)
    {
        "SetCurrentSceneCollection", "CreateSceneCollection", "SetCurrentProfile", "CreateProfile", "RemoveProfile",
    };

    /// <summary>Worst case for <c>Sleep.sleepFrames</c>: OBS running at 20 fps.</summary>
    private static readonly TimeSpan SlowestFrame = TimeSpan.FromMilliseconds(50);

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

    /// <summary>The RPC version OBS agreed to in Identified (always 1 today).</summary>
    public int NegotiatedRpcVersion { get; private set; }

    /// <summary>The event subscription bits currently in effect (set at Identify, changed by <see cref="ReidentifyAsync"/>).</summary>
    public int EventSubscriptions { get; private set; } = AllLowVolumeEventSubscriptions;

    /// <summary>True between a successful handshake and the end of the session.</summary>
    public bool IsConnected => _receiveLoop is { IsCompleted: false } && _closeReason is null;

    /// <summary>Opens the WebSocket and completes the Hello / Identify / Identified handshake.</summary>
    /// <exception cref="ObsAuthenticationException">The password is wrong or missing.</exception>
    /// <exception cref="ObsConnectionException">OBS closed the connection, timed out or broke the protocol.</exception>
    /// <param name="eventSubscriptions">Subscription bits to identify with; default every low-volume category.</param>
    public async Task ConnectAsync(Uri uri, string password, CancellationToken ct = default, int? eventSubscriptions = null)
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
            EventSubscriptions = eventSubscriptions ?? AllLowVolumeEventSubscriptions;
            (ServerVersion, NegotiatedRpcVersion) = await IdentifyAsync(socket, password, EventSubscriptions, handshake.Token)
                .ConfigureAwait(false);
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
    /// <param name="timeout">How long to wait for the answer; default the client's request timeout, or 60 s for
    /// requests that block in OBS (switching or creating scene collections and profiles).</param>
    public async Task<JsonElement> RequestAsync(string requestType, object? requestData = null, CancellationToken ct = default,
        TimeSpan? timeout = null)
    {
        var wait = timeout ?? TimeoutFor(requestType);
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
            return await response.Task.WaitAsync(wait, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"OBS did not answer '{requestType}' within {wait.TotalSeconds:0.#} s.");
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
    /// <param name="executionType">How OBS runs the batch; only sent when it is not the default (SerialRealtime).</param>
    public async Task<JsonElement> RequestBatchAsync(IReadOnlyList<object> requests, bool haltOnFailure = false,
        CancellationToken ct = default, ObsBatchExecution executionType = ObsBatchExecution.SerialRealtime)
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
            if (executionType != ObsBatchExecution.SerialRealtime) payload["executionType"] = (int)executionType;
            await socket.SendAsync(ObsOpCode.RequestBatch, payload, ct).ConfigureAwait(false);
            return await response.Task.WaitAsync(BatchTimeout(requests), ct).ConfigureAwait(false);
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

    /// <summary>
    /// Changes the event subscriptions of the running session (Reidentify, op 3), e.g. to add the high-volume
    /// <c>InputVolumeMeters</c> only while a trigger needs it. OBS sends no answer.
    /// </summary>
    public async Task ReidentifyAsync(int eventSubscriptions, CancellationToken ct = default)
    {
        var socket = _socket;
        if (socket is null || !IsConnected) throw new ObsConnectionException(_closeReason ?? "Not connected to OBS.");
        try
        {
            await socket.SendAsync(ObsOpCode.Reidentify, new Dictionary<string, object> { ["eventSubscriptions"] = eventSubscriptions }, ct)
                .ConfigureAwait(false);
            EventSubscriptions = eventSubscriptions;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException)
        {
            throw new ObsConnectionException(_closeReason ?? "The connection to OBS was lost.", ex);
        }
    }

    private TimeSpan TimeoutFor(string requestType) =>
        BlockingRequests.Contains(requestType) && BlockingRequestTimeout > _requestTimeout ? BlockingRequestTimeout : _requestTimeout;

    /// <summary>
    /// The time a batch may take: each request's own timeout plus every <c>Sleep</c> in it (up to 50 s each), so a
    /// long sleep does not make Cuebot give up while OBS is still running the batch.
    /// </summary>
    internal TimeSpan BatchTimeout(IReadOnlyList<object> requests)
    {
        var total = TimeSpan.Zero;
        foreach (var request in requests)
        {
            JsonElement element;
            try
            {
                element = request is JsonElement json ? json : JsonSerializer.SerializeToElement(request);
            }
            catch (NotSupportedException)
            {
                total += _requestTimeout;
                continue;
            }
            var type = element.ValueKind == JsonValueKind.Object && element.TryGetProperty("requestType", out var t) &&
                       t.ValueKind == JsonValueKind.String ? t.GetString() ?? "" : "";
            total += TimeoutFor(type);
            if (type != "Sleep" || !element.TryGetProperty("requestData", out var data) || data.ValueKind != JsonValueKind.Object) continue;
            if (data.TryGetProperty("sleepMillis", out var ms) && ms.TryGetDouble(out var millis))
                total += TimeSpan.FromMilliseconds(Math.Clamp(millis, 0, 50_000));
            if (data.TryGetProperty("sleepFrames", out var frames) && frames.TryGetDouble(out var count))
                total += SlowestFrame * Math.Clamp(count, 0, 10_000);
        }
        return total > TimeSpan.Zero ? total : _requestTimeout;
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

    private static async Task<(string? Version, int Rpc)> IdentifyAsync(ObsSocket socket, string password, int eventSubscriptions,
        CancellationToken ct)
    {
        var hello = await ReceiveHandshakeMessageAsync(socket, ObsOpCode.Hello, ct).ConfigureAwait(false);
        var identify = new Dictionary<string, object>
        {
            ["rpcVersion"] = RpcVersion,
            ["eventSubscriptions"] = eventSubscriptions,
        };
        if (hello.Data.TryGetProperty("authentication", out var auth))
        {
            if (string.IsNullOrEmpty(password))
                throw new ObsAuthenticationException("OBS requires a password, but none is configured.");
            identify["authentication"] = ObsAuth.Compute(password,
                auth.GetProperty("salt").GetString() ?? "", auth.GetProperty("challenge").GetString() ?? "");
        }
        await socket.SendAsync(ObsOpCode.Identify, identify, ct).ConfigureAwait(false);
        var identified = await ReceiveHandshakeMessageAsync(socket, ObsOpCode.Identified, ct).ConfigureAwait(false);
        // Only RPC 1 exists; a server that agrees to anything else speaks a protocol Cuebot does not know.
        var rpc = identified.Data.ValueKind == JsonValueKind.Object && identified.Data.TryGetProperty("negotiatedRpcVersion", out var n) &&
                  n.TryGetInt32(out var negotiated) ? negotiated : RpcVersion;
        if (rpc != RpcVersion)
            throw new ObsConnectionException(
                $"OBS agreed to obs-websocket RPC version {rpc}, but Cuebot only speaks version {RpcVersion}. Update Cuebot.");
        var version = hello.Data.TryGetProperty("obsWebSocketVersion", out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
        return (version, rpc);
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
