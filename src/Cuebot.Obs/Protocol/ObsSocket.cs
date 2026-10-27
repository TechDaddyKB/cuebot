using System.Net.WebSockets;
using System.Text.Json;

namespace Cuebot.Obs.Protocol;

/// <summary>
/// Frames obs-websocket messages over a <see cref="WebSocket"/>: serializes sends (a WebSocket allows only one
/// at a time) and reassembles fragmented text messages. Only one caller may receive at a time.
/// </summary>
internal sealed class ObsSocket(WebSocket socket) : IDisposable
{
    private const int ChunkSize = 16 * 1024;
    private static readonly TimeSpan CloseHandshakeTimeout = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly byte[] _chunk = new byte[ChunkSize];

    public WebSocketCloseStatus? CloseStatus => socket.CloseStatus;
    public string? CloseStatusDescription => socket.CloseStatusDescription;

    public async Task SendAsync(ObsOpCode op, object data, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { op = (int)op, d = data });
        await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Receives the next message, or null when the peer closed the connection.</summary>
    public async Task<ObsMessage?> ReceiveAsync(CancellationToken ct)
    {
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(_chunk, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                await CloseOutputAsync().ConfigureAwait(false);
                return null;
            }
            message.Write(_chunk, 0, result.Count);
            if (result.EndOfMessage) return ObsMessage.Parse(message.GetBuffer().AsMemory(0, (int)message.Length));
        }
    }

    /// <summary>Sends a close frame if the socket is still open; never throws.</summary>
    public async Task CloseOutputAsync()
    {
        if (socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            using var timeout = new CancellationTokenSource(CloseHandshakeTimeout);
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Client disconnecting", timeout.Token)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
            // The connection is already gone; there is nothing left to close.
        }
    }

    // The send lock is deliberately not disposed: a racing sender may still release it, and a SemaphoreSlim
    // that never handed out its wait handle holds no unmanaged resources.
    public void Dispose() => socket.Dispose();
}
