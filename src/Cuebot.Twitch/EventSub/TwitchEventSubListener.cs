using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Twitch.Helix;

namespace Cuebot.Twitch.EventSub;

/// <summary>What one EventSub session subscribes to and for which account.</summary>
internal sealed record EventSubAccount(
    string Label,
    Func<string> UserId,
    Func<string> Login,
    Func<IReadOnlyCollection<string>> GrantedScopes,
    IReadOnlyList<EventSubSubscriptionSpec> Subscriptions,
    bool IsBot = false,
    Func<CancellationToken, Task<IReadOnlyList<EventSubSubscriptionSpec>>>? MoreSubscriptions = null);

/// <summary>
/// Bridges an EventSub session to the rest of Cuebot: creates the subscriptions the granted scopes allow (skipping
/// the others with a warning), publishes notifications through the <see cref="TwitchEventRouter"/> and reports
/// connection progress.
/// <para>
/// After a lost connection Twitch still counts the dead session's subscriptions and connection for a while (up to 3
/// connections per account and app, one subscription per type and condition), so a quick resubscribe can be refused
/// with 429 for every event type. Refused ones are retried in the background after deleting this app's subscriptions
/// from earlier sessions (Twitch's documented way: find them by session id), and the status says "Connecting" until
/// every event type is back, instead of "Connected" with alerts silently missing.
/// </para>
/// </summary>
internal sealed class TwitchEventSubListener(
    HelixClient helix,
    EventSubAccount account,
    TwitchEventRouter router,
    EventBus bus,
    AppLog log,
    Action<ConnectionState, string> reportStatus,
    Func<CancellationToken, Task>? onSubscribed = null,
    Func<string, Task>? lookUpBoxArt = null,
    IReadOnlyList<TimeSpan>? retryDelays = null) : IEventSubListener
{
    /// <summary>Waits between retries of refused subscriptions: about ten minutes in all, then the status turns to an error.</summary>
    private static readonly TimeSpan[] DefaultRetryDelays =
        [.. new[] { 2, 5, 15, 30, 60, 60, 60, 60, 60, 60, 60, 60, 60 }.Select(s => TimeSpan.FromSeconds(s))];

    private readonly object _gate = new();
    private readonly IReadOnlyList<TimeSpan> _retryDelays = retryDelays ?? DefaultRetryDelays;
    private readonly HashSet<string> _earlierSessions = new(StringComparer.Ordinal);
    private Task _channelUpdates = Task.CompletedTask;
    private string? _session;
    private CancellationTokenSource? _retries;

    /// <summary>Event types skipped at the last session start because a scope was missing or Twitch refused them.</summary>
    public IReadOnlyList<string> Unavailable { get; private set; } = [];

    /// <summary>The background retries of the current session, for tests; completed when there is nothing to retry.</summary>
    internal Task Retrying { get; private set; } = Task.CompletedTask;

    public async Task OnSessionStartedAsync(string sessionId, CancellationToken ct)
    {
        StopRetrying();
        if (_session is { } previous) _earlierSessions.Add(previous);
        _session = sessionId;

        var granted = account.GrantedScopes().ToHashSet(StringComparer.Ordinal);
        var unavailable = new List<string>();
        var missingScopes = new SortedSet<string>(StringComparer.Ordinal);
        var refused = new List<EventSubSubscriptionSpec>();
        _revocationsLogged.Clear();
        foreach (var spec in account.Subscriptions.Concat(await MoreSubscriptionsAsync(ct).ConfigureAwait(false)))
        {
            if (spec.MissingScope(granted) is { } missing)
            {
                unavailable.Add(spec.Name);
                missingScopes.Add(missing);
                continue;
            }
            switch (await TrySubscribeAsync(spec, sessionId, ct).ConfigureAwait(false))
            {
                case SubscribeResult.Refused: refused.Add(spec); break;
                case SubscribeResult.Failed: unavailable.Add(spec.Name); break;
            }
        }
        if (missingScopes.Count > 0)
            log.Warn("Twitch", $"{account.Label}: skipped {unavailable.Count} event types because these scopes were not granted: "
                               + $"{string.Join(", ", missingScopes)}. Enable them on the Twitch Settings tab and sign in again.");
        Unavailable = [.. unavailable, .. refused.Select(s => s.Name)];

        if (ct.IsCancellationRequested) return;
        if (onSubscribed is not null)
        {
            try
            {
                await onSubscribed(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                log.Warn("Twitch", $"{account.Label}: {ex.Message}");
            }
        }

        if (refused.Count > 0)
        {
            log.Warn("Twitch", $"{account.Label}: Twitch refused {refused.Count} event types for now (too many connections or leftover "
                               + "subscriptions from the lost connection); cleaning up and retrying");
            reportStatus(ConnectionState.Connecting, Waiting(refused.Count));
            var retries = _retries = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Retrying = RetryAsync(sessionId, refused, unavailable, retries.Token);
            return;
        }
        ReportConnected(unavailable);
    }

    /// <summary>Extra subscriptions worked out per session (the watched channels); a failure only costs those.</summary>
    private async Task<IReadOnlyList<EventSubSubscriptionSpec>> MoreSubscriptionsAsync(CancellationToken ct)
    {
        if (account.MoreSubscriptions is null) return [];
        try
        {
            return await account.MoreSubscriptions(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log.Warn("Twitch", $"{account.Label}: {ex.Message}");
            return [];
        }
    }

    private enum SubscribeResult { Done, Refused, Failed }

    /// <summary>One subscription; 429 (Twitch's limits, usually leftovers of a lost connection) is worth retrying.</summary>
    private async Task<SubscribeResult> TrySubscribeAsync(EventSubSubscriptionSpec spec, string sessionId, CancellationToken ct)
    {
        try
        {
            await helix.CreateEventSubSubscriptionAsync(spec, account.UserId(), sessionId, ct).ConfigureAwait(false);
            return SubscribeResult.Done;
        }
        catch (TwitchApiException ex) when (ex.StatusCode == 429)
        {
            log.Debug("Twitch", ex.Message);
            if (ex.Message.Contains("transports limit", StringComparison.OrdinalIgnoreCase)) WarnTransportLimitOnce();
            return SubscribeResult.Refused;
        }
        catch (TwitchApiException ex) when (ex.StatusCode == 403)
        {
            // Subscriptions whose scopes are missing are never sent, so a 403 here is Twitch refusing a granted scope:
            // signing in again (what the generic Helix 403 hint says) cannot help.
            log.Warn("Twitch", $"Twitch refused the {spec.Name} event for {account.Login()} (403) although Cuebot has the "
                + "permission it needs; it stays unavailable. Signing in again will not change this.");
            return SubscribeResult.Failed;
        }
        catch (TwitchApiException ex)
        {
            log.Warn("Twitch", ex.Message);
            return SubscribeResult.Failed;
        }
    }

    private async Task RetryAsync(string sessionId, List<EventSubSubscriptionSpec> refused, List<string> unavailable, CancellationToken ct)
    {
        try
        {
            await CleanUpEarlierSessionsAsync(sessionId, ct).ConfigureAwait(false);
            foreach (var delay in _retryDelays)
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
                var still = new List<EventSubSubscriptionSpec>();
                foreach (var spec in refused)
                {
                    switch (await TrySubscribeAsync(spec, sessionId, ct).ConfigureAwait(false))
                    {
                        case SubscribeResult.Refused: still.Add(spec); break;
                        case SubscribeResult.Failed: unavailable.Add(spec.Name); break;
                    }
                }
                refused = still;
                Unavailable = [.. unavailable, .. refused.Select(s => s.Name)];
                if (refused.Count == 0)
                {
                    log.Info("Twitch", $"{account.Label}: all event types are subscribed again");
                    ReportConnected(unavailable);
                    return;
                }
                reportStatus(ConnectionState.Connecting, Waiting(refused.Count));
            }
            log.Error("Twitch", $"{account.Label}: Twitch still refuses {refused.Count} event types ({string.Join(", ", refused.Select(s => s.Name))}). "
                                + "Alerts for them will not fire. Close other copies of Cuebot signed in as this account, then reconnect.");
            reportStatus(ConnectionState.Error, $"Connected as {account.Login()}, but {refused.Count} event types are not working (see log)");
        }
        catch (OperationCanceledException)
        {
            // a new session or a disconnect took over
        }
    }

    /// <summary>
    /// Deletes this app's WebSocket subscriptions from earlier (lost) sessions of this listener and any that Twitch
    /// already disabled. Those of other connections that are still enabled (another Cuebot) are left alone.
    /// </summary>
    private async Task CleanUpEarlierSessionsAsync(string currentSession, CancellationToken ct)
    {
        try
        {
            var stale = (await helix.ListEventSubSubscriptionsAsync(ct).ConfigureAwait(false))
                .Where(s => s.Method == "websocket" && s.SessionId != currentSession
                            && (_earlierSessions.Contains(s.SessionId) || s.Status != "enabled"))
                .ToList();
            foreach (var subscription in stale)
                await helix.DeleteEventSubSubscriptionAsync(subscription.Id, ct).ConfigureAwait(false);
            if (stale.Count > 0) log.Info("Twitch", $"{account.Label}: removed {stale.Count} subscriptions left over from the lost connection");
        }
        catch (TwitchApiException ex)
        {
            log.Debug("Twitch", $"Could not clean up old EventSub subscriptions: {ex.Message}");
        }
    }

    private bool _warnedTransports;

    private void WarnTransportLimitOnce()
    {
        if (_warnedTransports) return;
        _warnedTransports = true;
        log.Warn("Twitch", $"{account.Label}: Twitch allows 3 event connections per account and app. Another copy of Cuebot (on another "
                           + $"computer, or a second one here) signed in as {account.Login()} may be using them.");
    }

    private string Waiting(int count) => $"Connected as {account.Login()}; {count} event types still waiting for Twitch (retrying)";

    private void ReportConnected(List<string> unavailable)
    {
        var suffix = unavailable.Count > 0 ? $" ({unavailable.Count} event types unavailable, see log)" : "";
        log.Info("Twitch", $"{account.Label} connected to EventSub as {account.Login()}");
        reportStatus(ConnectionState.Connected, $"Connected as {account.Login()}{suffix}");
    }

    private void StopRetrying()
    {
        _retries?.Cancel();
        _retries?.Dispose();
        _retries = null;
    }

    public void OnNotification(EventSubNotification notification)
    {
        // channel.update has no box art: a new category is looked up first (Stream Update's gameBoxArt). Updates
        // wait for each other so the router still compares them in order.
        if (lookUpBoxArt is not null && notification.SubscriptionType == "channel.update"
            && notification.Event.Str("broadcaster_user_id") == account.UserId())
        {
            var categoryId = notification.Event.Str("category_id");
            lock (_gate)
            {
                if (!_channelUpdates.IsCompleted || !router.HasBoxArt(categoryId))
                {
                    _channelUpdates = _channelUpdates.ContinueWith(async _ =>
                    {
                        if (!router.HasBoxArt(categoryId)) await LookUpAsync(categoryId).ConfigureAwait(false);
                        Publish(notification);
                    }, TaskScheduler.Default).Unwrap();
                    return;
                }
            }
        }
        Publish(notification);
    }

    private async Task LookUpAsync(string categoryId)
    {
        try
        {
            await lookUpBoxArt!(categoryId).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            log.Debug("Twitch", $"Could not look up the box art of category {categoryId}: {ex.Message}");
        }
    }

    private void Publish(EventSubNotification notification)
    {
        var events = router.Route(notification.SubscriptionType, notification.Event, account.IsBot);
        if (events.Count == 0) log.Debug("Twitch", $"Ignoring notification of type {notification.SubscriptionType}");
        foreach (var botEvent in events) bus.Publish(botEvent);
    }

    private readonly HashSet<string> _revocationsLogged = new(StringComparer.Ordinal);

    /// <summary>
    /// Twitch stopped a subscription and sends nothing more for it: the type joins the unavailable list and the
    /// status says why. A withdrawn authorization means signing in again; nothing is resubscribed, because Twitch
    /// would refuse it for the same reason (a lost connection resubscribes everything anyway).
    /// </summary>
    public void OnRevocation(string subscriptionType, string status)
    {
        List<string> unavailable;
        bool firstOfItsKind;
        lock (_gate)
        {
            unavailable = [.. Unavailable];
            if (!unavailable.Contains(subscriptionType)) unavailable.Add(subscriptionType);
            Unavailable = unavailable;
            firstOfItsKind = _revocationsLogged.Add(status);
        }
        if (status is "authorization_revoked" or "user_removed")
            reportStatus(ConnectionState.Error, $"Twitch withdrew Cuebot's access for {account.Login()}; sign in again");
        else
            reportStatus(ConnectionState.Connected, $"Connected as {account.Login()} ({unavailable.Count} event types unavailable, see log)");
        if (!firstOfItsKind) return;   // Twitch revokes every subscription at once: explain it once
        var why = status switch
        {
            "authorization_revoked" => "Cuebot's access was withdrawn (disconnected on Twitch, or the password changed); sign in again",
            "user_removed" => "the account is no longer available on Twitch",
            "version_removed" => "Twitch retired the version Cuebot uses; update Cuebot",
            "moderator_removed" => "the account is no longer a moderator of the channel",
            "chat_user_banned" => "the account is banned from the channel's chat",
            "beta_maintenance" => "Twitch paused this beta feature; reconnect later",
            _ => $"Twitch gave the reason \"{status}\"",
        };
        log.Warn("Twitch", $"{account.Label}: Twitch stopped sending {subscriptionType} events: {why}.");
    }

    public void OnConnectionLost(string reason, TimeSpan retryIn)
    {
        StopRetrying();
        reportStatus(ConnectionState.Connecting, $"Connection lost ({reason}); retrying in {retryIn.TotalSeconds:0}s");
    }
}
