using System.Net.WebSockets;
using System.Text;
using System.Threading.Channels;

namespace Cuebot.Twitch.EventSub;

/// <summary>A received text message, or (when <see cref="Text"/> is null) the end of a connection.</summary>
internal sealed record EventSubFrame(EventSubConnection Source, string? Text, string? CloseReason = null);

/// <summary>
/// One WebSocket connection. A background pump reassembles multi-frame messages and writes them, followed by a
/// final close frame, to a shared channel so a session can read the old and new connection during a reconnect.
/// </summary>
internal sealed class EventSubConnection : IAsyncDisposable
{
    private const int MaxMessageBytes = 1024 * 1024;
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(2);

    private readonly ClientWebSocket _socket = new();
    private readonly CancellationTokenSource _stop = new();
    private Task _pump = Task.CompletedTask;

    private EventSubConnection()
    {
    }

    public static async Task<EventSubConnection> OpenAsync(Uri url, ChannelWriter<EventSubFrame> frames, CancellationToken ct)
    {
        var connection = new EventSubConnection();
        try
        {
            await connection._socket.ConnectAsync(url, ct).ConfigureAwait(false);
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        connection._pump = connection.PumpAsync(frames);
        return connection;
    }

    private async Task PumpAsync(ChannelWriter<EventSubFrame> frames)
    {
        string closeReason;
        try
        {
            while (await ReceiveTextAsync(_stop.Token).ConfigureAwait(false) is { } text)
                await frames.WriteAsync(new EventSubFrame(this, text), _stop.Token).ConfigureAwait(false);
            closeReason = DescribeClose();
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            closeReason = ex.Message;
        }
        frames.TryWrite(new EventSubFrame(this, null, closeReason));
    }

    /// <summary>Returns the next complete text message, or null when the server closed the connection.</summary>
    private async Task<string?> ReceiveTextAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await _socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            message.Write(buffer, 0, result.Count);
            if (message.Length > MaxMessageBytes) throw new InvalidDataException("EventSub message too large");
            if (result.EndOfMessage) return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
        }
    }

    private string DescribeClose()
    {
        if (_socket.CloseStatus is not { } status) return "Twitch closed the connection";
        var description = string.IsNullOrEmpty(_socket.CloseStatusDescription) ? "" : $" {_socket.CloseStatusDescription}";
        return $"Twitch closed the connection ({(int)status}{description})";
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
                // Best effort: the connection is being abandoned either way.
            }
        }
        await _stop.CancelAsync().ConfigureAwait(false);
        await _pump.ConfigureAwait(false);
        _socket.Dispose();
        _stop.Dispose();
    }
}
