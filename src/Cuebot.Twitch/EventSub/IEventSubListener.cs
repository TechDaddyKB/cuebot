namespace Cuebot.Twitch.EventSub;

/// <summary>Receives what happens on an EventSub WebSocket session.</summary>
internal interface IEventSubListener
{
    /// <summary>A fresh session was welcomed; subscriptions must be created for it within 10 seconds.</summary>
    Task OnSessionStartedAsync(string sessionId, CancellationToken ct);

    void OnNotification(EventSubNotification notification);

    void OnRevocation(string subscriptionType, string status);

    /// <summary>The session ended unexpectedly; a fresh connection is attempted after <paramref name="retryIn"/>.</summary>
    void OnConnectionLost(string reason, TimeSpan retryIn);
}
