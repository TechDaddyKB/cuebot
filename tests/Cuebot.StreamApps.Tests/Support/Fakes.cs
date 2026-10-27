using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Tests.Support;

/// <summary>In-memory websocket server: every transport the client opens shows up in <see cref="NextAsync"/>.</summary>
internal sealed class FakeServer
{
    private readonly Channel<FakeSocket> _accepted = Channel.CreateUnbounded<FakeSocket>();
    private readonly ConcurrentQueue<Uri> _attempts = new();

    /// <summary>Return an exception to fail the upgrade for that URL (like a closed port).</summary>
    public Func<Uri, Exception?> Reject { get; set; } = _ => null;

    public IReadOnlyList<Uri> Attempts => _attempts.ToArray();

    public IWebSocketTransport Create() => new FakeSocket(this);

    internal Exception? Attempt(Uri url)
    {
        _attempts.Enqueue(url);
        return Reject(url);
    }

    internal void Accept(FakeSocket socket) => _accepted.Writer.TryWrite(socket);

    public async Task<FakeSocket> NextAsync()
    {
        using var cts = new CancellationTokenSource(Wait.Timeout);
        return await _accepted.Reader.ReadAsync(cts.Token);
    }
}

internal sealed class FakeSocket(FakeServer server) : IWebSocketTransport
{
    private readonly Channel<string?> _toClient = Channel.CreateUnbounded<string?>();
    private readonly Channel<string> _fromClient = Channel.CreateUnbounded<string>();

    public Uri? Url { get; private set; }
    public bool Disposed { get; private set; }
    public string? CloseReason { get; private set; }

    public Task ConnectAsync(Uri url, CancellationToken ct)
    {
        if (server.Attempt(url) is { } error) throw error;
        Url = url;
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

    public void Push(string text) => _toClient.Writer.TryWrite(text);

    public void Close(string reason = "closed by the other side (1000)")
    {
        CloseReason = reason;
        _toClient.Writer.TryWrite(null);
    }

    public async Task<string> NextSentAsync()
    {
        using var cts = new CancellationTokenSource(Wait.Timeout);
        return await _fromClient.Reader.ReadAsync(cts.Token);
    }

    public bool TryReadSent(out string? text) => _fromClient.Reader.TryRead(out text);

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A Qt WebChannel server publishing a "meld" object like Meld Studio's: answers init with methods, properties and
/// signals, records invokes (answering each with a null result), counts idles and pushes property updates and signals.
/// </summary>
internal sealed class FakeMeld
{
    public static readonly string[] DefaultMethods =
    [
        "showScene", "toggleMute", "toggleMonitor", "toggleLayer", "toggleEffect", "toggleRecord", "toggleStream",
        "registerTrackObserver", "unregisterTrackObserver", "setGain", "callFunction", "callFunctionWithArgs", "sendCommand",
        "sendCommandWithArgs", "sendStreamEvent", "sendEvent", "setStagedScene", "showStagedScene", "setProperty",
    ];

    // Property / signal indices as a Qt server would number them.
    public const int RecordingProperty = 0, StreamingProperty = 1, SessionProperty = 2, VersionProperty = 3;
    public const int RecordingSignal = 10, StreamingSignal = 11, SessionSignal = 12, GainSignal = 13;

    private readonly ConcurrentQueue<(string Method, JsonElement Args)> _calls = new();
    private readonly string[] _methods;
    private int _idles;

    public FakeMeld(FakeSocket socket, JsonObject session, bool recording = false, bool streaming = false, int? version = 2, string[]? methods = null)
    {
        Socket = socket;
        _methods = methods ?? DefaultMethods;
        Session = session;
        Recording = recording;
        Streaming = streaming;
        Version = version;
        Loop = Task.Run(RunAsync);
    }

    public FakeSocket Socket { get; }
    public JsonObject Session { get; private set; }
    public bool Recording { get; }
    public bool Streaming { get; }
    public int? Version { get; }
    public Task Loop { get; }
    public int Idles => Volatile.Read(ref _idles);
    public ConcurrentQueue<int> ConnectedSignals { get; } = new();
    public IReadOnlyList<(string Method, JsonElement Args)> Calls => _calls.ToArray();

    /// <summary>Method index = 100 + position, so tests can tell indices from names.</summary>
    public int MethodIndex(string name) => 100 + Array.IndexOf(_methods, name);

    public async Task<(string Method, JsonElement Args)> NextCallAsync(string method)
    {
        await Wait.UntilAsync(() => _calls.Any(c => c.Method == method), $"call to {method}");
        return _calls.First(c => c.Method == method);
    }

    public int CallCount(string method) => _calls.Count(c => c.Method == method);

    public void PushSession(JsonObject session)
    {
        Session = session;
        PushProperty(SessionProperty, SessionSignal, new JsonObject { ["items"] = session.DeepClone() });
    }

    public void PushRecording(bool value) => PushProperty(RecordingProperty, RecordingSignal, value);

    public void PushStreaming(bool value) => PushProperty(StreamingProperty, StreamingSignal, value);

    public void PushGain(string trackId, double gain, bool muted) =>
        Socket.Push(new JsonObject { ["type"] = 1, ["object"] = "meld", ["signal"] = GainSignal, ["args"] = new JsonArray(trackId, gain, muted) }.ToJsonString());

    private void PushProperty(int property, int signal, JsonNode? value) =>
        Socket.Push(new JsonObject
        {
            ["type"] = 2,
            ["data"] = new JsonArray(new JsonObject
            {
                ["object"] = "meld",
                ["signals"] = new JsonObject { [signal.ToString()] = new JsonArray() },
                ["properties"] = new JsonObject { [property.ToString()] = value },
            }),
        }.ToJsonString());

    private async Task RunAsync()
    {
        while (true)
        {
            string text;
            try
            {
                text = await Socket.NextSentAsync();
            }
            catch (OperationCanceledException)
            {
                return;
            }
            using var doc = JsonDocument.Parse(text);
            var m = doc.RootElement;
            switch (m.GetProperty("type").GetInt32())
            {
                case 3:
                    Socket.Push(new JsonObject { ["type"] = 10, ["id"] = m.GetProperty("id").GetInt64(), ["data"] = Description() }.ToJsonString());
                    break;
                case 4:
                    Interlocked.Increment(ref _idles);
                    break;
                case 6:
                    var method = m.GetProperty("method");
                    var name = method.ValueKind == JsonValueKind.Number ? _methods[method.GetInt32() - 100] : method.GetString()!;
                    _calls.Enqueue((name, m.GetProperty("args").Clone()));
                    Socket.Push(new JsonObject { ["type"] = 10, ["id"] = m.GetProperty("id").GetInt64(), ["data"] = null }.ToJsonString());
                    break;
                case 7:
                    ConnectedSignals.Enqueue(m.GetProperty("signal").GetInt32());
                    break;
            }
        }
    }

    private JsonObject Description()
    {
        var methods = new JsonArray();
        for (var i = 0; i < _methods.Length; i++)
        {
            methods.Add(new JsonArray(_methods[i], 100 + i));
            methods.Add(new JsonArray(_methods[i] + "(QString)", 100 + i));   // Qt also lists signatures
        }
        var properties = new JsonArray(
            new JsonArray(RecordingProperty, "isRecording", new JsonArray(1, RecordingSignal), Recording),
            new JsonArray(StreamingProperty, "isStreaming", new JsonArray("isStreamingChanged", StreamingSignal), Streaming),
            new JsonArray(SessionProperty, "session", new JsonArray(1, SessionSignal), new JsonObject { ["items"] = Session.DeepClone() }));
        if (Version is { } v) properties.Add(new JsonArray(VersionProperty, "version", null, v));
        return new JsonObject
        {
            ["meld"] = new JsonObject
            {
                ["methods"] = methods,
                ["properties"] = properties,
                ["signals"] = new JsonArray(new JsonArray("destroyed", 0), new JsonArray("gainUpdated", GainSignal)),
                ["enums"] = new JsonObject(),
            },
        };
    }

    /// <summary>The session from Meld's documentation, trimmed: two scenes, layers, an effect and tracks.</summary>
    public static JsonObject SampleSession() => new()
    {
        [Ids.Chatting] = new JsonObject { ["type"] = "scene", ["name"] = "Just Chatting", ["index"] = 1, ["current"] = true, ["staged"] = false },
        [Ids.Gaming] = new JsonObject { ["type"] = "scene", ["name"] = "Gaming", ["index"] = 0, ["current"] = false, ["staged"] = false },
        [Ids.Camera] = new JsonObject { ["type"] = "layer", ["name"] = "Camera", ["parent"] = Ids.Gaming, ["index"] = 0, ["visible"] = true },
        [Ids.Spoilers] = new JsonObject { ["type"] = "layer", ["name"] = "Spoilers", ["parent"] = Ids.Chatting, ["index"] = 1, ["visible"] = true, ["url"] = "https://example.com/" },
        [Ids.Glow] = new JsonObject { ["type"] = "effect", ["name"] = "Glow Styles", ["parent"] = Ids.Camera, ["enabled"] = true },
        [Ids.Mic] = new JsonObject { ["type"] = "track", ["name"] = "Microphone", ["muted"] = true, ["monitoring"] = false },
        [Ids.SpoilersTrack] = new JsonObject { ["type"] = "track", ["name"] = "Spoilers", ["parent"] = Ids.Spoilers, ["muted"] = false, ["monitoring"] = false },
    };

    public static class Ids
    {
        public const string Chatting = "05731BDA0C5DE934359C8F7F24CE6C1B";
        public const string Gaming = "9DA5A633182EB0C0804E671B26A1B581";
        public const string Camera = "9F2A72924699F775E0E789C6940141F6";
        public const string Spoilers = "89BFE4955144FBD6490ECBB453CA6639";
        public const string Glow = "85DFC4BCD7451D2DB3C55A6E2D9948E2";
        public const string Mic = "D4D3457F8AF5D1F5381A903A8C43BE27";
        public const string SpoilersTrack = "375F1BB5A322BCC7F60CAAD16EE859D7";
        public const string NewLayer = "EC495E605169EF94FE380E10A57EBF82";
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
    public static readonly StreamAppsTimings Fast = new(
        ConnectTimeout: TimeSpan.FromSeconds(2),
        RequestTimeout: TimeSpan.FromSeconds(3),
        ReconnectDelay: TimeSpan.FromMilliseconds(20),
        MaxReconnectDelay: TimeSpan.FromMilliseconds(100));
}

/// <summary>A temp data directory with streamapps.json, a log and a recording event bus.</summary>
internal sealed class TestEnvironment : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuebot-streamapps-tests-" + Guid.NewGuid().ToString("N"));

    public TestEnvironment()
    {
        Directory.CreateDirectory(_dir);
        Settings = new JsonFile<StreamAppsSettings>(Path.Combine(_dir, "streamapps.json"), Log);
        Bus = new EventBus(Log);
        Bus.Subscribe(Published.Enqueue);
    }

    public AppLog Log { get; } = new();
    public EventBus Bus { get; }
    public JsonFile<StreamAppsSettings> Settings { get; }
    public FakeServer Server { get; } = new();
    public ConcurrentQueue<BotEvent> Published { get; } = new();

    public BotEvent[] Events(string type) => Published.Where(e => e.Type == type).ToArray();

    public async Task<BotEvent> WaitForEventAsync(string type, Func<BotEvent, bool>? match = null)
    {
        BotEvent? found = null;
        await Wait.UntilAsync(() => (found = Published.FirstOrDefault(e => e.Type == type && (match?.Invoke(e) ?? true))) is not null, type);
        return found!;
    }

    public void Dispose()
    {
        Settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* best effort */ }
    }
}
