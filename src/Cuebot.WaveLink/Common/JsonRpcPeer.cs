using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.WaveLink.Common;

/// <summary>
/// JSON-RPC 2.0 over one Wave Link websocket (both API generations use it): numbered requests, their responses, and
/// server notifications (messages with a method and no id). <see cref="RunAsync"/> reads until the socket closes.
/// </summary>
internal sealed class JsonRpcPeer(IWaveLinkTransport transport, TimeSpan requestTimeout)
{
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> _pending = new();
    private long _nextId;

    /// <summary>Called for every notification (method, params; params is undefined when absent). Runs on the read loop.</summary>
    public Action<string, JsonElement>? Notification { get; set; }

    public IWaveLinkTransport Transport => transport;

    /// <summary>Requests that went out (method names), for diagnostics.</summary>
    public long RequestCount => Interlocked.Read(ref _nextId);

    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text) Dispatch(text);
        }
        finally
        {
            foreach (var id in _pending.Keys)
                if (_pending.TryRemove(id, out var tcs)) tcs.TrySetException(new WaveLinkNotConnectedException());
        }
    }

    /// <summary>Sends a request and waits for its result (an undefined element when the result is null or missing).</summary>
    public async Task<JsonElement> CallAsync(string method, JsonNode? parameters, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        var request = new JsonObject { ["id"] = id, ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null) request["params"] = parameters;
        try
        {
            await transport.SendAsync(request.ToJsonString(), ct).ConfigureAwait(false);
            return await tcs.Task.WaitAsync(requestTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Wave Link did not answer '{method}' in time.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private void Dispatch(string text)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(text);
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return;
        }
        // Batches are legal JSON-RPC; Wave Link has not been seen sending them, but handle them anyway.
        if (root.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in root.EnumerateArray()) DispatchOne(item);
            return;
        }
        DispatchOne(root);
    }

    private void DispatchOne(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) return;
        var hasId = message.TryGetProperty("id", out var idElement) && idElement.ValueKind is JsonValueKind.Number or JsonValueKind.String;
        if (message.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String)
        {
            // A request from the server (method + id) is answered by nobody; Wave Link only sends notifications.
            if (hasId) return;
            var parameters = message.TryGetProperty("params", out var p) ? p : default;
            Notification?.Invoke(method.GetString()!, parameters);
            return;
        }
        if (!hasId) return;
        long id;
        if (idElement.ValueKind == JsonValueKind.Number) id = idElement.GetInt64();
        else if (!long.TryParse(idElement.GetString(), out id)) return;
        if (!_pending.TryRemove(id, out var tcs)) return;
        if (message.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0;
            var text = error.TryGetProperty("message", out var m) ? m.ToString() : "error";
            tcs.TrySetException(new WaveLinkRequestException($"Wave Link refused the request: {text} ({code})", code));
            return;
        }
        tcs.TrySetResult(message.TryGetProperty("result", out var result) && result.ValueKind != JsonValueKind.Null ? result : default);
    }
}
