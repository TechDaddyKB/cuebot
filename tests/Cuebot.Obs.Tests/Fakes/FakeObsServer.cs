using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Cuebot.Obs.Protocol;

namespace Cuebot.Obs.Tests.Fakes;

/// <summary>How the fake server answers one request. A null <see cref="FakeReply"/> means "never answer".</summary>
internal sealed record FakeReply(bool Success = true, object? Data = null, int Code = 100, string? Comment = null)
{
    public TimeSpan Delay { get; init; }

    public static FakeReply Ok(object? data = null) => new(Data: data);
    public static FakeReply Fail(int code, string comment) => new(false, null, code, comment);
}

/// <summary>
/// In-process obs-websocket v5 server on http://localhost:&lt;free port&gt;/. Implements the handshake (with
/// optional password), answers requests through <see cref="Handler"/> and can push events to the session.
/// </summary>
internal sealed class FakeObsServer : IAsyncDisposable
{
    public const string Salt = "lM1GncleQOaCu9lT1yeUZhFYnqhsLLP1G5lAGo3ixaI=";
    public const string Challenge = "+IxH4CnCiqpX1rM9scsNynZzbOe4KhDeYcTNS3PDaeY=";

    private readonly HttpListener _listener;
    private readonly string? _password;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptLoop;
    private TaskCompletionSource<WebSocket> _identified = NewSessionSource();

    private FakeObsServer(string? password)
    {
        _password = password;
        // Another test can take the probed port before it is bound, so try a few.
        for (var attempt = 1; ; attempt++)
        {
            Port = GetFreePort();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            try { _listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 5) { _listener.Close(); }
        }
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }
    public Uri Uri => new($"ws://localhost:{Port}/");

    /// <summary>Answers requests; the default succeeds with no response data.</summary>
    public Func<string, JsonElement, FakeReply?> Handler { get; set; } = (_, _) => FakeReply.Ok();

    public ConcurrentQueue<(string Type, JsonElement Data)> Requests { get; } = new();

    /// <summary>The "d" payload of the most recent Identify message.</summary>
    public JsonElement? LastIdentify { get; private set; }

    public int SessionCount { get; private set; }

    public static FakeObsServer Start(string? password = null) => new(password);

    /// <summary>Waits until a client has completed the handshake.</summary>
    public Task WaitForSessionAsync() => _identified.Task.WaitAsync(TimeSpan.FromSeconds(10));

    public async Task SendEventAsync(string eventType, object? eventData = null)
    {
        var session = await _identified.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await SendAsync(session, 5, new { eventType, eventIntent = 1, eventData });
    }

    /// <summary>Closes the current session from the server side, as OBS does when it shuts down.</summary>
    public async Task CloseSessionAsync()
    {
        var session = await _identified.Task.WaitAsync(TimeSpan.FromSeconds(10));
        _identified = NewSessionSource();
        await TryCloseAsync(session, WebSocketCloseStatus.NormalClosure, "Server stopping");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Close();
        try { await _acceptLoop; } catch (Exception) { /* listener closed */ }
        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) { return; }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }
            var webSocket = (await context.AcceptWebSocketAsync(ObsClient.SubProtocol)).WebSocket;
            _ = Task.Run(() => RunSessionAsync(webSocket));
        }
    }

    private async Task RunSessionAsync(WebSocket session)
    {
        try
        {
            object hello = _password is null
                ? new { obsWebSocketVersion = "5.0.0", rpcVersion = 1 }
                : new { obsWebSocketVersion = "5.0.0", rpcVersion = 1, authentication = new { challenge = Challenge, salt = Salt } };
            await SendAsync(session, 0, hello);

            var identify = await ReceiveAsync(session);
            if (identify is null) return;
            LastIdentify = identify.Value.GetProperty("d").Clone();
            if (_password is not null && !IsAuthenticated(LastIdentify.Value))
            {
                await TryCloseAsync(session, (WebSocketCloseStatus)4009, "Authentication failed.");
                return;
            }
            await SendAsync(session, 2, new { negotiatedRpcVersion = 1 });
            SessionCount++;
            _identified.TrySetResult(session);

            while (await ReceiveAsync(session) is { } message)
            {
                var op = message.GetProperty("op").GetInt32();
                if (op == 6) _ = Task.Run(() => AnswerAsync(session, message.GetProperty("d").Clone()));
                else if (op == 8) _ = Task.Run(() => AnswerBatchAsync(session, message.GetProperty("d").Clone()));
            }
        }
        catch (Exception)
        {
            // The client went away; the test observes that on its side.
        }
    }

    private bool IsAuthenticated(JsonElement identify) =>
        identify.TryGetProperty("authentication", out var auth) &&
        auth.GetString() == ObsAuth.Compute(_password!, Salt, Challenge);

    /// <summary>RequestBatch (op 8): answers each request through <see cref="Handler"/> in order, then sends op 9.</summary>
    private async Task AnswerBatchAsync(WebSocket session, JsonElement batch)
    {
        var halt = batch.TryGetProperty("haltOnFailure", out var h) && h.GetBoolean();
        var results = new List<object>();
        foreach (var request in batch.GetProperty("requests").EnumerateArray())
        {
            var requestType = request.GetProperty("requestType").GetString()!;
            var data = request.TryGetProperty("requestData", out var d) ? d : default;
            Requests.Enqueue((requestType, data));
            var reply = Handler(requestType, data) ?? FakeReply.Fail(204, "No reply");
            var status = new { result = reply.Success, code = reply.Code, comment = reply.Comment };
            results.Add(reply.Data is null ? new { requestType, requestStatus = status } : new { requestType, requestStatus = status, responseData = reply.Data });
            if (halt && !reply.Success) break;
        }
        await SendAsync(session, 9, new { requestId = batch.GetProperty("requestId").GetString(), results });
    }

    private async Task AnswerAsync(WebSocket session, JsonElement request)
    {
        var requestType = request.GetProperty("requestType").GetString()!;
        var requestId = request.GetProperty("requestId").GetString()!;
        var data = request.TryGetProperty("requestData", out var d) ? d : default;
        Requests.Enqueue((requestType, data));

        if (Handler(requestType, data) is not { } reply) return;
        if (reply.Delay > TimeSpan.Zero) await Task.Delay(reply.Delay);
        var status = new { result = reply.Success, code = reply.Code, comment = reply.Comment };
        await SendAsync(session, 7, reply.Data is null
            ? new { requestType, requestId, requestStatus = status }
            : new { requestType, requestId, requestStatus = status, responseData = reply.Data });
    }

    private async Task SendAsync(WebSocket session, int op, object data)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { op, d = data });
        await _sendLock.WaitAsync();
        try { await session.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { _sendLock.Release(); }
    }

    private static async Task<JsonElement?> ReceiveAsync(WebSocket session)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await session.ReceiveAsync(buffer, CancellationToken.None);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await TryCloseAsync(session, WebSocketCloseStatus.NormalClosure, "Bye");
                return null;
            }
            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage) return JsonDocument.Parse(Encoding.UTF8.GetString(message.ToArray())).RootElement.Clone();
        }
    }

    private static async Task TryCloseAsync(WebSocket session, WebSocketCloseStatus status, string description)
    {
        try
        {
            if (session.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await session.CloseOutputAsync(status, description, CancellationToken.None);
        }
        catch (Exception)
        {
            // Already closed by the client.
        }
    }

    private static TaskCompletionSource<WebSocket> NewSessionSource() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }
}
