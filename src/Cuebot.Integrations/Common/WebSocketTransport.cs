using System.Net.WebSockets;
using System.Text;

namespace Cuebot.Integrations.Common;

/// <summary>A text-message websocket. Abstracted so every protocol can be tested against in-memory fake servers.</summary>
internal interface IWebSocketTransport : IAsyncDisposable
{
    /// <param name="headers">Extra HTTP headers for the upgrade request (e.g. Authorization); may be null.</param>
    Task ConnectAsync(Uri url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct);

    Task SendAsync(string text, CancellationToken ct);

    /// <summary>The next complete text message, or null once the server closed the connection.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);

    /// <summary>Why the connection ended, when known.</summary>
    string? CloseReason { get; }
}

/// <summary><see cref="IWebSocketTransport"/> over <see cref="ClientWebSocket"/> (websocket PINGs keep idle sessions alive).</summary>
internal sealed class ClientWebSocketTransport : IWebSocketTransport
{
    private const int MaxMessageBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public ClientWebSocketTransport()
    {
        _socket.Options.SetRequestHeader("User-Agent", "Cuebot");
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
    }

    public string? CloseReason { get; private set; }

    public Task ConnectAsync(Uri url, IReadOnlyDictionary<string, string>? headers, CancellationToken ct)
    {
        foreach (var (name, value) in headers ?? new Dictionary<string, string>()) _socket.Options.SetRequestHeader(name, value);
        return _socket.ConnectAsync(url, ct);
    }

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
            // Voicemod and VTube Studio send text frames; binary frames are decoded as UTF-8 text too.
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
