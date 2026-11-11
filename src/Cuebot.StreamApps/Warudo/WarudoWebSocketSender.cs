using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Warudo;

/// <summary>
/// A send-only connection to Warudo's own WebSocket endpoint (ws://localhost:19190). A connection without a
/// <c>Channel</c> header goes to Warudo's raw-message pipeline, which fires every "On WebSocket Message Received"
/// node with the text (docs.warudo.app, scripting/api/external-callbacks). It is opened on the first message, kept
/// open, and opened again on the next message after Warudo closed it. Anything Warudo sends back is read and ignored.
/// </summary>
internal sealed class WarudoWebSocketSender(Func<IWebSocketTransport> transports, StreamAppsTimings timings) : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IWebSocketTransport? _socket;
    private Uri? _url;
    private CancellationTokenSource? _drainStop;
    private Task _drain = Task.CompletedTask;
    private volatile bool _closed = true;

    /// <summary>True while a connection is open (it opens on the first message).</summary>
    public bool IsOpen => !_closed;

    /// <summary>Sends one text message, connecting (or reconnecting once) as needed.</summary>
    public async Task SendAsync(Uri url, string text, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                if (_closed || _socket is null || _url != url) await OpenAsync(url, ct).ConfigureAwait(false);
                try
                {
                    await _socket!.SendAsync(text, ct).ConfigureAwait(false);
                    return;
                }
                catch (Exception ex) when (attempt == 0 && ex is not OperationCanceledException)
                {
                    // Warudo closed the connection since the last message (e.g. it restarted): open a new one, once.
                    await CloseAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task OpenAsync(Uri url, CancellationToken ct)
    {
        await CloseAsync().ConfigureAwait(false);
        var socket = transports();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(timings.ConnectTimeout);
            await socket.ConnectAsync(url, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            await socket.DisposeAsync().ConfigureAwait(false);
            throw new StreamAppRequestException(
                $"Warudo's WebSocket port is not reachable at {url.Authority} ({(ex is OperationCanceledException ? "timed out" : ex.Message)}). Is Warudo open?");
        }
        _socket = socket;
        _url = url;
        _closed = false;
        var stop = new CancellationTokenSource();
        _drainStop = stop;
        _drain = DrainAsync(socket, stop.Token);
    }

    private async Task DrainAsync(IWebSocketTransport socket, CancellationToken ct)
    {
        try
        {
            while (await socket.ReceiveAsync(ct).ConfigureAwait(false) is not null)
            {
                // Nothing Warudo sends on this connection is used.
            }
        }
        catch (Exception)
        {
            // Closed or cancelled: the next message opens a new connection.
        }
        if (!ct.IsCancellationRequested) _closed = true;
    }

    private async Task CloseAsync()
    {
        _closed = true;
        var socket = _socket;
        _socket = null;
        if (_drainStop is { } stop)
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await _drain.ConfigureAwait(false);
            stop.Dispose();
            _drainStop = null;
        }
        if (socket is not null) await socket.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            await CloseAsync().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }
}
