using System.Text.Json;
using System.Threading.Channels;
using Cuebot.Kick.Pusher;

namespace Cuebot.Kick.Tests.Support;

/// <summary>One in-memory websocket connection: the test plays kick.com's real-time (Centrifugo) server.</summary>
internal sealed class FakeRealtimeTransport : IPusherTransport
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

    /// <summary>Reads the client's connect command and accepts it. Returns the command's <c>connect</c> object.</summary>
    public async Task<JsonElement> EstablishAsync(int ping = 25, bool pong = true, int? ttl = null)
    {
        var connect = await NextSentAsync();
        var result = new Dictionary<string, object> { ["client"] = "client-1", ["version"] = "6.0.0", ["ping"] = ping, ["pong"] = pong };
        if (ttl is { } t)
        {
            result["expires"] = true;
            result["ttl"] = t;
        }
        Push(JsonSerializer.Serialize(new { id = connect.GetProperty("id").GetInt32(), connect = result }));
        return connect.GetProperty("connect");
    }

    /// <summary>A publication; like kick.com, the envelope's <paramref name="data"/> is JSON-encoded into a string.</summary>
    public void Event(string name, string channel, object data) => Push(Frame(name, channel, data));

    public void EventRaw(string name, string channel, string dataJson) =>
        Push(JsonSerializer.Serialize(new { push = new { channel, pub = new { data = new { @event = name, data = dataJson } } } }));

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

    /// <summary>The channel of the next message the client sent, which must be a subscribe command.</summary>
    public async Task<string> NextSubscribedChannelAsync() =>
        (await NextSentAsync()).GetProperty("subscribe").GetProperty("channel").GetString()!;

    public static string Frame(string name, string channel, object data) =>
        JsonSerializer.Serialize(new { push = new { channel, pub = new { data = new { @event = name, data = JsonSerializer.Serialize(data) } } } });

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>Hands out a new <see cref="FakeRealtimeTransport"/> per connection attempt.</summary>
internal sealed class FakeRealtimeServer
{
    private readonly Channel<FakeRealtimeTransport> _connections = Channel.CreateUnbounded<FakeRealtimeTransport>();
    private int _failuresLeft;

    public int Attempts { get; private set; }

    public void FailNextConnects(int count) => _failuresLeft = count;

    public IPusherTransport Create()
    {
        Attempts++;
        var fail = _failuresLeft > 0;
        if (fail) _failuresLeft--;
        var transport = new FakeRealtimeTransport { FailConnect = fail };
        if (!fail) _connections.Writer.TryWrite(transport);
        return transport;
    }

    public async Task<FakeRealtimeTransport> NextConnectionAsync()
    {
        using var timeout = new CancellationTokenSource(Wait.Timeout);
        return await _connections.Reader.ReadAsync(timeout.Token);
    }
}
