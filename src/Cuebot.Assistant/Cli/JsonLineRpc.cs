using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace Cuebot.Assistant.Cli;

/// <summary>A JSON-RPC message from the other side: a notification (no id) or a request we must answer.</summary>
public sealed record RpcIncoming(string Method, JsonObject Params, JsonNode? Id)
{
    public bool IsRequest => Id is not null;
}

/// <summary>The other side answered a request with an error.</summary>
public sealed class RpcException(string method, string message) : Exception($"{method}: {message}");

/// <summary>
/// JSON-RPC 2.0 over newline-delimited JSON (Codex app-server, Agent Client Protocol agents). Requests are matched by
/// id; notifications and the other side's requests arrive in <see cref="Incoming"/> in order. Works over any pair of
/// streams, so tests drive it without a process.
/// </summary>
public sealed class JsonLineRpc : IAsyncDisposable
{
    private readonly StreamWriter _writer;
    private readonly StreamReader _reader;
    private readonly SemaphoreSlim _write = new(1, 1);
    private readonly ConcurrentDictionary<long, (string Method, TaskCompletionSource<JsonNode?> Answer)> _pending = new();
    private readonly Channel<RpcIncoming> _incoming = Channel.CreateUnbounded<RpcIncoming>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _readLoop;
    private readonly bool _jsonRpcField;
    private long _nextId;

    /// <param name="jsonRpcField">Send <c>"jsonrpc": "2.0"</c> (ACP requires it; Codex app-server leaves it out).</param>
    public JsonLineRpc(Stream fromPeer, Stream toPeer, bool jsonRpcField = true)
    {
        _reader = new StreamReader(fromPeer, new UTF8Encoding(false));
        _writer = new StreamWriter(toPeer, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
        _jsonRpcField = jsonRpcField;
        _readLoop = Task.Run(ReadLoopAsync);
    }

    /// <summary>Notifications and requests from the other side, in order. Completes when the connection closes.</summary>
    public ChannelReader<RpcIncoming> Incoming => _incoming.Reader;

    /// <summary>Why the connection closed (null while open or after a clean close).</summary>
    public string? CloseReason { get; private set; }

    public async Task<JsonNode?> RequestAsync(string method, JsonNode? parameters = null, CancellationToken ct = default)
    {
        var id = Interlocked.Increment(ref _nextId);
        var answer = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = (method, answer);
        var message = Envelope();
        message["id"] = id;
        message["method"] = method;
        if (parameters is not null) message["params"] = parameters;
        try
        {
            await SendAsync(message, ct).ConfigureAwait(false);
            using var cancel = ct.Register(() => answer.TrySetCanceled(ct));
            return await answer.Task.ConfigureAwait(false);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, JsonNode? parameters = null, CancellationToken ct = default)
    {
        var message = Envelope();
        message["method"] = method;
        if (parameters is not null) message["params"] = parameters;
        return SendAsync(message, ct);
    }

    public Task RespondAsync(JsonNode? id, JsonNode? result, CancellationToken ct = default)
    {
        var message = Envelope();
        message["id"] = id?.DeepClone();
        message["result"] = result ?? new JsonObject();
        return SendAsync(message, ct);
    }

    public Task RespondErrorAsync(JsonNode? id, int code, string error, CancellationToken ct = default)
    {
        var message = Envelope();
        message["id"] = id?.DeepClone();
        message["error"] = new JsonObject { ["code"] = code, ["message"] = error };
        return SendAsync(message, ct);
    }

    private JsonObject Envelope() => _jsonRpcField ? new JsonObject { ["jsonrpc"] = "2.0" } : [];

    private async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        if (CloseReason is { } reason) throw new IOException(reason);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _writer.WriteLineAsync(message.ToJsonString().AsMemory(), ct).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        try
        {
            while (await _reader.ReadLineAsync(_stop.Token).ConfigureAwait(false) is { } line)
            {
                if (line.Trim().Length == 0) continue;
                JsonObject? message;
                try
                {
                    message = JsonNode.Parse(line) as JsonObject;
                }
                catch (System.Text.Json.JsonException)
                {
                    message = null;
                }
                // Some CLIs print warnings on stdout; they are skipped, never logged (they may echo configuration).
                if (message is not null) Dispatch(message);
            }
            Close("The AI app closed the connection.");
        }
        catch (OperationCanceledException)
        {
            Close("Closed.");
        }
        catch (Exception ex)
        {
            Close(ex.Message);
        }
    }

    private void Dispatch(JsonObject message)
    {
        var method = message["method"] is JsonValue m && m.TryGetValue<string>(out var name) ? name : null;
        if (method is null)
        {
            // A response to one of our requests.
            if (message["id"] is JsonValue idValue && idValue.TryGetValue<long>(out var id) && _pending.TryGetValue(id, out var pending))
            {
                if (message["error"] is JsonObject error)
                    pending.Answer.TrySetException(new RpcException(pending.Method, error["message"]?.GetValue<string>() ?? error.ToJsonString()));
                else pending.Answer.TrySetResult(message["result"]?.DeepClone());
            }
            return;
        }
        _incoming.Writer.TryWrite(new RpcIncoming(method, message["params"] as JsonObject ?? [], message["id"]?.DeepClone()));
    }

    private void Close(string reason)
    {
        CloseReason ??= reason;
        _incoming.Writer.TryComplete();
        foreach (var (_, (method, answer)) in _pending) answer.TrySetException(new IOException($"{method}: {reason}"));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _readLoop.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // closing anyway
        }
        _stop.Dispose();
        _write.Dispose();
    }
}
