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
    bool IsBot = false);

/// <summary>
/// Bridges an EventSub session to the rest of Cuebot: creates the subscriptions the granted scopes allow (skipping
/// the others with a warning), publishes notifications through the <see cref="TwitchEventRouter"/> and reports
/// connection progress.
/// </summary>
internal sealed class TwitchEventSubListener(
    HelixClient helix,
    EventSubAccount account,
    TwitchEventRouter router,
    EventBus bus,
    AppLog log,
    Action<ConnectionState, string> reportStatus,
    Func<CancellationToken, Task>? onSubscribed = null) : IEventSubListener
{
    /// <summary>Event types skipped at the last session start because a scope was missing or Twitch refused them.</summary>
    public IReadOnlyList<string> Unavailable { get; private set; } = [];

    public async Task OnSessionStartedAsync(string sessionId, CancellationToken ct)
    {
        var granted = account.GrantedScopes().ToHashSet(StringComparer.Ordinal);
        var unavailable = new List<string>();
        var missingScopes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var spec in account.Subscriptions)
        {
            if (!spec.IsAuthorized(granted))
            {
                unavailable.Add(spec.Type);
                missingScopes.Add(spec.Scopes[^1]);
                continue;
            }
            try
            {
                await helix.CreateEventSubSubscriptionAsync(spec, account.UserId(), sessionId, ct).ConfigureAwait(false);
            }
            catch (TwitchApiException ex)
            {
                log.Warn("Twitch", ex.Message);
                unavailable.Add(spec.Type);
            }
        }
        if (missingScopes.Count > 0)
            log.Warn("Twitch", $"{account.Label}: skipped {unavailable.Count} event types because these scopes were not granted: "
                               + $"{string.Join(", ", missingScopes)}. Enable them on the Twitch Settings tab and sign in again.");
        Unavailable = unavailable;

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

        var suffix = unavailable.Count > 0 ? $" ({unavailable.Count} event types unavailable, see log)" : "";
        log.Info("Twitch", $"{account.Label} connected to EventSub as {account.Login()}");
        reportStatus(ConnectionState.Connected, $"Connected as {account.Login()}{suffix}");
    }

    public void OnNotification(EventSubNotification notification)
    {
        var events = router.Route(notification.SubscriptionType, notification.Event, account.IsBot);
        if (events.Count == 0) log.Debug("Twitch", $"Ignoring notification of type {notification.SubscriptionType}");
        foreach (var botEvent in events) bus.Publish(botEvent);
    }

    public void OnRevocation(string subscriptionType, string status) =>
        log.Warn("Twitch", $"Twitch revoked the {subscriptionType} subscription ({status})");

    public void OnConnectionLost(string reason, TimeSpan retryIn) =>
        reportStatus(ConnectionState.Connecting, $"Connection lost ({reason}); retrying in {retryIn.TotalSeconds:0}s");
}
