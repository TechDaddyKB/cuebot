using System.Net;
using System.Net.WebSockets;
using System.Text;
using Cuebot.Core;
using Cuebot.Core.Logging;

namespace Cuebot.Remote;

/// <summary>
/// The outgoing link to one instance in remote.json: connects to its <c>/api/v1/remote</c> with its token, says hello,
/// feeds received frames to the hub, and while enabled reconnects every ReconnectSeconds after a failure or drop.
/// </summary>
internal sealed class RemoteClient(RemoteHub hub, RemoteInstanceDefinition definition, AppLog log) : IAsyncDisposable
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _loop;
    private ClientWebSocket? _socket;
    private string _lastLogged = "";

    public ConnectionState State { get; private set; } = ConnectionState.Connecting;
    public string Message { get; private set; } = "Connecting...";

    /// <summary>True once the loop has ended (disabled, or stopped).</summary>
    public bool Stopped { get; private set; }

    /// <summary>Starts the loop; returns when the first attempt connected (said hello) or failed.</summary>
    public async Task StartAsync(CancellationToken ct)
    {
        var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _loop = Task.Run(() => RunAsync(first));
        using var registration = ct.Register(() => first.TrySetCanceled(ct));
        await first.Task.ConfigureAwait(false);
    }

    private async Task RunAsync(TaskCompletionSource first)
    {
        var ct = _stop.Token;
        while (!ct.IsCancellationRequested)
        {
            RemoteHub.Link? link = null;
            try
            {
                var uri = RemoteProtocol.BuildUri(definition.Host, definition.Port);
                if (string.IsNullOrWhiteSpace(definition.Token)) throw new RemoteRefusedException("No token: paste the other instance's API token");
                using var socket = new ClientWebSocket();
                _socket = socket;
                socket.Options.SetRequestHeader("Authorization", "Bearer " + definition.Token.Trim());
                socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                socket.Options.CollectHttpResponseDetails = true;
                Set(ConnectionState.Connecting, $"Connecting to {uri}...");
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(ConnectTimeout);
                    await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
                }
                catch (WebSocketException) when (socket.HttpStatusCode != 0 && socket.HttpStatusCode != HttpStatusCode.SwitchingProtocols)
                {
                    throw new RemoteRefusedException(Refusal(socket.HttpStatusCode));
                }

                link = await hub.OpenOutgoingAsync(definition, uri.ToString(), (text, token) => SendAsync(socket, text, token),
                    () => CloseAsync(socket)).ConfigureAwait(false);
                Set(ConnectionState.Connecting, "Connected, waiting for the other instance's hello...");
                await ReceiveAsync(socket, link, first, ct).ConfigureAwait(false);
                if (!ct.IsCancellationRequested)
                {
                    if (link.Error is { Length: > 0 } error) Set(ConnectionState.Error, error);
                    else Set(ConnectionState.Error, link.Peer is null ? "Closed before the other instance said hello" : "Connection closed");
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Disconnect requested.
            }
            catch (RemoteRefusedException ex)
            {
                Set(ConnectionState.Error, ex.Message);
            }
            catch (Exception ex) when (ex is WebSocketException or IOException or UriFormatException or ArgumentException
                                           or OperationCanceledException or InvalidOperationException or HttpRequestException)
            {
                Set(ConnectionState.Error, ex is OperationCanceledException ? "Timed out connecting" : "Could not connect: " + ex.Message);
            }
            finally
            {
                _socket = null;
                if (link is not null) hub.CloseLink(link);
            }

            first.TrySetResult();
            if (ct.IsCancellationRequested || !definition.Enabled) break;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(definition.ReconnectSeconds, 1, 3600)), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
        Stopped = true;
        Set(ConnectionState.Disconnected, "Not connected");
        first.TrySetResult();
    }

    internal static string Refusal(HttpStatusCode status) => (int)status switch
    {
        401 => "Refused: wrong token (copy the API token from the other instance's Servers page)",
        403 => "Refused by the other instance (origin not allowed)",
        404 => "The other instance's REST API is off, or it is not a Cuebot with remote signals",
        _ => $"Refused by the other instance (HTTP {(int)status})",
    };

    private async Task ReceiveAsync(ClientWebSocket socket, RemoteHub.Link link, TaskCompletionSource first, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close) break;
            message.Write(buffer, 0, result.Count);
            if (message.Length > Cuebot.Server.Hosting.WebSocketConnection.MaxMessageBytes) throw new InvalidOperationException("Message too big");
            if (!result.EndOfMessage) continue;
            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);
            var wasReady = link.Peer is not null;
            await hub.HandleAsync(link, text).ConfigureAwait(false);
            if (!wasReady && link.Peer is not null)
            {
                Set(ConnectionState.Connected, "Connected");
                first.TrySetResult();
            }
        }
    }

    private async Task<bool> SendAsync(ClientWebSocket socket, string text, CancellationToken ct)
    {
        if (socket.State != WebSocketState.Open) return false;
        try
        {
            await _sendLock.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        try
        {
            await socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is WebSocketException or ObjectDisposedException or IOException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            try { _sendLock.Release(); }
            catch (ObjectDisposedException) { }
        }
    }

    private static async Task CloseAsync(ClientWebSocket socket)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            if (socket.State == WebSocketState.Open)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or IOException)
        {
            socket.Abort();
        }
    }

    private void Set(ConnectionState state, string message)
    {
        if (State == state && Message == message) return;
        State = state;
        Message = message;
        if (state == ConnectionState.Error && message != _lastLogged)
        {
            _lastLogged = message;
            log.Warn(RemoteHub.Source, $"'{definition.Name}': {message}");
        }
        else if (state == ConnectionState.Connected)
        {
            _lastLogged = "";
        }
        hub.RaiseStatusChanged();
    }

    public async ValueTask DisposeAsync()
    {
        // A clean close first, so the other instance sees the disconnect at once.
        if (_socket is { } socket) await CloseAsync(socket).ConfigureAwait(false);
        await _stop.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _stop.Dispose();
        _sendLock.Dispose();
    }

    private sealed class RemoteRefusedException(string message) : Exception(message);
}
