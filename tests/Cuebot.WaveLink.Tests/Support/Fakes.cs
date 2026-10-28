using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.WaveLink.Common;

namespace Cuebot.WaveLink.Tests.Support;

/// <summary>
/// In-memory 127.0.0.1: fake Wave Link apps listen on ports; a transport connecting to a port nobody listens on fails
/// like a refused connection. Every connection attempt is recorded.
/// </summary>
internal sealed class FakeNetwork
{
    private readonly ConcurrentDictionary<int, Func<FakeSocket, FakeWaveLink>> _listeners = new();
    private readonly ConcurrentDictionary<int, ConcurrentQueue<FakeWaveLink>> _apps = new();
    private readonly ConcurrentQueue<int> _attempts = new();

    public IReadOnlyList<int> Attempts => _attempts.ToArray();

    public IWaveLinkTransport Create() => new FakeSocket(this);

    public void Listen(int port, Func<FakeSocket, FakeWaveLink> app) => _listeners[port] = app;

    /// <summary>Stops listening and closes the open connections (Wave Link quit).</summary>
    public void Stop(int port)
    {
        _listeners.TryRemove(port, out _);
        if (_apps.TryGetValue(port, out var apps))
            foreach (var app in apps) app.Socket.Close();
    }

    internal void Connect(FakeSocket socket, Uri url)
    {
        _attempts.Enqueue(url.Port);
        if (url.Host != "127.0.0.1") throw new InvalidOperationException("Wave Link is only reachable on 127.0.0.1");
        if (!_listeners.TryGetValue(url.Port, out var factory))
            throw new WebSocketException(WebSocketError.Faulted, "No connection could be made because the target machine actively refused it.");
        _apps.GetOrAdd(url.Port, _ => new ConcurrentQueue<FakeWaveLink>()).Enqueue(factory(socket));
    }

    /// <summary>The newest app instance a client connected to on that port.</summary>
    public async Task<FakeWaveLink> AppAsync(int port, int instance = 0)
    {
        FakeWaveLink? found = null;
        await Wait.UntilAsync(() => _apps.TryGetValue(port, out var q) && (found = q.Skip(instance).FirstOrDefault()) is not null, $"connection on {port}");
        return found!;
    }

    public int Connections(int port) => _apps.TryGetValue(port, out var q) ? q.Count : 0;
}

internal sealed class FakeSocket(FakeNetwork network) : IWaveLinkTransport
{
    private readonly Channel<string?> _toClient = Channel.CreateUnbounded<string?>();
    private readonly Channel<string> _fromClient = Channel.CreateUnbounded<string>();

    public bool Disposed { get; private set; }
    public string? CloseReason { get; private set; }

    public Task ConnectAsync(Uri url, CancellationToken ct)
    {
        network.Connect(this, url);
        return Task.CompletedTask;
    }

    public Task SendAsync(string text, CancellationToken ct)
    {
        if (Disposed) throw new ObjectDisposedException(nameof(FakeSocket));
        _fromClient.Writer.TryWrite(text);
        return Task.CompletedTask;
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct) => await _toClient.Reader.ReadAsync(ct);

    public void Push(string text) => _toClient.Writer.TryWrite(text);

    public void Close(string reason = "closed by Wave Link (1000)")
    {
        CloseReason = reason;
        _toClient.Writer.TryWrite(null);
    }

    public async Task<string?> NextSentAsync(CancellationToken ct)
    {
        try
        {
            return await _fromClient.Reader.ReadAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _fromClient.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

/// <summary>
/// A JSON-RPC 2.0 server shaped like Wave Link: answers its methods from <see cref="Methods"/> (unknown ones get
/// -32601 "Method not found"), records every call and pushes notifications.
/// </summary>
internal sealed class FakeWaveLink
{
    private readonly ConcurrentQueue<(string Method, JsonNode? Params)> _calls = new();
    private readonly CancellationTokenSource _stop = new();

    public FakeWaveLink(FakeSocket socket, Dictionary<string, Func<JsonNode?, JsonNode?>> methods)
    {
        Socket = socket;
        Methods = methods;
        Loop = Task.Run(RunAsync);
    }

    public FakeSocket Socket { get; }
    public Dictionary<string, Func<JsonNode?, JsonNode?>> Methods { get; }
    public Task Loop { get; }

    /// <summary>When set, answers this method with a JSON-RPC error.</summary>
    public ConcurrentDictionary<string, string> Errors { get; } = new();

    public IReadOnlyList<(string Method, JsonNode? Params)> Calls => _calls.ToArray();

    public IReadOnlyList<JsonNode?> CallsTo(string method) => _calls.Where(c => c.Method == method).Select(c => c.Params).ToArray();

    public async Task<JsonNode?> NextCallAsync(string method, int index = 0)
    {
        await Wait.UntilAsync(() => CallsTo(method).Count > index, $"call to {method}");
        return CallsTo(method)[index];
    }

    public void Notify(string method, JsonNode? parameters)
    {
        var message = new JsonObject { ["jsonrpc"] = "2.0", ["method"] = method };
        if (parameters is not null) message["params"] = parameters;
        Socket.Push(message.ToJsonString());
    }

    private async Task RunAsync()
    {
        while (await Socket.NextSentAsync(_stop.Token) is { } text)
        {
            var request = JsonNode.Parse(text)!.AsObject();
            var method = request["method"]!.GetValue<string>();
            var id = request["id"]!.GetValue<long>();
            Assert.Equal("2.0", request["jsonrpc"]!.GetValue<string>());
            var parameters = request["params"]?.DeepClone();
            _calls.Enqueue((method, parameters));
            JsonObject response;
            if (Errors.TryGetValue(method, out var error))
                response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32000, ["message"] = error } };
            else if (Methods.TryGetValue(method, out var handler))
                response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = handler(parameters) };
            else
                response = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "Method not found" } };
            Socket.Push(response.ToJsonString());
        }
    }

    // ---- Wave Link 3 ----

    public static class V3Ids
    {
        public const string Mic = "PCM_IN_01_C_00_SD1";
        public const string Music = "com.elgato.channel.music";
        public const string Game = "com.elgato.channel.game";
        public const string Personal = "com.elgato.mix.personal";
        public const string Stream = "com.elgato.mix.stream";
        public const string Wave3 = "WAVE3_USB_0001";
        public const string Wave3Input = "WAVE3_USB_0001_IN";
        public const string Headphones = "HEADPHONES_0001";
        public const string HeadphonesOut = "HEADPHONES_0001_OUT";
        public const string Speakers = "SPEAKERS_0002";
        public const string SpeakersOut = "SPEAKERS_0002_OUT";
    }

    public static JsonObject V3Info() => new() { ["appID"] = "EWL", ["name"] = "Elgato Wave Link", ["interfaceRevision"] = 1 };

    public static JsonArray V3Channels() =>
    [
        new JsonObject
        {
            ["id"] = V3Ids.Mic, ["name"] = "Wave:3", ["type"] = "Hardware", ["isMuted"] = false, ["level"] = 0.8,
            ["image"] = new JsonObject { ["name"] = "microphone", ["isAppIcon"] = false },
            ["apps"] = new JsonArray(),
            ["mixes"] = new JsonArray(
                new JsonObject { ["mixId"] = V3Ids.Personal, ["isMuted"] = false, ["level"] = 1.0 },
                new JsonObject { ["mixId"] = V3Ids.Stream, ["isMuted"] = false, ["level"] = 0.9 }),
            ["effects"] = new JsonArray(new JsonObject { ["id"] = "fx-eq", ["name"] = "EQ", ["isEnabled"] = true, ["isSupported"] = true }),
        },
        new JsonObject
        {
            ["id"] = V3Ids.Music, ["name"] = "Music", ["type"] = "Software", ["isMuted"] = false, ["level"] = 0.5,
            ["apps"] = new JsonArray(new JsonObject { ["id"] = "spotify.exe", ["name"] = "Spotify" }),
            ["mixes"] = new JsonArray(
                new JsonObject { ["mixId"] = V3Ids.Personal, ["isMuted"] = false, ["level"] = 0.6 },
                new JsonObject { ["mixId"] = V3Ids.Stream, ["isMuted"] = true, ["level"] = 0.3 }),
            ["effects"] = new JsonArray(),
        },
        new JsonObject
        {
            ["id"] = V3Ids.Game, ["name"] = "Game", ["type"] = "Software", ["isMuted"] = false, ["level"] = 1.0,
            ["apps"] = new JsonArray(),
            ["mixes"] = new JsonArray(
                new JsonObject { ["mixId"] = V3Ids.Personal, ["isMuted"] = false, ["level"] = 0.75 },
                new JsonObject { ["mixId"] = V3Ids.Stream, ["isMuted"] = false, ["level"] = 0.5 }),
        },
    ];

    public static JsonArray V3Mixes() =>
    [
        new JsonObject { ["id"] = V3Ids.Personal, ["name"] = "Personal Mix", ["isMuted"] = false, ["level"] = 1.0 },
        new JsonObject { ["id"] = V3Ids.Stream, ["name"] = "Stream Mix", ["isMuted"] = false, ["level"] = 0.85 },
    ];

    public static JsonArray V3InputDevices() =>
    [
        new JsonObject
        {
            ["id"] = V3Ids.Wave3, ["name"] = "Wave:3", ["isWaveDevice"] = true,
            ["inputs"] = new JsonArray(new JsonObject
            {
                ["id"] = V3Ids.Wave3Input, ["name"] = "Wave:3", ["isMuted"] = false, ["isGainLockOn"] = false,
                ["gain"] = new JsonObject { ["value"] = 0.5, ["max"] = 40 },
                ["micPcMix"] = new JsonObject { ["value"] = 0.25, ["isInverted"] = false },
                ["effects"] = new JsonArray(),
                ["dspEffects"] = new JsonArray(
                    new JsonObject { ["id"] = "clipguard", ["name"] = "Clipguard", ["isEnabled"] = true, ["isSupported"] = true },
                    new JsonObject { ["id"] = "lowcut", ["name"] = "Low Cut Filter", ["isEnabled"] = false, ["isSupported"] = true }),
            }),
        },
    ];

    public static JsonObject V3OutputDevices() => new()
    {
        ["mainOutput"] = new JsonObject { ["outputDeviceId"] = V3Ids.Headphones, ["outputId"] = V3Ids.HeadphonesOut },
        ["outputDevices"] = new JsonArray(
            new JsonObject
            {
                ["id"] = V3Ids.Headphones, ["name"] = "Headphones (Wave:3)", ["isWaveDevice"] = true,
                ["outputs"] = new JsonArray(new JsonObject { ["id"] = V3Ids.HeadphonesOut, ["name"] = "Headphones", ["isMuted"] = false, ["level"] = 0.7, ["mixId"] = V3Ids.Personal }),
            },
            new JsonObject
            {
                ["id"] = V3Ids.Speakers, ["name"] = "Speakers (Realtek)", ["isWaveDevice"] = false,
                ["outputs"] = new JsonArray(new JsonObject { ["id"] = V3Ids.SpeakersOut, ["name"] = "Speakers", ["isMuted"] = false, ["level"] = 1.0, ["mixId"] = V3Ids.Stream }),
            }),
    };

    /// <summary>A Wave Link 3 like the official Stream Deck plugin sees it; setters answer null (no echo).</summary>
    public static FakeWaveLink V3(FakeSocket socket) => new(socket, new()
    {
        ["getApplicationInfo"] = _ => V3Info(),
        ["setPluginInfo"] = _ => null,
        ["getChannels"] = _ => new JsonObject { ["channels"] = V3Channels() },
        ["getMixes"] = _ => new JsonObject { ["mixes"] = V3Mixes() },
        ["getInputDevices"] = _ => new JsonObject { ["inputDevices"] = V3InputDevices() },
        ["getOutputDevices"] = _ => V3OutputDevices(),
        ["setSubscription"] = p => p?.DeepClone(),
        ["setChannel"] = _ => null,
        ["setMix"] = _ => null,
        ["setInputDevice"] = _ => null,
        ["setOutputDevice"] = _ => null,
        ["addToChannel"] = _ => null,
    });

    // ---- Wave Link 1.x/2.x ----

    public static class LegacyIds
    {
        public const string Mic = "PCM_IN_01_C_00_SD1";
        public const string Music = "PCM_OUT_01_V_02_SD3";
        public const string Headset = "HEADSET_{0.0.0.00000000}.{1d6a2b}";
        public const string Speakers = "SPEAKERS_{0.0.0.00000000}.{88c1f0}";
        public const string Local = "com.elgato.mix.local";
        public const string Stream = "com.elgato.mix.stream";
    }

    public static JsonObject LegacyInfo(int revision = 7) =>
        new() { ["appID"] = "egwl", ["appName"] = "Elgato Wave Link", ["interfaceRevision"] = revision };

    public static JsonArray LegacyInputs() =>
    [
        new JsonObject
        {
            ["identifier"] = LegacyIds.Mic, ["name"] = "Wave:3", ["inputType"] = 1, ["isAvailable"] = true, ["bgColor"] = "#1E183C",
            ["localMixer"] = new JsonArray(false, 100, false), ["streamMixer"] = new JsonArray(false, 90, false),
            ["filters"] = new JsonArray(new JsonObject { ["filterID"] = "F-NV-1", ["name"] = "NVIDIA Broadcast", ["isActive"] = true, ["pluginID"] = "nvafx" }),
        },
        new JsonObject
        {
            ["identifier"] = LegacyIds.Music, ["name"] = "Music", ["inputType"] = 4, ["isAvailable"] = true,
            ["localMixer"] = new JsonArray(false, 50, false), ["streamMixer"] = new JsonArray(true, 30, false),
            ["filters"] = new JsonArray(),
        },
    ];

    public static JsonArray LegacyMicrophones() =>
    [
        new JsonObject
        {
            ["identifier"] = LegacyIds.Mic, ["name"] = "Wave:3", ["isWaveLink"] = true, ["isWaveXLR"] = false,
            ["gain"] = 75, ["outputVolume"] = 80, ["balance"] = 0.5, ["isLowCutOn"] = false, ["lowCutType"] = 0,
            ["isClipGuardOn"] = true, ["isGainLocked"] = false, ["isMicMuted"] = false,
        },
    ];

    /// <summary>A Wave Link 1.x/2.x (interface revision 7 by default); setters answer null (no echo).</summary>
    public static FakeWaveLink Legacy(FakeSocket socket, int revision = 7) => new(socket, new()
    {
        ["getApplicationInfo"] = _ => LegacyInfo(revision),
        ["getMicrophoneConfig"] = _ => LegacyMicrophones(),
        ["getSwitchState"] = _ => new JsonObject { ["value"] = "LocalMix" },
        ["getOutputConfig"] = _ => new JsonObject { ["localMixer"] = new JsonArray(false, 80), ["streamMixer"] = new JsonArray(false, 100) },
        ["getOutputs"] = _ => new JsonObject
        {
            ["outputs"] = new JsonObject
            {
                ["localMixer"] = new JsonArray(
                    new JsonObject { ["identifier"] = LegacyIds.Headset, ["name"] = "Headset" },
                    new JsonObject { ["identifier"] = LegacyIds.Speakers, ["name"] = "Speakers" }),
                ["streamMixer"] = new JsonArray(new JsonObject { ["identifier"] = "PCM_IN_01_V_00_SD2", ["name"] = "Stream" }),
            },
            ["selectedOutput"] = new JsonObject { ["localMixer"] = LegacyIds.Headset, ["streamMixer"] = "PCM_IN_01_V_00_SD2" },
        },
        ["getInputConfigs"] = _ => LegacyInputs(),
        ["setInputConfig"] = _ => null,
        ["setOutputConfig"] = _ => null,
        ["setMicrophoneConfig"] = _ => null,
        ["setFilter"] = _ => null,
        ["setFilterBypass"] = _ => null,
        ["setSelectedOutput"] = _ => null,
        ["switchOutput"] = _ => null,
    });
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
            await Task.Delay(5);
        }
    }
}

/// <summary>A TimeProvider whose clock only moves when told to; timers fire during <see cref="Advance"/>.</summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks = TimeSpan.FromHours(1).Ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp()
    {
        lock (_gate) return _ticks;
    }

    public override DateTimeOffset GetUtcNow() => new(new DateTime(2026, 10, 27).Ticks + GetTimestamp(), TimeSpan.Zero);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timer.Change(dueTime, period);
        return timer;
    }

    public int PendingTimers
    {
        get { lock (_gate) return _timers.Count; }
    }

    public void Advance(TimeSpan by)
    {
        long target;
        lock (_gate) target = _ticks + by.Ticks;
        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(t => t.Due <= target).MinBy(t => t.Due);
                if (next is null)
                {
                    _ticks = target;
                    return;
                }
                _ticks = Math.Max(_ticks, next.Due);
                _timers.Remove(next);
            }
            next.Fire();
        }
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public long Due;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._gate)
            {
                owner._timers.Remove(this);
                if (dueTime == Timeout.InfiniteTimeSpan) return true;
                Due = owner._ticks + dueTime.Ticks;
                owner._timers.Add(this);
            }
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner._gate) owner._timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}

internal static class TestTimings
{
    public static readonly WaveLinkTimings Fast = new(
        ConnectTimeout: TimeSpan.FromSeconds(2),
        RequestTimeout: TimeSpan.FromSeconds(3),
        ReconnectDelay: TimeSpan.FromMilliseconds(20),
        MaxReconnectDelay: TimeSpan.FromMilliseconds(100));
}

/// <summary>A temp data directory with wavelink.json, a log, a recording event bus and the fake network.</summary>
internal sealed class TestEnvironment : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuebot-wavelink-tests-" + Guid.NewGuid().ToString("N"));

    public TestEnvironment()
    {
        Directory.CreateDirectory(_dir);
        Settings = new JsonFile<WaveLinkSettings>(Path.Combine(_dir, "wavelink.json"), Log);
        Bus = new EventBus(Log);
        Bus.Subscribe(Published.Enqueue);
    }

    public string Directory_ => _dir;
    public AppLog Log { get; } = new();
    public EventBus Bus { get; }
    public JsonFile<WaveLinkSettings> Settings { get; }
    public FakeNetwork Network { get; } = new();
    public ManualTimeProvider Time { get; } = new();
    public ConcurrentQueue<BotEvent> Published { get; } = new();

    /// <summary>Paths the service reads ws-info.json from (empty = none).</summary>
    public List<string> WsInfoPaths { get; } = [];

    public string? UnsupportedReason { get; set; }

    public WaveLinkService Service(TimeProvider? time = null) => new(Settings, Bus, Log, Network.Create, TestTimings.Fast,
        time ?? TimeProvider.System, () => UnsupportedReason, () => WsInfoPaths);

    public void WriteWsInfo(int port)
    {
        var path = Path.Combine(_dir, "ws-info.json");
        File.WriteAllText(path, $"{{\"port\":{port}}}");
        WsInfoPaths.Add(path);
    }

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

internal static class JsonExtensions
{
    public static JsonElement Element(this JsonNode? node) => JsonDocument.Parse(node?.ToJsonString() ?? "null").RootElement.Clone();
}
