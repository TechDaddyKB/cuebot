using System.Text.Json;
using System.Threading.Channels;
using Cuebot.Core.Logging;

namespace Cuebot.Twitch.EventSub;

/// <summary>
/// One EventSub session from a fresh connection until it is lost. Handles the welcome (subscribe callback),
/// keepalive watchdog, deduplicated notifications and <c>session_reconnect</c> migrations, during which the
/// old connection keeps delivering until the new one is welcomed.
/// </summary>
internal sealed class EventSubSession(
    IEventSubListener listener,
    MessageDeduplicator deduplicator,
    EventSubTimeouts timeouts,
    AppLog log) : IAsyncDisposable
{
    private readonly Channel<EventSubFrame> _frames = Channel.CreateUnbounded<EventSubFrame>();
    private EventSubConnection? _current;
    private EventSubConnection? _pending;
    private TimeSpan _idleTimeout = timeouts.Welcome;
    private string? _sessionId;

    /// <summary>Raised when the first welcome of this session arrives.</summary>
    public event Action? Started;

    /// <summary>Runs until the session is lost and returns a human-readable reason.</summary>
    public async Task<string> RunAsync(Uri url, CancellationToken ct)
    {
        _current = await EventSubConnection.OpenAsync(url, _frames.Writer, ct).ConfigureAwait(false);
        while (true)
        {
            var frame = await ReadFrameAsync(ct).ConfigureAwait(false);
            if (frame is null) return $"no message from Twitch for {_idleTimeout.TotalSeconds:0.#}s";
            if (frame.Source != _current && frame.Source != _pending) continue;

            if (frame.Text is not null)
                await HandleMessageAsync(frame.Source, frame.Text, ct).ConfigureAwait(false);
            else if (frame.Source == _pending)
                await AbandonPendingAsync($"EventSub reconnect connection failed: {frame.CloseReason}").ConfigureAwait(false);
            else if (_pending is not null)
                await PromotePendingAsync().ConfigureAwait(false); // old socket closed first; keep waiting for the new welcome
            else
                return frame.CloseReason ?? "connection closed";
        }
    }

    /// <summary>Returns null when nothing arrived within the idle timeout (the keepalive watchdog).</summary>
    private async Task<EventSubFrame?> ReadFrameAsync(CancellationToken ct)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idle.CancelAfter(_idleTimeout);
        try
        {
            return await _frames.Reader.ReadAsync(idle.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    private async Task HandleMessageAsync(EventSubConnection source, string text, CancellationToken ct)
    {
        EventSubMessage message;
        try
        {
            message = EventSubMessage.Parse(text);
        }
        catch (JsonException ex)
        {
            log.Warn("Twitch", $"Ignoring malformed EventSub message: {ex.Message}");
            return;
        }

        switch (message.MessageType)
        {
            case EventSubMessageTypes.Welcome:
                await HandleWelcomeAsync(source, message, ct).ConfigureAwait(false);
                break;
            case EventSubMessageTypes.Keepalive:
                break;
            case EventSubMessageTypes.Notification:
                if (deduplicator.TryAdd(message.MessageId))
                    listener.OnNotification(new EventSubNotification(message.MessageId, message.SubscriptionType, message.Event));
                break;
            case EventSubMessageTypes.Reconnect:
                await BeginReconnectAsync(message.ReconnectUrl, ct).ConfigureAwait(false);
                break;
            case EventSubMessageTypes.Revocation:
                listener.OnRevocation(message.SubscriptionType, message.SubscriptionStatus);
                break;
            default:
                log.Debug("Twitch", $"Ignoring EventSub message type '{message.MessageType}'");
                break;
        }
    }

    private async Task HandleWelcomeAsync(EventSubConnection source, EventSubMessage welcome, CancellationToken ct)
    {
        if (welcome.KeepaliveTimeoutSeconds is { } keepalive)
            _idleTimeout = TimeSpan.FromSeconds(keepalive) + timeouts.KeepaliveSlack;

        if (_sessionId is null)
        {
            _sessionId = welcome.SessionId;
            Started?.Invoke();
            _ = NotifySessionStartedAsync(welcome.SessionId, ct);
        }
        else if (source == _pending)
        {
            // Subscriptions carry over to the new connection; only the socket changes.
            _sessionId = welcome.SessionId;
            await PromotePendingAsync().ConfigureAwait(false);
            log.Info("Twitch", "EventSub moved to a new connection");
        }
    }

    /// <summary>Runs the subscribe callback without blocking the read loop (keepalives must keep flowing).</summary>
    private async Task NotifySessionStartedAsync(string sessionId, CancellationToken ct)
    {
        try
        {
            await listener.OnSessionStartedAsync(sessionId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            log.Error("Twitch", "Setting up EventSub subscriptions failed", ex);
        }
    }

    private async Task BeginReconnectAsync(string reconnectUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(reconnectUrl, UriKind.Absolute, out var url))
        {
            log.Warn("Twitch", "EventSub asked to reconnect without a valid URL");
            return;
        }
        if (_pending is not null) await AbandonPendingAsync("EventSub reconnect superseded by a newer request").ConfigureAwait(false);
        try
        {
            _pending = await EventSubConnection.OpenAsync(url, _frames.Writer, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Warn("Twitch", $"Could not open the EventSub reconnect connection: {ex.Message}");
        }
    }

    private async Task PromotePendingAsync()
    {
        var old = _current;
        _current = _pending;
        _pending = null;
        if (old is not null) await old.DisposeAsync().ConfigureAwait(false);
    }

    private async Task AbandonPendingAsync(string reason)
    {
        log.Warn("Twitch", reason);
        var pending = _pending;
        _pending = null;
        if (pending is not null) await pending.DisposeAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (_pending is not null) await _pending.DisposeAsync().ConfigureAwait(false);
        if (_current is not null) await _current.DisposeAsync().ConfigureAwait(false);
        _pending = _current = null;
    }
}
