using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Threading.Channels;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Integrations.Common;

namespace Cuebot.Integrations.Tests.Support;

/// <summary>In-memory websocket server: every transport the client opens shows up in <see cref="NextAsync"/>.</summary>
internal sealed class FakeServer
{
    private readonly Channel<FakeSocket> _accepted = Channel.CreateUnbounded<FakeSocket>();
    private readonly List<(Uri Url, IReadOnlyDictionary<string, string> Headers)> _attempts = [];

    /// <summary>Return an exception to fail the upgrade for that URL (like a closed port or an HTTP 401).</summary>
    public Func<Uri, Exception?> Reject { get; set; } = _ => null;

    public IReadOnlyList<(Uri Url, IReadOnlyDictionary<string, string> Headers)> Attempts
    {
        get { lock (_attempts) return _attempts.ToArray(); }
    }

    public IWebSocketTransport Create() => new FakeSocket(this);

    internal Exception? Attempt(Uri url, IReadOnlyDictionary<string, string>? headers)
    {
        lock (_attempts) _attempts.Add((url, headers ?? new Dictionary<string, string>()));
        return Reject(url);
    }

    internal void Accept(FakeSocket socket) => _accepted.Writer.TryWrite(socket);

    public async Task<FakeSocket> NextAsync()
    {
        using var cts = new CancellationTokenSource(Wait.Timeout);
        return await _accepted.Reader.ReadAsync(cts.Token);
    }

    public static WebSocketException Status(int code) =>
        new($"The server returned status code '{code}' when status code '101' was expected.");

    public static WebSocketException Refused() => new("Unable to connect to the remote server");
}

internal sealed class FakeSocket(FakeServer server) : IWebSocketTransport
{
    private readonly Channel<string?> _toClient = Channel.CreateUnbounded<string?>();
    private readonly Channel<string> _fromClient = Channel.CreateUnbounded<string>();

    public Uri? Url { get; private set; }
    public IReadOnlyDictionary<string, string> Headers { get; private set; } = new Dictionary<string, string>();
    public bool Disposed { get; private set; }
    public string? CloseReason { get; private set; }

    public Task ConnectAsync(Uri url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        if (server.Attempt(url, headers) is { } error) throw error;
        Url = url;
        Headers = headers ?? new Dictionary<string, string>();
        server.Accept(this);
        return Task.CompletedTask;
    }

    public Task SendAsync(string text, CancellationToken ct)
    {
        if (Disposed) throw new ObjectDisposedException(nameof(FakeSocket));
        _fromClient.Writer.TryWrite(text);
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        try
        {
            return await _toClient.Reader.ReadAsync(ct);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    /// <summary>Server → client text frame.</summary>
    public void Push(string text) => _toClient.Writer.TryWrite(text);

    public void Close(string reason = "server closed the connection (1000)")
    {
        CloseReason = reason;
        _toClient.Writer.TryWrite(null);
    }

    public async Task<string> NextSentAsync()
    {
        using var cts = new CancellationTokenSource(Wait.Timeout);
        return await _fromClient.Reader.ReadAsync(cts.Token);
    }

    public async Task<JsonElement> NextJsonAsync()
    {
        using var document = JsonDocument.Parse(await NextSentAsync());
        return document.RootElement.Clone();
    }

    /// <summary>Reads client frames until one satisfies <paramref name="match"/>.</summary>
    public async Task<JsonElement> NextJsonAsync(Func<JsonElement, bool> match)
    {
        while (true)
        {
            var message = await NextJsonAsync();
            if (match(message)) return message;
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
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

internal static class TestTimings
{
    public static readonly IntegrationsTimings Fast = new(
        ConnectTimeout: TimeSpan.FromSeconds(2),
        RequestTimeout: TimeSpan.FromSeconds(3),
        ReconnectDelay: TimeSpan.FromMilliseconds(20),
        MaxReconnectDelay: TimeSpan.FromMilliseconds(100),
        AuthApprovalTimeout: TimeSpan.FromSeconds(3),
        HeartbeatInterval: TimeSpan.FromMilliseconds(250),
        InjectInterval: TimeSpan.FromMilliseconds(50));
}

/// <summary>A temp data directory with integrations.json, a log and a recording event bus.</summary>
internal sealed class TestEnvironment : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuebot-integrations-tests-" + Guid.NewGuid().ToString("N"));

    public TestEnvironment()
    {
        Directory.CreateDirectory(_dir);
        Settings = new JsonFile<IntegrationsSettings>(Path.Combine(_dir, "integrations.json"), Log);
        Bus = new EventBus(Log);
        Bus.Subscribe(Published.Enqueue);
    }

    public AppLog Log { get; } = new();
    public EventBus Bus { get; }
    public JsonFile<IntegrationsSettings> Settings { get; }
    public FakeServer Server { get; } = new();
    public ConcurrentQueue<BotEvent> Published { get; } = new();

    public BotEvent[] Events(string type) => Published.Where(e => e.Type == type).ToArray();

    public async Task<BotEvent> WaitForEventAsync(string type, Func<BotEvent, bool>? match = null)
    {
        BotEvent? found = null;
        await Wait.UntilAsync(() => (found = Published.FirstOrDefault(e => e.Type == type && (match?.Invoke(e) ?? true))) is not null, type);
        return found!;
    }

    public string AllLogText() => string.Join("\n", Log.Recent().Select(e => e.ToString()));

    public void Dispose()
    {
        Settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}

/// <summary>A clock tests move by hand.</summary>
internal sealed class ManualClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
