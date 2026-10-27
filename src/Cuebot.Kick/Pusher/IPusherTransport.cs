using System.Net.WebSockets;
using System.Text;

namespace Cuebot.Kick.Pusher;

/// <summary>A text-message websocket. Abstracted so the Pusher protocol can be tested without a network.</summary>
internal interface IPusherTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri url, CancellationToken ct);

    Task SendAsync(string text, CancellationToken ct);

    /// <summary>The next complete text message, or null once the server closed the connection.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);

    /// <summary>Why the connection ended, when known.</summary>
    string? CloseReason { get; }

    /// <summary>The websocket close code , when the server closed the connection.</summary>
    int? CloseCode { get; }
}

/// <summary><see cref="IPusherTransport"/> over <see cref="ClientWebSocket"/>.</summary>
internal sealed class ClientWebSocketTransport : IPusherTransport
{
    private const int MaxMessageBytes = 1024 * 1024;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public ClientWebSocketTransport() =>
        _socket.Options.SetRequestHeader("User-Agent", Api.KickWebClient.BrowserUserAgent);

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
                    ? $"Kick's real-time server closed the connection ({(int)status}{(string.IsNullOrEmpty(_socket.CloseStatusDescription) ? "" : " " + _socket.CloseStatusDescription)})"
                    : "Kick's real-time server closed the connection";
                return null;
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes) throw new InvalidDataException("Real-time message too large");
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    /// <summary>The websocket close code, used to decide how to reconnect.</summary>
    public int? CloseCode => _socket.CloseStatus is { } s ? (int)s : null;

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
