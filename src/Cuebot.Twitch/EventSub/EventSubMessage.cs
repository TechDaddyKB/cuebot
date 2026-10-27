using System.Text.Json;

namespace Cuebot.Twitch.EventSub;

/// <summary>Message types of the EventSub WebSocket protocol.</summary>
internal static class EventSubMessageTypes
{
    public const string Welcome = "session_welcome";
    public const string Keepalive = "session_keepalive";
    public const string Notification = "notification";
    public const string Reconnect = "session_reconnect";
    public const string Revocation = "revocation";
}

/// <summary>A parsed EventSub WebSocket message (metadata plus payload).</summary>
internal sealed record EventSubMessage(string MessageId, string MessageType, JsonElement Payload)
{
    /// <exception cref="JsonException">The text is not a JSON object.</exception>
    public static EventSubMessage Parse(string json)
    {
        var root = JsonSerializer.Deserialize<JsonElement>(json);
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException("EventSub message is not a JSON object");
        var metadata = root.Child("metadata") ?? default;
        return new EventSubMessage(metadata.Str("message_id"), metadata.Str("message_type"), root.Child("payload") ?? default);
    }

    private JsonElement Session => Payload.Child("session") ?? default;
    private JsonElement Subscription => Payload.Child("subscription") ?? default;

    public string SessionId => Session.Str("id");

    public int? KeepaliveTimeoutSeconds =>
        Session.Child("keepalive_timeout_seconds") is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var seconds)
            ? seconds
            : null;

    public string ReconnectUrl => Session.Str("reconnect_url");

    public string SubscriptionType => Subscription.Str("type");

    public string SubscriptionStatus => Subscription.Str("status");

    public JsonElement Event => Payload.Child("event") ?? default;
}

/// <summary>An event delivered by EventSub, e.g. a <c>channel.follow</c> with its event body.</summary>
internal sealed record EventSubNotification(string MessageId, string SubscriptionType, JsonElement Event);
