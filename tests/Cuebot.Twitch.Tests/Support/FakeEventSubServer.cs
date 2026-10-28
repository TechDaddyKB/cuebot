using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Cuebot.Twitch.Tests.Support;

/// <summary>In-process WebSocket server standing in for wss://eventsub.wss.twitch.tv.</summary>
public sealed class FakeEventSubServer : IAsyncDisposable
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    private readonly HttpListener _listener;
    private readonly Channel<FakeEventSubConnection> _connections = Channel.CreateUnbounded<FakeEventSubConnection>();
    private readonly List<FakeEventSubConnection> _all = [];
    private readonly Task _acceptLoop;

    public FakeEventSubServer()
    {
        // Another test can take the probed port before it is bound, so try a few.
        for (var attempt = 1; ; attempt++)
        {
            Port = FreePort();
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{Port}/");
            try { _listener.Start(); break; }
            catch (HttpListenerException) when (attempt < 5) { _listener.Close(); }
        }
        _acceptLoop = AcceptLoopAsync();
    }

    public int Port { get; }

    public Uri Url(string path = "ws") => new($"ws://localhost:{Port}/{path}");

    public async Task<FakeEventSubConnection> NextConnectionAsync()
    {
        using var timeout = new CancellationTokenSource(Timeout);
        return await _connections.Reader.ReadAsync(timeout.Token);
    }

    private async Task AcceptLoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }
            var socket = await context.AcceptWebSocketAsync(null);
            var connection = new FakeEventSubConnection(context.Request.Url!.AbsolutePath, socket.WebSocket);
            lock (_all) _all.Add(connection);
            _connections.Writer.TryWrite(connection);
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        FakeEventSubConnection[] all;
        lock (_all) all = _all.ToArray();
        foreach (var connection in all) connection.Abort();
        _listener.Stop();
        _listener.Close();
        await _acceptLoop;
    }
}

/// <summary>The server side of one client connection.</summary>
public sealed class FakeEventSubConnection(string path, WebSocket socket)
{
    public string Path { get; } = path;

    public Task SendAsync(string json) =>
        socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>Sends one message split across several WebSocket frames.</summary>
    public async Task SendFragmentedAsync(string json, int parts)
    {
        var bytes = Encoding.UTF8.GetBytes(json);
        var size = (bytes.Length + parts - 1) / parts;
        for (var offset = 0; offset < bytes.Length; offset += size)
        {
            var count = Math.Min(size, bytes.Length - offset);
            await socket.SendAsync(bytes.AsMemory(offset, count), WebSocketMessageType.Text,
                offset + count >= bytes.Length, CancellationToken.None);
        }
    }

    public Task CloseAsync() =>
        socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);

    /// <summary>Waits until the client closes the connection; returns false on timeout.</summary>
    public async Task<bool> WaitForClientCloseAsync()
    {
        using var timeout = new CancellationTokenSource(FakeEventSubServer.Timeout);
        var buffer = new byte[4096];
        try
        {
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer, timeout.Token);
                if (result.MessageType == WebSocketMessageType.Close) return true;
            }
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (WebSocketException)
        {
            return true;
        }
    }

    public void Abort() => socket.Abort();
}
