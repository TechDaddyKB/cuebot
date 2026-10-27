using Cuebot.Core.Logging;
using Cuebot.Kick.Events;
using Cuebot.Kick.Pusher;

namespace Cuebot.Kick.Realtime;

/// <summary>
/// UNOFFICIAL direct real-time source: kick.com's own real-time server (Centrifugo), the same one its website chat
/// uses. Needs no public URL and no sign-in. Subscribes to the chatroom and channel channels and decodes their events.
/// </summary>
internal sealed class KickRealtimeEventSource : IKickEventSource, IAsyncDisposable
{
    private readonly Func<IPusherTransport> _transports;
    private readonly KickRealtimeAuth _auth;
    private readonly KickTimings _timings;
    private readonly AppLog _log;
    private CancellationTokenSource? _stop;
    private Task _run = Task.CompletedTask;
    private long _broadcasterUserId;
    private volatile bool _connected;

    public KickRealtimeEventSource(Func<IPusherTransport> transports, KickRealtimeAuth auth, KickTimings timings, AppLog log)
    {
        _transports = transports;
        _auth = auth;
        _timings = timings;
        _log = log;
    }

    public string Name => "Realtime chat";
    public bool IsConnected => _connected;
    public event Action<KickEvent>? EventReceived;
    public event Action<bool, string>? ConnectionChanged;

    /// <summary>Starts (or restarts) listening for the given channel and chatroom. Returns immediately.</summary>
    public async Task StartAsync(long channelId, long chatroomId, long broadcasterUserId)
    {
        await StopAsync().ConfigureAwait(false);
        var channels = KickPusherDecoder.ChannelsFor(channelId, chatroomId);
        if (channels.Count == 0) throw new KickApiException("No Kick chatroom id is known, so real-time chat cannot start.");
        _broadcasterUserId = broadcasterUserId;
        var client = new CentrifugoClient(_transports,
            (clientId, ct) => _auth.GetCredentialsAsync(clientId, channelId, ct),
            _auth.GetTokenAsync, _timings, _log);
        client.Connected += _ => SetConnected(true, "connected");
        client.Disconnected += reason => SetConnected(false, reason);
        client.SubscriptionSucceeded += channel => _log.Debug("Kick", $"Subscribed to {channel}");
        client.Message += OnMessage;
        var stop = new CancellationTokenSource();
        var token = stop.Token; // captured now: StopAsync may clear the field before the task starts
        _stop = stop;
        _run = Task.Run(() => client.RunAsync(channels, token));
    }

    public async Task StopAsync()
    {
        var stop = _stop;
        if (stop is null) return;
        _stop = null;
        await stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _run.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is OperationCanceledException)
        {
        }
        stop.Dispose();
        SetConnected(false, "stopped");
    }

    private void OnMessage(PusherFrame frame)
    {
        foreach (var e in KickPusherDecoder.Decode(frame, _broadcasterUserId))
        {
            try
            {
                EventReceived?.Invoke(e);
            }
            catch (Exception ex)
            {
                _log.Error("Kick", $"Handling {e.Type} failed", ex);
            }
        }
    }

    private void SetConnected(bool connected, string reason)
    {
        if (_connected == connected) return;
        _connected = connected;
        ConnectionChanged?.Invoke(connected, reason);
    }

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(false);
}
