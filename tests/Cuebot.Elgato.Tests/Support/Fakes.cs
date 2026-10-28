using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Elgato.CameraHub;
using Cuebot.Elgato.Lights;

namespace Cuebot.Elgato.Tests.Support;

/// <summary>
/// A real HTTP/1.1 server on 127.0.0.1 that behaves like an Elgato Key Light's port-9123 API (one connection per request).
/// </summary>
internal sealed class FakeLightServer : IAsyncDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _accept;
    private readonly object _gate = new();

    public FakeLightServer(string serial = "CW12K1A00001", string product = "Elgato Key Light")
    {
        Serial = serial;
        Product = product;
        _listener.Start();
        _accept = Task.Run(AcceptLoopAsync);
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;
    public string Address => "127.0.0.1";
    public string Serial { get; set; }
    public string Product { get; set; }
    public string DisplayName { get; set; } = "";
    public bool On { get; set; }
    public int Brightness { get; set; } = 20;
    public int? Temperature { get; set; } = 213;
    public double? Hue { get; set; }
    public double? Saturation { get; set; }
    public int PowerOnBrightness { get; set; } = 20;

    /// <summary>While true every request gets HTTP 503 (like a light that is unplugged, but faster to test).</summary>
    public bool Down { get; set; }

    public int Identifies { get; private set; }

    public ConcurrentQueue<string> Requests { get; } = new();

    public ConcurrentQueue<string> Bodies { get; } = new();

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token);
            }
            catch (Exception)
            {
                return;
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using var _ = client;
        try
        {
            var stream = client.GetStream();
            while (true)
            {
                var (method, path, body) = await ReadRequestAsync(stream);
                if (method is null) return;
                Requests.Enqueue($"{method} {path}");
                if (body.Length > 0) Bodies.Enqueue(body);
                var (status, json) = Down ? (503, "") : Handle(method, path!, body);
                var payload = Encoding.UTF8.GetBytes(json);
                var head = $"HTTP/1.1 {status} {(status == 200 ? "OK" : "Error")}\r\nContent-Type: application/json\r\nContent-Length: {payload.Length}\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
                await stream.WriteAsync(payload);
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private (int, string) Handle(string method, string path, string body)
    {
        lock (_gate)
        {
            switch (method, path)
            {
                case ("GET", "/elgato/lights"):
                    return (200, LightsJson());
                case ("PUT", "/elgato/lights"):
                    var light = JsonNode.Parse(body)!["lights"]![0]!;
                    if (light["on"] is { } on) On = on.GetValue<int>() != 0;
                    if (light["brightness"] is { } b) Brightness = b.GetValue<int>();
                    if (light["temperature"] is { } t) Temperature = t.GetValue<int>();
                    if (light["hue"] is { } h) Hue = h.GetValue<double>();
                    if (light["saturation"] is { } s) Saturation = s.GetValue<double>();
                    return (200, LightsJson());
                case ("GET", "/elgato/accessory-info"):
                    return (200, new JsonObject
                    {
                        ["productName"] = Product, ["hardwareBoardType"] = 53, ["firmwareBuildNumber"] = 218, ["firmwareVersion"] = "1.0.3",
                        ["serialNumber"] = Serial, ["displayName"] = DisplayName, ["features"] = new JsonArray("lights"),
                        ["wifi-info"] = new JsonObject { ["ssid"] = "test", ["frequencyMHz"] = 2400, ["rssi"] = -48 },
                    }.ToJsonString());
                case ("PUT", "/elgato/accessory-info"):
                    DisplayName = JsonNode.Parse(body)!["displayName"]!.GetValue<string>();
                    return (200, "");
                case ("GET", "/elgato/lights/settings"):
                    return (200, $"{{\"powerOnBehavior\":1,\"powerOnBrightness\":{PowerOnBrightness},\"powerOnTemperature\":213,\"switchOnDurationMs\":100,\"switchOffDurationMs\":300,\"colorChangeDurationMs\":100}}");
                case ("POST", "/elgato/identify"):
                    Identifies++;
                    return (200, "");
                default:
                    return (404, "");
            }
        }
    }

    private string LightsJson()
    {
        var light = new JsonObject { ["on"] = On ? 1 : 0, ["brightness"] = Brightness };
        if (Temperature is { } t) light["temperature"] = t;
        if (Hue is { } h) light["hue"] = h;
        if (Saturation is { } s) light["saturation"] = s;
        return new JsonObject { ["numberOfLights"] = 1, ["lights"] = new JsonArray(light) }.ToJsonString();
    }

    private static async Task<(string? Method, string? Path, string Body)> ReadRequestAsync(NetworkStream stream)
    {
        var header = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var n = await stream.ReadAsync(one);
            if (n == 0) return (null, null, "");
            header.Add(one[0]);
            if (header.Count >= 4 && header[^1] == '\n' && header[^2] == '\r' && header[^3] == '\n' && header[^4] == '\r') break;
        }
        var lines = Encoding.ASCII.GetString([.. header]).Split("\r\n");
        var parts = lines[0].Split(' ');
        var length = lines.Select(l => l.Split(':', 2)).Where(p => p.Length == 2 && p[0].Trim().Equals("Content-Length", StringComparison.OrdinalIgnoreCase))
            .Select(p => int.Parse(p[1].Trim())).FirstOrDefault();
        var body = new byte[length];
        var read = 0;
        while (read < length)
        {
            var n = await stream.ReadAsync(body.AsMemory(read));
            if (n == 0) break;
            read += n;
        }
        return (parts[0], parts[1], Encoding.UTF8.GetString(body, 0, read));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _listener.Stop();
        try { await _accept; } catch { }
        _stop.Dispose();
    }
}

/// <summary>Answers mDNS queries with canned packets (built by <see cref="DnsPacketBuilder"/>), recording each query.</summary>
internal sealed class FakeMdnsTransport : IMdnsTransport
{
    public List<MdnsPacket> Answers { get; } = [];
    public ConcurrentQueue<byte[]> Queries { get; } = new();

    public Task<IReadOnlyList<MdnsPacket>> QueryAsync(byte[] query, TimeSpan listen, CancellationToken ct)
    {
        Queries.Enqueue(query);
        return Task.FromResult<IReadOnlyList<MdnsPacket>>([.. Answers]);
    }
}

/// <summary>
/// Writes DNS response packets byte by byte (independent of the parser under test), including name compression the way
/// real responders (Elgato's firmware, Bonjour, Avahi) use it.
/// </summary>
internal sealed class DnsPacketBuilder
{
    private readonly List<byte> _bytes = [];
    private readonly Dictionary<string, int> _names = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Action> _records = [];
    private int _count;

    public DnsPacketBuilder()
    {
        // id 0, flags 0x8400 (response, authoritative), qd 0, an (patched), ns 0, ar 0
        _bytes.AddRange([0, 0, 0x84, 0x00, 0, 0, 0, 0, 0, 0, 0, 0]);
    }

    public DnsPacketBuilder Ptr(string name, string target) => Record(name, 12, () => Name(target));

    public DnsPacketBuilder Srv(string name, int port, string target) => Record(name, 33, () =>
    {
        _bytes.AddRange([0, 0, 0, 0, (byte)(port >> 8), (byte)port]);
        Name(target);
    });

    public DnsPacketBuilder Txt(string name, params string[] entries) => Record(name, 16, () =>
    {
        foreach (var e in entries)
        {
            var b = Encoding.UTF8.GetBytes(e);
            _bytes.Add((byte)b.Length);
            _bytes.AddRange(b);
        }
    });

    public DnsPacketBuilder A(string name, string ip) => Record(name, 1, () => _bytes.AddRange(IPAddress.Parse(ip).GetAddressBytes()));

    public byte[] Build()
    {
        _bytes[6] = (byte)(_count >> 8);
        _bytes[7] = (byte)_count;
        return [.. _bytes];
    }

    private DnsPacketBuilder Record(string name, ushort type, Action data)
    {
        _count++;
        Name(name);
        _bytes.AddRange([(byte)(type >> 8), (byte)type, 0x80, 0x01, 0, 0, 0x11, 0x94]);   // class IN + cache-flush, ttl 4500
        var lengthAt = _bytes.Count;
        _bytes.AddRange([0, 0]);
        data();
        var length = _bytes.Count - lengthAt - 2;
        _bytes[lengthAt] = (byte)(length >> 8);
        _bytes[lengthAt + 1] = (byte)length;
        return this;
    }

    private void Name(string name)
    {
        var labels = name.TrimEnd('.').Split('.');
        for (var i = 0; i < labels.Length; i++)
        {
            var suffix = string.Join('.', labels[i..]);
            if (_names.TryGetValue(suffix, out var pointer))
            {
                _bytes.Add((byte)(0xC0 | (pointer >> 8)));
                _bytes.Add((byte)pointer);
                return;
            }
            if (_bytes.Count < 0x3FFF) _names[suffix] = _bytes.Count;
            var b = Encoding.UTF8.GetBytes(labels[i]);
            _bytes.Add((byte)b.Length);
            _bytes.AddRange(b);
        }
        _bytes.Add(0);
    }
}

/// <summary>An in-memory camera with a few UVC controls.</summary>
internal sealed class FakeWebcamBackend : IWebcamBackend
{
    public List<WebcamDeviceInfo> Devices { get; } = [new("usb-0000:00:14.0-3", "Elgato Facecam", "/dev/video0")];

    public Dictionary<WebcamControl, WebcamRange> Ranges { get; } = new()
    {
        [WebcamControl.Brightness] = new(0, 255, 1, 128),
        [WebcamControl.Contrast] = new(0, 10, 1, 3),
        [WebcamControl.Sharpness] = new(0, 4, 1, 2),
        [WebcamControl.WhiteBalance] = new(2800, 12500, 10, 5000),
        [WebcamControl.AutoWhiteBalance] = new(0, 1, 1, 1),
        [WebcamControl.AntiFlicker] = new(0, 2, 1, 2),
        [WebcamControl.Zoom] = new(100, 400, 1, 100),
        [WebcamControl.AutoFocus] = new(0, 1, 1, 1),
        [WebcamControl.Mirror] = new(0, 1, 1, 0),
    };

    public ConcurrentDictionary<WebcamControl, int> Values { get; } = new()
    {
        [WebcamControl.Brightness] = 128,
        [WebcamControl.Contrast] = 3,
        [WebcamControl.Sharpness] = 2,
        [WebcamControl.WhiteBalance] = 5000,
        [WebcamControl.AutoWhiteBalance] = 0,
        [WebcamControl.AntiFlicker] = 2,
        [WebcamControl.Zoom] = 100,
        [WebcamControl.AutoFocus] = 1,
        [WebcamControl.Mirror] = 0,
    };

    public string Description => "fake";

    public IReadOnlyList<WebcamDeviceInfo> Enumerate() => [.. Devices];

    public WebcamRange? Query(WebcamDeviceInfo device, WebcamControl control) => Ranges.GetValueOrDefault(control);

    public int Get(WebcamDeviceInfo device, WebcamControl control) => Values[control];

    public void Set(WebcamDeviceInfo device, WebcamControl control, int value) => Values[control] = value;
}

internal sealed class TestEnvironment : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cuebot-elgato-tests-" + Guid.NewGuid().ToString("N"));

    public TestEnvironment()
    {
        Directory.CreateDirectory(_dir);
        Settings = new JsonFile<ElgatoSettings>(Path.Combine(_dir, "elgato.json"), Log);
        Settings.Value.Lights.PollIntervalMs = 100;
        Settings.Value.Lights.Discovery = false;
        Settings.Value.CameraHub.PollIntervalMs = 250;
        Bus = new EventBus(Log);
        Bus.Subscribe(Published.Enqueue);
    }

    public AppLog Log { get; } = new();
    public EventBus Bus { get; }
    public JsonFile<ElgatoSettings> Settings { get; }
    public ConcurrentQueue<BotEvent> Published { get; } = new();
    public FakeMdnsTransport Mdns { get; } = new();

    public ElgatoLightsService Lights() => new(Settings, Bus, Log, mdns: Mdns, discoveryWindow: TimeSpan.FromMilliseconds(50));

    public BotEvent[] Of(string type) => Published.Where(e => e.Type == type).ToArray();

    public void Dispose()
    {
        Settings.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
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
