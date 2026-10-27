using System.Buffers;
using System.Net.WebSockets;
using System.Text;

namespace Cuebot.Server.Hosting;

/// <summary>One connected WebSocket peer: serialized sends and a text receive loop.</summary>
public sealed class WebSocketConnection : IAsyncDisposable
{
    public const int MaxMessageBytes = 1024 * 1024;

    private readonly WebSocket _socket;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    internal WebSocketConnection(WebSocket socket, string remote)
    {
        _socket = socket;
        Remote = remote;
    }

    public string Id { get; } = Guid.NewGuid().ToString();
    public string Remote { get; }
    public DateTimeOffset ConnectedAt { get; } = DateTimeOffset.Now;
    public bool IsOpen => _socket.State == WebSocketState.Open;

    /// <summary>Sends a text message; returns false when the socket is gone.</summary>
    public async Task<bool> SendAsync(string text, CancellationToken ct = default)
    {
        if (!IsOpen) return false;
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            try { _sendLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    /// <summary>Receives text messages until the peer closes or <paramref name="ct"/> is cancelled.</summary>
    public async Task ReceiveAsync(Func<string, Task> onMessage, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        using var message = new MemoryStream();
        try
        {
            while (!ct.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                var result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) break;
                message.Write(buffer, 0, result.Count);
                if (message.Length > MaxMessageBytes)
                {
                    await CloseAsync(WebSocketCloseStatus.MessageTooBig, "Message too big").ConfigureAwait(false);
                    break;
                }
                if (!result.EndOfMessage) continue;
                var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                message.SetLength(0);
                await onMessage(text).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Peer went away or the server is stopping.
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task CloseAsync(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure, string reason = "")
    {
        try
        {
            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(status, reason, timeout.Token).ConfigureAwait(false);
                // A peer that never answers the close would keep the receive loop (and the session) alive.
                _ = Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ =>
                {
                    try { if (_socket.State != WebSocketState.Closed) _socket.Abort(); }
                    catch (ObjectDisposedException) { }
                }, TaskScheduler.Default);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or IOException or ObjectDisposedException)
        {
            _socket.Abort();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CloseAsync().ConfigureAwait(false);
        _socket.Dispose();
        _sendLock.Dispose();
    }
}
