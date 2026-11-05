using System.Net;
using System.Net.WebSockets;
using System.Text;

namespace Cuebot.Portal.Relay;

/// <summary>One whole WebSocket message: text (as UTF-8) or binary.</summary>
public readonly record struct RelayMessage(bool Binary, byte[] Data)
{
    public string Text => Encoding.UTF8.GetString(Data);
}

/// <summary>The WebSocket to the shared relay; abstracted so the client can be tested against an in-memory relay.</summary>
internal interface IPortalRelayTransport : IAsyncDisposable
{
    Task ConnectAsync(Uri url, CancellationToken ct);

    Task SendTextAsync(string text, CancellationToken ct);

    Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct);

    /// <summary>The next whole message, or null once the relay closed the connection.</summary>
    Task<RelayMessage?> ReceiveAsync(CancellationToken ct);

    int? CloseCode { get; }

    string? CloseReason { get; }
}

/// <summary><see cref="IPortalRelayTransport"/> over <see cref="ClientWebSocket"/>.</summary>
internal sealed class ClientWebSocketPortalTransport : IPortalRelayTransport
{
    /// <summary>The relay's largest frame to Cuebot is a sealed 1 MB webhook (base64 inside), so 4 MB is plenty.</summary>
    private const int MaxMessageBytes = 4 * 1024 * 1024;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public ClientWebSocketPortalTransport()
    {
        _socket.Options.SetRequestHeader("User-Agent", "Cuebot");
        _socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        _socket.Options.CollectHttpResponseDetails = true;
    }

    public int? CloseCode { get; private set; }
    public string? CloseReason { get; private set; }

    public async Task ConnectAsync(Uri url, CancellationToken ct)
    {
        try
        {
            await _socket.ConnectAsync(url, ct).ConfigureAwait(false);
        }
        catch (WebSocketException) when (_socket.HttpStatusCode is HttpStatusCode.NotFound)
        {
            throw new WebSocketException("The relay answered 404: is the address a Cuebot portal relay?");
        }
    }

    public Task SendTextAsync(string text, CancellationToken ct) => SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, ct);

    public Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken ct) => SendAsync(data, WebSocketMessageType.Binary, ct);

    private async Task SendAsync(ReadOnlyMemory<byte> data, WebSocketMessageType type, CancellationToken ct)
    {
        await _sendGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(data, type, true, ct).ConfigureAwait(false);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    public async Task<RelayMessage?> ReceiveAsync(CancellationToken ct)
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
            if (result.EndOfMessage) return new RelayMessage(result.MessageType == WebSocketMessageType.Binary, message.ToArray());
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
