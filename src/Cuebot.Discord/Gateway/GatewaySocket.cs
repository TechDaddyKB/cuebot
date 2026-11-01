using System.Net.WebSockets;
using System.Text;

namespace Cuebot.Discord.Gateway;

/// <summary>A text websocket to the Discord Gateway. Abstracted so the protocol can be tested against an in-memory fake.</summary>
internal interface IGatewaySocket : IAsyncDisposable
{
    Task ConnectAsync(Uri url, CancellationToken ct);

    Task SendAsync(string text, CancellationToken ct);

    /// <summary>The next complete text message, or null once the connection closed.</summary>
    Task<string?> ReceiveAsync(CancellationToken ct);

    /// <summary>The close code Discord sent (4004, 4014...), when it closed the connection.</summary>
    int? CloseCode { get; }

    /// <summary>Why the connection ended, when known.</summary>
    string? CloseReason { get; }

    /// <summary>
    /// Closes the connection. Any code other than 1000/1001 keeps the session resumable (Discord: closing with 1000 or
    /// 1001 invalidates it and shows the bot offline).
    /// </summary>
    Task CloseAsync(int code, CancellationToken ct);
}

/// <summary><see cref="IGatewaySocket"/> over <see cref="ClientWebSocket"/>.</summary>
internal sealed class ClientGatewaySocket : IGatewaySocket
{
    private const int MaxMessageBytes = 32 * 1024 * 1024;
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    public ClientGatewaySocket()
    {
        _socket.Options.SetRequestHeader("User-Agent", DiscordRestClient.UserAgent);
    }

    public int? CloseCode { get; private set; }
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
                CloseCode = _socket.CloseStatus is { } status ? (int)status : null;
                CloseReason = CloseCode is { } code
                    ? $"closed by Discord ({code}{(string.IsNullOrEmpty(_socket.CloseStatusDescription) ? "" : " " + _socket.CloseStatusDescription)})"
                    : "closed by Discord";
                return null;
            }
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes) throw new InvalidDataException("Gateway message too large");
            if (!result.EndOfMessage) continue;
            return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    public async Task CloseAsync(int code, CancellationToken ct)
    {
        if (_socket.State is not (WebSocketState.Open or WebSocketState.CloseReceived)) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(2));
            await _socket.CloseOutputAsync((WebSocketCloseStatus)code, null, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            // Best effort; aborting below ends the receive loop either way.
        }
        _socket.Abort();
    }

    public ValueTask DisposeAsync()
    {
        _socket.Dispose();
        _sendGate.Dispose();
        return ValueTask.CompletedTask;
    }
}
