using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Cuebot.StreamApps.Obsbot;

namespace Cuebot.StreamApps.Tests.Support;

/// <summary>
/// An in-process OBSBOT Center OSC server on 127.0.0.1 (UDP or TCP, any free port) built from OBSBOT's published OSC
/// definition: answers the "Connected" hello with ConnectedResp + DeviceInfo and the Get* queries with the state below,
/// and records every message it receives.
/// </summary>
internal sealed class FakeObsbotCenter : IAsyncDisposable
{
    private readonly Channel<OscMessage> _received = Channel.CreateUnbounded<OscMessage>();
    private readonly CancellationTokenSource _stop = new();
    private readonly Socket? _udp;
    private readonly TcpListener? _tcp;
    private readonly Task _loop;
    private EndPoint? _lastClient;
    private NetworkStream? _stream;

    public FakeObsbotCenter(ObsbotTransport transport)
    {
        if (transport == ObsbotTransport.Udp)
        {
            _udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            _udp.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_udp.LocalEndPoint!).Port;
            _loop = Task.Run(UdpLoopAsync);
        }
        else
        {
            _tcp = new TcpListener(IPAddress.Loopback, 0);
            _tcp.Start();
            Port = ((IPEndPoint)_tcp.LocalEndpoint).Port;
            _loop = Task.Run(TcpLoopAsync);
        }
    }

    public int Port { get; }

    /// <summary>Do not answer anything (Center with OSC off, or replies going elsewhere).</summary>
    public volatile bool Silent;

    /// <summary>
    /// Behave like a Tail 2's own OSC server (OBSBOT's Tail 2 sheet, plus what Bitfocus Companion's module reports from
    /// the real camera): Get* queries are ignored, every Connected is answered with the whole state, ZoomInfo carries
    /// fov -1 and the gimbal reply has two values, [pitch, yaw].
    /// </summary>
    public volatile bool Tail2Hardware;

    public int Zoom { get; set; } = 20;
    public int Fov { get; set; }
    public int AiLocked { get; set; }
    public int Selected { get; set; }
    public string DeviceName { get; set; } = "OBSBOT Tiny 2";

    public int TcpClients { get; private set; }

    public async Task<OscMessage> NextAsync(string? address = null)
    {
        using var cts = new CancellationTokenSource(Wait.Timeout);
        while (true)
        {
            var m = await _received.Reader.ReadAsync(cts.Token);
            if (address is null || m.Address == address) return m;
        }
    }

    /// <summary>Drops everything received so far.</summary>
    public void Clear()
    {
        while (_received.Reader.TryRead(out _))
        {
        }
    }

    public Task PushAsync(OscMessage message) => SendAsync(message);

    /// <summary>TCP: closes the current client connection (Center quitting).</summary>
    public void DropClient()
    {
        _stream?.Dispose();
        _stream = null;
    }

    private IEnumerable<OscMessage> Answer(OscMessage m)
    {
        const string g = ObsbotProtocol.General;
        if (Tail2Hardware)
        {
            if (m.Address == ObsbotProtocol.Connected)
            {
                yield return new OscMessage(ObsbotProtocol.ConnectedResp, 1);
                yield return new OscMessage(ObsbotProtocol.DeviceInfo, 1, "OBSBOT Tail 2", 0, "", 0, "", 0, "", 0, 1, 0);
                yield return new OscMessage(ObsbotProtocol.ZoomInfo, Zoom, -1);
                yield return new OscMessage(ObsbotProtocol.GimbalPosInfo, -8, 40);
            }
            yield break;
        }
        switch (m.Address)
        {
            case ObsbotProtocol.Connected:
                yield return new OscMessage(ObsbotProtocol.ConnectedResp, 1);
                yield return DeviceInfo();
                break;
            case g + "GetDeviceInfo":
                yield return DeviceInfo();
                break;
            case g + "GetZoomInfo":
                yield return new OscMessage(ObsbotProtocol.ZoomInfo, Zoom, Fov);
                break;
            case g + "GetGimbalPosInfo":
                yield return new OscMessage(ObsbotProtocol.GimbalPosInfo, 0, -10, 25);
                break;
            case ObsbotProtocol.Tiny + "GetAiTrackingInfo":
                yield return new OscMessage(ObsbotProtocol.AiTrackingInfo, AiLocked);
                break;
            case ObsbotProtocol.Tiny + "GetPresetPositionInfo":
                yield return new OscMessage(ObsbotProtocol.TinyPresetInfo, 1, "Desk", 1, "Whiteboard", 0, "");
                break;
            case g + "SetZoom":
                Zoom = m.Int(m.Args.Count - 1) ?? Zoom;
                break;
            case ObsbotProtocol.Tiny + "ToggleAILock":
                AiLocked = m.Int(m.Args.Count - 1) ?? AiLocked;
                break;
        }
    }

    private OscMessage DeviceInfo() =>
        new(ObsbotProtocol.DeviceInfo, 1, DeviceName, 1, "OBSBOT Meet 2", 0, "", 0, "", Selected, 1, 0);

    private async Task UdpLoopAsync()
    {
        var buffer = new byte[65536];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var r = await _udp!.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), _stop.Token);
                _lastClient = r.RemoteEndPoint;
                foreach (var m in Osc.Decode(buffer.AsSpan(0, r.ReceivedBytes))) await HandleAsync(m);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopped.
        }
    }

    private async Task TcpLoopAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                using var client = await _tcp!.AcceptTcpClientAsync(_stop.Token);
                TcpClients++;
                _stream = client.GetStream();
                var decoder = new OscStreamDecoder();
                var buffer = new byte[4096];
                try
                {
                    while (_stream is { } s && await s.ReadAsync(buffer, _stop.Token) is var n and > 0)
                        foreach (var m in decoder.Push(buffer.AsSpan(0, n))) await HandleAsync(m);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // Client dropped.
                }
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Stopped.
        }
    }

    private async Task HandleAsync(OscMessage m)
    {
        _received.Writer.TryWrite(m);
        if (Silent) return;
        foreach (var reply in Answer(m).ToArray()) await SendAsync(reply);
    }

    private async Task SendAsync(OscMessage message)
    {
        var packet = Osc.Encode(message);
        if (_udp is not null && _lastClient is { } client) await _udp.SendToAsync(packet, SocketFlags.None, client);
        else if (_stream is { } s) await s.WriteAsync(Osc.Frame(packet));
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _udp?.Dispose();
        _tcp?.Stop();
        _stream?.Dispose();
        try { await _loop; }
        catch (Exception) { /* stopped */ }
        _stop.Dispose();
    }
}
