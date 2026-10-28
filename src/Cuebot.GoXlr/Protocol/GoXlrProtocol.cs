using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.GoXlr.Protocol;

/// <summary>
/// Builds the utility's requests. Its Rust enums are serialized by serde's default "externally tagged" form: a unit
/// variant is a string (<c>"GetStatus"</c>), a one-field variant an object (<c>{"SetFXEnabled":true}</c>) and a
/// multi-field variant an object holding an array (<c>{"SetVolume":["Mic",255]}</c>; no fields: <c>[]</c>).
/// </summary>
internal static class GoXlrRequests
{
    public static JsonNode Ping => JsonValue.Create("Ping")!;

    public static JsonNode GetStatus => JsonValue.Create("GetStatus")!;

    /// <summary>DaemonRequest::Command(serial, GoXLRCommand).</summary>
    public static JsonNode Command(string serial, JsonNode command) => new JsonObject { ["Command"] = new JsonArray(JsonValue.Create(serial), command) };

    /// <summary>A GoXLRCommand variant with its fields.</summary>
    public static JsonNode Cmd(string name, params JsonNode?[] fields) => new JsonObject
    {
        [name] = fields.Length == 1 ? fields[0] : new JsonArray(fields),
    };

    public static JsonNode V(string value) => JsonValue.Create(value)!;

    public static JsonNode V(int value) => JsonValue.Create(value);

    public static JsonNode V(bool value) => JsonValue.Create(value);

    /// <summary>{"id":n,"data":request}.</summary>
    public static string Envelope(ulong id, JsonNode request) =>
        new JsonObject { ["id"] = id, ["data"] = request.DeepClone() }.ToJsonString();
}

/// <summary>
/// The websocket conversation: requests carry an id the answer echoes; status patches arrive unprompted (with id
/// u64::MAX) as <c>{"Patch":[...]}</c>. Answers are <c>"Ok"</c>, <c>{"Error":"..."}</c>, <c>{"Status":{...}}</c> or
/// <c>{"MicLevel":n}</c>.
/// </summary>
internal sealed class GoXlrPeer(IGoXlrTransport transport, TimeSpan requestTimeout)
{
    private readonly ConcurrentDictionary<ulong, TaskCompletionSource<JsonElement>> _pending = new();
    private long _nextId;

    /// <summary>Raised on the read loop for each status patch (the array of operations).</summary>
    public Action<JsonElement>? Patch { get; set; }

    /// <summary>Sends a request and returns the answer's <c>data</c>. Throws <see cref="GoXlrRequestException"/> for an Error answer.</summary>
    public async Task<JsonElement> CallAsync(JsonNode request, CancellationToken ct)
    {
        var id = (ulong)Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(requestTimeout);
            await transport.SendAsync(GoXlrRequests.Envelope(id, request), timeout.Token).ConfigureAwait(false);
            JsonElement data;
            try
            {
                data = await tcs.Task.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new TimeoutException("The GoXLR Utility did not answer in time.");
            }
            if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("Error", out var error))
                throw new GoXlrRequestException(error.ValueKind == JsonValueKind.String ? error.GetString() ?? "error" : error.GetRawText());
            return data;
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Reads until the connection closes (returns) or <paramref name="ct"/> is cancelled.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        try
        {
            while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text)
                Dispatch(text);
        }
        finally
        {
            foreach (var (_, tcs) in _pending) tcs.TrySetException(new GoXlrNotConnectedException());
        }
    }

    internal void Dispatch(string text)
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
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var data)) return;
        if (data.ValueKind == JsonValueKind.Object && data.TryGetProperty("Patch", out var patch))
        {
            Patch?.Invoke(patch);
            return;
        }
        if (root.TryGetProperty("id", out var idElement) && idElement.TryGetUInt64(out var id) && _pending.TryGetValue(id, out var tcs))
            tcs.TrySetResult(data);
    }
}
