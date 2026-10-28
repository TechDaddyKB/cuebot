using System.Net.WebSockets;
using System.Text;

namespace Cuebot.GoXlr.Protocol;

/// <summary>A text-message websocket. Abstracted so the protocol can be tested against a fake utility.</summary>
internal interface IGoXlrTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri url, CancellationToken ct);

    Task SendAsync(string text, CancellationToken ct);

    /// <summary>The next complete text message, or null once the peer closed the connection.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);

    /// <summary>Why the connection ended, when known.</summary>
    string? CloseReason { get; }
}

/// <summary><see cref="IGoXlrTransport"/> over <see cref="ClientWebSocket"/>.</summary>
internal sealed class ClientWebSocketTransport : IGoXlrTransport
{
    // The full status of a GoXLR with a big sample library is a few hundred kB.
    private const int MaxMessageBytes = 32 * 1024 * 1024;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public ClientWebSocketTransport()
    {
        _socket.Options.SetRequestHeader("User-Agent", "Cuebot");
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
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
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (WebSocketException ex) when (!ct.IsCancellationRequested)
            {
                CloseReason = ex.InnerException?.Message ?? ex.Message;
                return null;
            }
            if (result.MessageType == WebSocketMessageType.Close)
            {
                CloseReason = _socket.CloseStatus is { } status
                    ? $"closed by the GoXLR Utility ({(int)status}{(_socket.CloseStatusDescription is { Length: > 0 } d ? ": " + d : "")})"
                    : "closed by the GoXLR Utility";
                return null;
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes) throw new InvalidDataException("websocket message too large");
            if (!result.EndOfMessage) continue;
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
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
            {
                // Best effort.
            }
        }
        _socket.Dispose();
        _sendGate.Dispose();
    }
}
