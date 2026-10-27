using System.Text;

namespace Cuebot.Core.Webhooks;

/// <summary>
/// One HTTP webhook as a service sent it, exactly as received (so signatures can be checked against the raw bytes).
/// It arrives through the user's Cloudflare relay (see relay/README.md), or any other transport that produces one.
/// </summary>
/// <param name="Id">Unique per delivery (assigned by the relay); used to acknowledge it and to drop replays.</param>
/// <param name="Service">The route the webhook was sent to, e.g. <c>kofi</c>, <c>patreon</c>, <c>kick</c>, <c>custom</c>.</param>
/// <param name="Path">Anything after the service in the URL (without a leading slash), e.g. the custom hook name; may be empty.</param>
/// <param name="Headers">Request headers with lower-case names.</param>
public sealed record WebhookDelivery(
    string Id,
    string Service,
    string Path,
    string Method,
    string Query,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body,
    DateTimeOffset ReceivedAt)
{
    public string BodyText => Encoding.UTF8.GetString(Body);

    /// <summary>A header value, or "" when absent. Names are matched case-insensitively.</summary>
    public string Header(string name) =>
        Headers.TryGetValue(name.ToLowerInvariant(), out var value) ? value : "";
}

/// <summary>What became of a delivery; reported back to the relay log and the Webhooks page.</summary>
public enum WebhookOutcome
{
    /// <summary>Verified and turned into events.</summary>
    Handled,

    /// <summary>Verified but nothing to do (test ping, event type Cuebot ignores, duplicate).</summary>
    Ignored,

    /// <summary>The signature or token did not match: probably not from the service. Never turned into events.</summary>
    Rejected,

    /// <summary>The service is not set up in Cuebot (no secret entered), so the delivery could not be verified.</summary>
    NotConfigured,

    /// <summary>Malformed body or a failure while handling it.</summary>
    Failed,
}

/// <summary>
/// Turns deliveries for one <see cref="Service"/> route into Cuebot events. Register implementations in DI
/// (<c>services.AddSingleton&lt;IWebhookHandler, ...&gt;()</c>); the relay client dispatches each delivery to the handler
/// whose <see cref="Service"/> matches. Handlers must verify the delivery (signature, verification token) themselves
/// with secrets kept in Cuebot's settings: the relay forwards without checking, so secrets never leave the PC.
/// </summary>
public interface IWebhookHandler
{
    /// <summary>Route name (lower case, letters/digits/dashes), as in <c>https://&lt;relay&gt;/hook/&lt;secret&gt;/&lt;service&gt;</c>.</summary>
    string Service { get; }

    /// <summary>Shown on the Webhooks page next to the URL to paste into the service.</summary>
    string DisplayName { get; }

    Task<WebhookOutcome> HandleAsync(WebhookDelivery delivery, CancellationToken ct);
}
