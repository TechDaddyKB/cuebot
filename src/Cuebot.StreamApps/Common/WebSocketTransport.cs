using System.Net.WebSockets;
using System.Text;

namespace Cuebot.StreamApps.Common;

/// <summary>A text-message websocket. Abstracted so every protocol can be tested against in-memory fake servers.</summary>
internal interface IWebSocketTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri url, CancellationToken ct);

    Task SendAsync(string text, CancellationToken ct);

    /// <summary>The next complete text message, or null once the peer closed the connection.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);

    /// <summary>Why the connection ended, when known.</summary>
    string? CloseReason { get; }
}

/// <summary>Text framing over any <see cref="WebSocket"/> (client or server side).</summary>
internal class WebSocketTextChannel(WebSocket socket) : IAsyncDisposable
{
    private const int MaxMessageBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    protected WebSocket Socket => socket;

    public string? CloseReason { get; private set; }

    public async Task SendAsync(string text, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
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
            WebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            }
            catch (WebSocketException ex) when (!ct.IsCancellationRequested)
            {
                CloseReason = ex.InnerException?.Message ?? ex.Message;
                return null;
            }
            if (result.MessageType == WebSocketMessageType.Close)
            {
                CloseReason = socket.CloseStatus is { } status
                    ? $"closed by the other side ({(int)status}{(string.IsNullOrEmpty(socket.CloseStatusDescription) ? "" : " " + socket.CloseStatusDescription)})"
                    : "closed by the other side";
                return null;
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes) throw new InvalidDataException("websocket message too large");
            if (!result.EndOfMessage) continue;
            return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    public virtual async ValueTask DisposeAsync()
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var timeout = new CancellationTokenSource(CloseTimeout);
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
            {
                // Best effort.
            }
        }
        socket.Dispose();
        _sendGate.Dispose();
        GC.SuppressFinalize(this);
    }
}

/// <summary><see cref="IWebSocketTransport"/> over <see cref="ClientWebSocket"/> (websocket PINGs keep idle sessions alive).</summary>
internal sealed class ClientWebSocketTransport() : WebSocketTextChannel(CreateSocket()), IWebSocketTransport
{
    private static ClientWebSocket CreateSocket()
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("User-Agent", "Cuebot");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        return socket;
    }

    public Task ConnectAsync(Uri url, CancellationToken ct) => ((ClientWebSocket)Socket).ConnectAsync(url, ct);
}
