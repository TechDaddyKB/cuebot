using System.Net.WebSockets;
using System.Text;

namespace Cuebot.Donations.Transport;

/// <summary>A text-message websocket. Abstracted so the protocols can be tested without a network.</summary>
internal interface IWebSocketTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri url, CancellationToken ct);

    Task SendAsync(string text, CancellationToken ct);

    /// <summary>The next complete text message, or null once the server closed the connection.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);

    /// <summary>Why the connection ended, when known.</summary>
    string? CloseReason { get; }
}

/// <summary>
/// <see cref="IWebSocketTransport"/> over <see cref="ClientWebSocket"/>. Websocket PING control frames (Astro sends
/// one every 30 s) are answered by the runtime; our own keep-alive pings detect dead connections.
/// </summary>
internal sealed class ClientWebSocketTransport : IWebSocketTransport
{
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public ClientWebSocketTransport(TimeSpan keepAlive)
    {
        _socket.Options.SetRequestHeader("User-Agent", "Cuebot");
        _socket.Options.KeepAliveInterval = keepAlive;
        _socket.Options.KeepAliveTimeout = keepAlive;
    }

    public string? CloseReason { get; private set; }

    public Task ConnectAsync(Uri url, CancellationToken ct) => _socket.ConnectAsync(url, ct);

    public async Task SendAsync(string text, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task<string?> ReceiveAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                CloseReason = _socket.CloseStatus is { } status
                    ? $"server closed the connection ({(int)status}{(string.IsNullOrEmpty(_socket.CloseStatusDescription) ? "" : " " + _socket.CloseStatusDescription)})"
                    : "server closed the connection";
                return null;
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes) throw new InvalidDataException("websocket message too large");
            if (!result.EndOfMessage) continue;
            if (result.MessageType == WebSocketMessageType.Binary)
            {
                message.SetLength(0); // Socket.IO binary attachments are not used by these services.
                continue;
            }
            return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var timeout = new CancellationTokenSource(CloseTimeout);
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
                // Best effort.
            }
        }
        _socket.Dispose();
        _sendGate.Dispose();
    }
}

/// <summary>Reads a transport into a channel so callers can wait with timeouts without losing messages.</summary>
internal sealed class TransportInbox : IAsyncDisposable
{
    private readonly System.Threading.Channels.Channel<string?> _inbox = System.Threading.Channels.Channel.CreateUnbounded<string?>();
    private readonly CancellationTokenSource _stop;
    private readonly Task _pump;

    public TransportInbox(IWebSocketTransport transport, CancellationToken ct)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _pump = PumpAsync(transport, _stop.Token);
    }

    public readonly record struct Read(bool TimedOut, string? Text);

    /// <summary>The next message; <see cref="Read.TimedOut"/> after <paramref name="timeout"/>; Text null when closed.</summary>
    public async Task<Read> ReadAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (timeout != Timeout.InfiniteTimeSpan) cts.CancelAfter(timeout < TimeSpan.Zero ? TimeSpan.Zero : timeout);
        try
        {
            return new Read(false, await _inbox.Reader.ReadAsync(cts.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new Read(true, null);
        }
        catch (System.Threading.Channels.ChannelClosedException)
        {
            return new Read(false, null);
        }
    }

    private async Task PumpAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        try
        {
            while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text) _inbox.Writer.TryWrite(text);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Treated as a closed connection.
        }
        catch (OperationCanceledException)
        {
            // Stopping.
        }
        _inbox.Writer.TryWrite(null);
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Teardown only.
        }
        _stop.Dispose();
    }
}
