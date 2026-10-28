using System.Net;
using System.Net.Sockets;

namespace Cuebot.StreamApps.Obsbot;

/// <summary>An OSC connection to OBSBOT Center (or a Tail camera). Abstracted so the service is tested in memory.</summary>
internal interface IOscTransport : IAsyncDisposable
{
    /// <summary>Opens the socket (TCP: connects; UDP: binds the reply port). Throws when that is impossible.</summary>
    Task OpenAsync(string host, int port, int listenPort, CancellationToken ct);

    Task SendAsync(OscMessage message, CancellationToken ct);

    /// <summary>The next message from the peer, or null once the connection closed (TCP only; UDP never closes).</summary>
    Task<OscMessage?> ReceiveAsync(CancellationToken ct);

    /// <summary>What the transport is talking to, for status lines (e.g. "udp://127.0.0.1:16284").</summary>
    string Display { get; }

    /// <summary>Why the connection ended, when known.</summary>
    string? CloseReason { get; }
}

internal static class OscTransports
{
    public static IOscTransport Create(ObsbotTransport transport) =>
        transport == ObsbotTransport.Tcp ? new TcpOscTransport() : new UdpOscTransport();

    internal static async Task<IPAddress> ResolveAsync(string host, CancellationToken ct)
    {
        if (IPAddress.TryParse(host, out var ip)) return ip;
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        var addresses = await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) ?? addresses.FirstOrDefault()
               ?? throw new SocketException((int)SocketError.HostNotFound);
    }
}

/// <summary>
/// OSC over UDP: one socket bound to the reply port (OBSBOT Center answers there), sending from it to host:port.
/// Datagrams from any other address than the configured host are ignored.
/// </summary>
internal sealed class UdpOscTransport : IOscTransport
{
    private readonly Queue<OscMessage> _received = new();
    private Socket? _socket;
    private IPEndPoint? _remote;

    public string Display => _remote is null ? "udp" : $"udp://{_remote}";

    public string? CloseReason { get; private set; }

    /// <summary>The local port actually bound (differs from the setting when it was 0 or taken).</summary>
    public int LocalPort => (_socket?.LocalEndPoint as IPEndPoint)?.Port ?? 0;

    public async Task OpenAsync(string host, int port, int listenPort, CancellationToken ct)
    {
        var address = await OscTransports.ResolveAsync(host, ct).ConfigureAwait(false);
        _remote = new IPEndPoint(address, port);
        var socket = new Socket(address.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        var bindTo = IPAddress.IsLoopback(address) ? (address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Loopback : IPAddress.Loopback)
            : address.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any;
        try
        {
            socket.Bind(new IPEndPoint(bindTo, listenPort));
        }
        catch (SocketException) when (listenPort != 0)
        {
            // The reply port is taken (another OSC tool?): fall back to any free port; replies may then not arrive.
            socket.Bind(new IPEndPoint(bindTo, 0));
            CloseReason = $"reply port {listenPort} is in use, listening on {((IPEndPoint)socket.LocalEndPoint!).Port}";
        }
        if (OperatingSystem.IsWindows())
        {
            // Without this an ICMP "port unreachable" (Center not running) makes the next ReceiveFrom throw forever.
            const int SioUdpConnreset = -1744830452;
            socket.IOControl(SioUdpConnreset, [0, 0, 0, 0], null);
        }
        _socket = socket;
    }

    public async Task SendAsync(OscMessage message, CancellationToken ct)
    {
        if (_socket is null || _remote is null) throw new InvalidOperationException("The OSC socket is not open.");
        await _socket.SendToAsync(Osc.Encode(message), SocketFlags.None, _remote, ct).ConfigureAwait(false);
    }

    public async Task<OscMessage?> ReceiveAsync(CancellationToken ct)
    {
        if (_socket is null || _remote is null) return null;
        if (_received.Count > 0) return _received.Dequeue();
        var buffer = new byte[Osc.MaxPacketLength + 1];
        while (true)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await _socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(_remote.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0), ct)
                    .ConfigureAwait(false);
            }
            catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset)
            {
                continue;
            }
            if (result.RemoteEndPoint is IPEndPoint from && !SameHost(from.Address, _remote.Address)) continue;
            IReadOnlyList<OscMessage> messages;
            try
            {
                messages = Osc.Decode(buffer.AsSpan(0, result.ReceivedBytes));
            }
            catch (FormatException)
            {
                continue;
            }
            foreach (var m in messages) _received.Enqueue(m);
            if (_received.Count > 0) return _received.Dequeue();
        }
    }

    private static bool SameHost(IPAddress a, IPAddress b) =>
        a.Equals(b) || (IPAddress.IsLoopback(a) && IPAddress.IsLoopback(b))
                    || (a.IsIPv4MappedToIPv6 && a.MapToIPv4().Equals(b)) || (b.IsIPv4MappedToIPv6 && b.MapToIPv4().Equals(a));

    public ValueTask DisposeAsync()
    {
        _socket?.Dispose();
        _socket = null;
        return ValueTask.CompletedTask;
    }
}

/// <summary>OSC over TCP with OSC 1.0 framing (each packet prefixed with its int32 length).</summary>
internal sealed class TcpOscTransport : IOscTransport
{
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly Queue<OscMessage> _received = new();
    private readonly OscStreamDecoder _decoder = new();
    private TcpClient? _client;
    private NetworkStream? _stream;
    private string _display = "tcp";

    public string Display => _display;

    public string? CloseReason { get; private set; }

    public async Task OpenAsync(string host, int port, int listenPort, CancellationToken ct)
    {
        _display = $"tcp://{host}:{port}";
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(host, port, ct).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _client = client;
        _stream = client.GetStream();
    }

    public async Task SendAsync(OscMessage message, CancellationToken ct)
    {
        var stream = _stream ?? throw new InvalidOperationException("The OSC connection is not open.");
        var frame = Osc.Frame(Osc.Encode(message));
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(frame, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task<OscMessage?> ReceiveAsync(CancellationToken ct)
    {
        var stream = _stream;
        if (stream is null) return null;
        var buffer = new byte[16 * 1024];
        while (_received.Count == 0)
        {
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (IOException ex) when (!ct.IsCancellationRequested)
            {
                CloseReason = ex.InnerException?.Message ?? ex.Message;
                return null;
            }
            if (read == 0)
            {
                CloseReason = "closed by OBSBOT Center";
                return null;
            }
            IReadOnlyList<OscMessage> messages;
            try
            {
                messages = _decoder.Push(buffer.AsSpan(0, read));
            }
            catch (FormatException ex)
            {
                CloseReason = ex.Message;
                return null;
            }
            foreach (var m in messages) _received.Enqueue(m);
        }
        return _received.Dequeue();
    }

    public ValueTask DisposeAsync()
    {
        _stream?.Dispose();
        _client?.Dispose();
        _stream = null;
        _client = null;
        return ValueTask.CompletedTask;
    }
}
