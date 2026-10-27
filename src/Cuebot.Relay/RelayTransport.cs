using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Cuebot.Relay;

/// <summary>The relay refused the connect token (HTTP 401): reconnecting will not help until the user fixes it.</summary>
public sealed class RelayAuthException(string message) : Exception(message);

/// <summary>A text-message websocket to the relay; abstracted so the client can be tested against an in-memory fake.</summary>
internal interface IRelayTransport : IAsyncDisposable
{
    /// <exception cref="RelayAuthException">The relay answered 401 (wrong connect token).</exception>
    Task ConnectAsync(Uri url, string bearerToken, CancellationToken ct);

    Task SendAsync(string text, CancellationToken ct);

    /// <summary>The next complete text message, or null once the relay closed the connection.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);

    /// <summary>The close code the relay sent, when it closed the connection.</summary>
    int? CloseCode { get; }

    string? CloseReason { get; }
}

/// <summary><see cref="IRelayTransport"/> over <see cref="ClientWebSocket"/>.</summary>
internal sealed class ClientWebSocketRelayTransport : IRelayTransport
{
    /// <summary>Relay messages carry webhook bodies of up to 1 MB as base64 plus headers.</summary>
    private const int MaxMessageBytes = 8 * 1024 * 1024;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public ClientWebSocketRelayTransport()
    {
        _socket.Options.SetRequestHeader("User-Agent", "Cuebot");
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _socket.Options.CollectHttpResponseDetails = true;
    }

    public int? CloseCode { get; private set; }
    public string? CloseReason { get; private set; }

    public async Task ConnectAsync(Uri url, string bearerToken, CancellationToken ct)
    {
        _socket.Options.SetRequestHeader("Authorization", "Bearer " + bearerToken);
        try
        {
            await _socket.ConnectAsync(url, ct).ConfigureAwait(false);
        }
        catch (WebSocketException) when (_socket.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new RelayAuthException("The relay refused the connect token (check it in the Webhooks settings)");
        }
        catch (WebSocketException) when (_socket.HttpStatusCode is HttpStatusCode.NotFound)
        {
            throw new WebSocketException("The relay URL answered 404: is it the Cuebot relay Worker's address?");
        }
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
                CloseCode = _socket.CloseStatus is { } status ? (int)status : null;
                CloseReason = _socket.CloseStatusDescription;
                return null;
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes) throw new InvalidDataException("relay message too large");
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
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
