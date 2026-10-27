using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;

namespace Cuebot.StreamApps.Common;

/// <summary>
/// A tiny RFC 6455 websocket listener on one address/port/path (TcpListener + <see cref="WebSocket.CreateFromStream(Stream, WebSocketCreationOptions)"/>):
/// cross-platform, no URL ACLs or admin rights, nothing else to host. Browser pages from other sites are refused (Origin check),
/// so a website cannot drive Cuebot through a local app's socket.
/// </summary>
internal sealed class LocalWebSocketServer : IAsyncDisposable
{
    private const string Rfc6455Guid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaxHeaderBytes = 16 * 1024;
    private readonly TcpListener _listener;
    private readonly string _path;

    private LocalWebSocketServer(TcpListener listener, string path)
    {
        _listener = listener;
        _path = path;
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <summary>"127.0.0.1"/"localhost" (default), "0.0.0.0"/"*" (all interfaces) or an IP address.</summary>
    public static IPAddress ParseAddress(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length == 0 || string.Equals(t, "localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        if (t is "*" or "+") return IPAddress.Any;
        return IPAddress.TryParse(t, out var address) ? address : throw new FormatException($"'{t}' is not an IP address.");
    }

    public static string NormalizePath(string? path) => "/" + (path ?? "").Trim().Trim('/');

    /// <summary>Starts listening; throws <see cref="SocketException"/> when the port is taken.</summary>
    public static LocalWebSocketServer Start(IPAddress address, int port, string path)
    {
        var listener = new TcpListener(address, port);
        listener.Start();
        return new LocalWebSocketServer(listener, NormalizePath(path));
    }

    /// <summary>The next client that completed the websocket upgrade on the right path.</summary>
    public async Task<(WebSocket Socket, string Remote)> AcceptAsync(CancellationToken ct)
    {
        while (true)
        {
            var client = await _listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            var remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                if (await UpgradeAsync(client, timeout.Token).ConfigureAwait(false) is { } socket) return (socket, remote);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException && !ct.IsCancellationRequested)
            {
                // A client that hung up or never finished its request.
            }
            client.Dispose();
        }
    }

    private async Task<WebSocket?> UpgradeAsync(TcpClient client, CancellationToken ct)
    {
        var stream = client.GetStream();
        var header = await ReadHeaderAsync(stream, ct).ConfigureAwait(false);
        if (header is null) return null;
        var lines = header.Split("\r\n");
        var request = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
            if (line.IndexOf(':') is var colon and > 0) headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();

        if (request.Length < 2 || request[0] != "GET")
            return await RefuseAsync(stream, "405 Method Not Allowed", ct).ConfigureAwait(false);
        var path = NormalizePath(request[1].Split('?')[0]);
        if (!string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
            return await RefuseAsync(stream, "404 Not Found", ct).ConfigureAwait(false);
        if (!headers.TryGetValue("Upgrade", out var upgrade) || !upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase)
            || !headers.TryGetValue("Sec-WebSocket-Key", out var key) || key.Length == 0)
            return await RefuseAsync(stream, "426 Upgrade Required", ct).ConfigureAwait(false);
        if (headers.TryGetValue("Origin", out var origin) && !IsLocalOrigin(origin))
            return await RefuseAsync(stream, "403 Forbidden", ct).ConfigureAwait(false);

        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + Rfc6455Guid)));
        var response = "HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                       $"Sec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), ct).ConfigureAwait(false);
        return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions { IsServer = true, KeepAliveInterval = TimeSpan.FromSeconds(30) });
    }

    /// <summary>No Origin (native apps such as PolyPop), "null" (local files) or a page on this PC.</summary>
    internal static bool IsLocalOrigin(string origin)
    {
        var o = origin.Trim();
        if (o.Length == 0 || o == "null") return true;
        return Uri.TryCreate(o, UriKind.Absolute, out var uri)
               && (uri.IsLoopback || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<string?> ReadHeaderAsync(NetworkStream stream, CancellationToken ct)
    {
        // Byte by byte so nothing after the header is consumed (the client sends no frames before our 101 anyway).
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;
        var one = new byte[1];
        while (length < buffer.Length)
        {
            if (await stream.ReadAsync(one, ct).ConfigureAwait(false) == 0) return null;
            buffer[length++] = one[0];
            if (length >= 4 && buffer[length - 4] == '\r' && buffer[length - 3] == '\n' && buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
                return Encoding.ASCII.GetString(buffer, 0, length - 4);
        }
        return null;
    }

    private static async Task<WebSocket?> RefuseAsync(NetworkStream stream, string status, CancellationToken ct)
    {
        var body = $"HTTP/1.1 {status}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(body), ct).ConfigureAwait(false);
        return null;
    }

    public ValueTask DisposeAsync()
    {
        _listener.Stop();
        _listener.Dispose();
        return ValueTask.CompletedTask;
    }
}
