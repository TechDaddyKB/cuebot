using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Cuebot.Core.Webhooks;

namespace Cuebot.Relay.Tests.Support;

/// <summary>An in-memory stand-in for the Cloudflare relay: each connection attempt creates a <see cref="FakeConnection"/>.</summary>
internal sealed class FakeRelay
{
    private readonly ConcurrentQueue<FakeConnection> _connections = new();

    public bool RejectAuth { get; set; }
    public string? UnreachableMessage { get; set; }

    /// <summary>Answer {"type":"ping"} with a pong (switch off to simulate a dead relay).</summary>
    public bool AnswerPings { get; set; } = true;

    public IReadOnlyList<FakeConnection> Connections => _connections.ToArray();

    public FakeConnection Latest => _connections.Last();

    public IRelayTransport Create()
    {
        var c = new FakeConnection(this);
        _connections.Enqueue(c);
        return c;
    }

    public async Task<FakeConnection> WaitForConnectionAsync(int count = 1)
    {
        await Wait.UntilAsync(() => _connections.Count(c => c.IsOpen || c.WasOpened) >= count, $"{count} connection(s)");
        return _connections.Where(c => c.WasOpened).ElementAt(count - 1);
    }
}

internal sealed class FakeConnection(FakeRelay relay) : IRelayTransport
{
    private readonly Channel<string?> _toClient = Channel.CreateUnbounded<string?>();
    private readonly ConcurrentQueue<string> _sent = new();

    public Uri? Url { get; private set; }
    public string? Token { get; private set; }
    public bool IsOpen { get; private set; }
    public bool WasOpened { get; private set; }
    public bool Disposed { get; private set; }
    public int? CloseCode { get; private set; }
    public string? CloseReason { get; private set; }
    private int? _pendingCloseCode;

    public IReadOnlyList<string> Sent => _sent.ToArray();

    public IReadOnlyList<string> Acks => _sent
        .Select(s => JsonDocument.Parse(s).RootElement)
        .Where(e => e.GetProperty("type").GetString() == "ack")
        .Select(e => e.GetProperty("id").GetString()!)
        .ToArray();

    public Task ConnectAsync(Uri url, string bearerToken, CancellationToken ct)
    {
        Url = url;
        Token = bearerToken;
        if (relay.RejectAuth) throw new RelayAuthException("The relay refused the connect token");
        if (relay.UnreachableMessage is { } m) throw new IOException(m);
        IsOpen = WasOpened = true;
        return Task.CompletedTask;
    }

    public Task SendAsync(string text, CancellationToken ct)
    {
        if (!IsOpen) throw new InvalidOperationException("not open");
        _sent.Enqueue(text);
        if (text == """{"type":"ping"}""" && relay.AnswerPings) _toClient.Writer.TryWrite("""{"type":"pong"}""");
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var text = await _toClient.Reader.ReadAsync(ct);
        if (text is null)
        {
            CloseCode = _pendingCloseCode;
            IsOpen = false;
        }
        return text;
    }

    public void Push(string json) => _toClient.Writer.TryWrite(json);

    public void Hello(int buffered = 0) => Push(JsonSerializer.Serialize(new { type = "hello", version = 1, buffered }));

    public void Deliver(string id, string service, string body = "{}", Dictionary<string, string>? headers = null, string path = "", string query = "") =>
        Push(JsonSerializer.Serialize(new
        {
            type = "delivery",
            id,
            service,
            path,
            method = "POST",
            query,
            headers = headers ?? new Dictionary<string, string> { ["content-type"] = "application/json" },
            bodyBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(body)),
            receivedAt = "2026-10-27T12:00:00.000Z",
        }));

    public void Close(int code = 1000, string reason = "")
    {
        _pendingCloseCode = code;
        CloseReason = reason;
        _toClient.Writer.TryWrite(null);
    }

    public ValueTask DisposeAsync()
    {
        IsOpen = false;
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>A handler that records what it got and returns a fixed outcome (or throws).</summary>
internal sealed class FakeHandler(string service, WebhookOutcome outcome = WebhookOutcome.Handled) : IWebhookHandler
{
    private readonly ConcurrentQueue<WebhookDelivery> _received = new();

    public string Service => service;
    public string DisplayName => service.ToUpperInvariant();
    public Exception? Throw { get; set; }
    public TimeSpan Delay { get; set; }
    public IReadOnlyList<WebhookDelivery> Received => _received.ToArray();

    public async Task<WebhookOutcome> HandleAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        if (Delay > TimeSpan.Zero) await Task.Delay(Delay, ct);
        _received.Enqueue(delivery);
        if (Throw is { } ex) throw ex;
        return outcome;
    }
}

internal static class Wait
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task UntilAsync(Func<bool> condition, string what = "condition")
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(10);
        }
    }
}
