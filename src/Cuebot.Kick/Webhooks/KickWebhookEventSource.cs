using System.Text.Json;
using Cuebot.Kick.Events;

namespace Cuebot.Kick.Webhooks;

/// <summary>One webhook request as Kick sent it: the <c>Kick-Event-*</c> headers and the raw body.</summary>
public sealed record KickWebhookDelivery(string MessageId, string Timestamp, string Signature, string EventType, string EventVersion, string Body)
{
    /// <summary>Reads the delivery from HTTP headers (case-insensitive names).</summary>
    public static KickWebhookDelivery FromHeaders(IEnumerable<KeyValuePair<string, string>> headers, string body)
    {
        var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in headers) h[key] = value;
        return new KickWebhookDelivery(h.GetValueOrDefault("Kick-Event-Message-Id", ""), h.GetValueOrDefault("Kick-Event-Message-Timestamp", ""),
            h.GetValueOrDefault("Kick-Event-Signature", ""), h.GetValueOrDefault("Kick-Event-Type", ""),
            h.GetValueOrDefault("Kick-Event-Version", ""), body);
    }
}

public enum KickWebhookOutcome { Accepted, Duplicate, Ignored, BadSignature, Malformed }

/// <summary>
/// The webhook event source. It has no listener of its own: the user-hosted relay (relay/, see PROTOCOL.md)
/// forwards each delivery to <see cref="HandleAsync"/>, which verifies Kick's signature against the public key
/// from the API (re-fetched once on a mismatch, as Kick may rotate it), drops repeated message ids and raises
/// the decoded events.
/// </summary>
public sealed class KickWebhookEventSource(Func<CancellationToken, Task<string>> fetchPublicKey) : IKickEventSource
{
    private readonly RecentKeys _seen = new(TimeSpan.FromHours(1));
    private readonly SemaphoreSlim _keyGate = new(1, 1);
    private string? _publicKey;
    private bool _connected;

    public string Name => "Webhook relay";
    public bool IsConnected => _connected;
    public event Action<KickEvent>? EventReceived;
    public event Action<bool, string>? ConnectionChanged;

    /// <summary>Called by the relay connection when it attaches or detaches.</summary>
    public void SetConnected(bool connected, string reason)
    {
        if (_connected == connected) return;
        _connected = connected;
        ConnectionChanged?.Invoke(connected, reason);
    }

    public async Task<KickWebhookOutcome> HandleAsync(KickWebhookDelivery delivery, CancellationToken ct)
    {
        if (!await VerifyAsync(delivery, ct).ConfigureAwait(false)) return KickWebhookOutcome.BadSignature;
        if (!_seen.TryAdd(delivery.MessageId, DateTimeOffset.UtcNow)) return KickWebhookOutcome.Duplicate;
        IReadOnlyList<KickEvent> events;
        try
        {
            events = KickWebhookParser.Parse(delivery.EventType, delivery.Body);
        }
        catch (JsonException)
        {
            return KickWebhookOutcome.Malformed;
        }
        if (events.Count == 0) return KickWebhookOutcome.Ignored;
        foreach (var e in events) EventReceived?.Invoke(e);
        return KickWebhookOutcome.Accepted;
    }

    private async Task<bool> VerifyAsync(KickWebhookDelivery d, CancellationToken ct)
    {
        var key = _publicKey ?? await RefreshKeyAsync(null, ct).ConfigureAwait(false);
        if (KickWebhookVerifier.Verify(d.MessageId, d.Timestamp, d.Body, d.Signature, key)) return true;
        var fresh = await RefreshKeyAsync(key, ct).ConfigureAwait(false);
        return fresh != key && KickWebhookVerifier.Verify(d.MessageId, d.Timestamp, d.Body, d.Signature, fresh);
    }

    private async Task<string> RefreshKeyAsync(string? stale, CancellationToken ct)
    {
        await _keyGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_publicKey is not null && _publicKey != stale) return _publicKey;
            return _publicKey = await fetchPublicKey(ct).ConfigureAwait(false);
        }
        finally
        {
            _keyGate.Release();
        }
    }
}
