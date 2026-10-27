using Cuebot.Core.Events;
using Cuebot.Core.Webhooks;

namespace Cuebot.Kick.Webhooks;

/// <summary>
/// Feeds Kick webhooks forwarded by the relay (route <c>kick</c>, i.e. <c>https://&lt;relay&gt;/hook/&lt;secret&gt;/kick</c>)
/// into <see cref="KickWebhookEventSource"/>, which verifies Kick's signature and raises the events. Also mirrors the
/// relay connection state ("relay.connected" / "relay.disconnected" bot events) onto that event source.
/// </summary>
public sealed class KickWebhookHandler : IWebhookHandler, IDisposable
{
    /// <summary>The relay client's connection events (Cuebot.Relay.RelayEvents; repeated here to avoid a project reference).</summary>
    internal const string RelayConnectedEvent = "relay.connected";
    internal const string RelayDisconnectedEvent = "relay.disconnected";

    private readonly KickWebhookEventSource _source;
    private readonly IDisposable? _subscription;

    public KickWebhookHandler(KickWebhookEventSource source, EventBus? bus = null)
    {
        _source = source;
        _subscription = bus?.Subscribe(e =>
        {
            if (e.Type == RelayConnectedEvent) _source.SetConnected(true, "Relay connected");
            else if (e.Type == RelayDisconnectedEvent) _source.SetConnected(false, e.Args.TryGetValue("reason", out var r) ? r?.ToString() ?? "" : "");
        });
    }

    public string Service => "kick";
    public string DisplayName => "Kick";

    public async Task<WebhookOutcome> HandleAsync(WebhookDelivery delivery, CancellationToken ct)
    {
        var kick = new KickWebhookDelivery(
            delivery.Header("Kick-Event-Message-Id"),
            delivery.Header("Kick-Event-Message-Timestamp"),
            delivery.Header("Kick-Event-Signature"),
            delivery.Header("Kick-Event-Type"),
            delivery.Header("Kick-Event-Version"),
            delivery.BodyText);
        if (kick.MessageId.Length == 0 || kick.Signature.Length == 0) return WebhookOutcome.Rejected;
        return await _source.HandleAsync(kick, ct).ConfigureAwait(false) switch
        {
            KickWebhookOutcome.Accepted => WebhookOutcome.Handled,
            KickWebhookOutcome.Duplicate or KickWebhookOutcome.Ignored => WebhookOutcome.Ignored,
            KickWebhookOutcome.BadSignature => WebhookOutcome.Rejected,
            _ => WebhookOutcome.Failed,
        };
    }

    public void Dispose() => _subscription?.Dispose();
}
