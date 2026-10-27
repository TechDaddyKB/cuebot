using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Twitch.Helix;

namespace Cuebot.Twitch.EventSub;

/// <summary>
/// Bridges an EventSub session to the rest of Cuebot: creates the subscriptions the granted scopes allow,
/// publishes notifications as <see cref="BotEvent"/>s and reports connection progress.
/// </summary>
internal sealed class TwitchEventSubListener(
    HelixClient helix,
    TwitchSettings settings,
    EventBus bus,
    AppLog log,
    Action<ConnectionState, string> reportStatus) : IEventSubListener
{
    public async Task OnSessionStartedAsync(string sessionId, CancellationToken ct)
    {
        var granted = settings.Scopes.ToHashSet(StringComparer.Ordinal);
        var unavailable = 0;
        foreach (var spec in EventSubSubscriptions.All)
        {
            if (spec.RequiredScope is { } scope && !granted.Contains(scope))
            {
                log.Warn("Twitch", $"Skipping {spec.Type}: scope '{scope}' was not granted. Sign in again to enable it.");
                unavailable++;
                continue;
            }
            try
            {
                await helix.CreateEventSubSubscriptionAsync(spec, settings.UserId, sessionId, ct).ConfigureAwait(false);
            }
            catch (TwitchApiException ex)
            {
                log.Warn("Twitch", ex.Message);
                unavailable++;
            }
        }

        if (ct.IsCancellationRequested) return;
        var suffix = unavailable > 0 ? $" ({unavailable} event types unavailable, see log)" : "";
        log.Info("Twitch", $"Connected to EventSub as {settings.Login}");
        reportStatus(ConnectionState.Connected, $"Connected as {settings.Login}{suffix}");
    }

    public void OnNotification(EventSubNotification notification)
    {
        if (EventSubMapper.Map(notification.SubscriptionType, notification.Event) is { } botEvent)
            bus.Publish(botEvent);
        else
            log.Debug("Twitch", $"Ignoring notification of type {notification.SubscriptionType}");
    }

    public void OnRevocation(string subscriptionType, string status) =>
        log.Warn("Twitch", $"Twitch revoked the {subscriptionType} subscription ({status})");

    public void OnConnectionLost(string reason, TimeSpan retryIn) =>
        reportStatus(ConnectionState.Connecting, $"Connection lost ({reason}); retrying in {retryIn.TotalSeconds:0}s");
}
