using System.Text.Json;
using System.Threading.Channels;
using Cuebot.Kick.Pusher;

namespace Cuebot.Kick.Tests.Support;

/// <summary>One in-memory websocket connection: the test plays the Pusher server.</summary>
internal sealed class FakePusherTransport : IPusherTransport
{
    private readonly Channel<string?> _toClient = Channel.CreateUnbounded<string?>();
    private readonly Channel<string> _fromClient = Channel.CreateUnbounded<string>();

    public Uri? Url { get; private set; }
    public bool FailConnect { get; init; }
    public bool Disposed { get; private set; }
    public string? CloseReason { get; private set; }
    public int? CloseCode { get; private set; }

    public Task ConnectAsync(Uri url, CancellationToken ct)
    {
        Url = url;
        return FailConnect ? throw new IOException("connection refused") : Task.CompletedTask;
    }

    public Task SendAsync(string text, CancellationToken ct)
    {
        _fromClient.Writer.TryWrite(text);
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct) => await _toClient.Reader.ReadAsync(ct);

    // ---- server side ----

    public void Push(string text) => _toClient.Writer.TryWrite(text);

    public void Establish(int activityTimeout = 120, string socketId = "123.456") =>
        Push(Frame("pusher:connection_established", null, new { socket_id = socketId, activity_timeout = activityTimeout }));

    /// <summary>Sends a channel event; like Pusher, <paramref name="data"/> is JSON-encoded into a string.</summary>
    public void Event(string name, string channel, object data) => Push(Frame(name, channel, data));

    public void EventRaw(string name, string channel, string dataJson) =>
        Push(JsonSerializer.Serialize(new { @event = name, channel, data = dataJson }));

    public void Close(int? code = null, string reason = "closed by test")
    {
        CloseCode = code;
        CloseReason = reason;
        _toClient.Writer.TryWrite(null);
    }

    /// <summary>The next message the client sent, parsed.</summary>
    public async Task<JsonElement> NextSentAsync()
    {
        using var timeout = new CancellationTokenSource(Wait.Timeout);
        var text = await _fromClient.Reader.ReadAsync(timeout.Token);
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task<string> NextSentEventAsync() => (await NextSentAsync()).GetProperty("event").GetString()!;

    public static string Frame(string name, string? channel, object data) =>
        JsonSerializer.Serialize(new { @event = name, channel, data = JsonSerializer.Serialize(data) });

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Hands out a new <see cref="FakePusherTransport"/> per connection attempt.</summary>
internal sealed class FakePusherServer
{
    private readonly Channel<FakePusherTransport> _connections = Channel.CreateUnbounded<FakePusherTransport>();
    private int _failuresLeft;

    public int Attempts { get; private set; }

    public void FailNextConnects(int count) => _failuresLeft = count;

    public IPusherTransport Create()
    {
        Attempts++;
        var fail = _failuresLeft > 0;
        if (fail) _failuresLeft--;
        var transport = new FakePusherTransport { FailConnect = fail };
        if (!fail) _connections.Writer.TryWrite(transport);
        return transport;
    }

    public async Task<FakePusherTransport> NextConnectionAsync()
    {
        using var timeout = new CancellationTokenSource(Wait.Timeout);
        return await _connections.Reader.ReadAsync(timeout.Token);
    }
}
