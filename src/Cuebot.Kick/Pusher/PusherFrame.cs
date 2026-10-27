using System.Text.Json;

namespace Cuebot.Kick.Pusher;

/// <summary>
/// One Pusher protocol (v7) message: <c>{"event": ..., "channel": ..., "data": ...}</c>. Pusher sends
/// <c>data</c> as a JSON-encoded string; <see cref="Data"/> holds that string decoded (or the raw object/value).
/// </summary>
internal sealed record PusherFrame(string Event, string? Channel, string Data)
{
    public const string ConnectionEstablished = "pusher:connection_established";
    public const string Error = "pusher:error";
    public const string Ping = "pusher:ping";
    public const string Pong = "pusher:pong";
    public const string Subscribe = "pusher:subscribe";
    public const string SubscriptionSucceeded = "pusher_internal:subscription_succeeded";
    public const string SubscriptionError = "pusher:subscription_error";

    /// <summary>Parses a frame; throws <see cref="JsonException"/> when it is not a Pusher message.</summary>
    public static PusherFrame Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.Prop("event") is not { ValueKind: JsonValueKind.String } ev)
            throw new JsonException("Not a Pusher message");
        var data = root.Prop("data") switch
        {
            { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
            { } other => other.GetRawText(),
            null => "",
        };
        return new PusherFrame(ev.GetString()!, root.Str("channel") is { Length: > 0 } c ? c : null, data);
    }

    /// <summary>The event name without Kick's PHP namespace (<c>App\Events\ChatMessageEvent</c> → <c>ChatMessageEvent</c>).</summary>
    public string ShortEvent => Event[(Event.LastIndexOf('\\') + 1)..];

    public static string SubscribeMessage(string channel) =>
        JsonSerializer.Serialize(new { @event = Subscribe, data = new { auth = "", channel } });

    public static string PingMessage { get; } = JsonSerializer.Serialize(new { @event = Ping, data = new { } });

    public static string PongMessage { get; } = JsonSerializer.Serialize(new { @event = Pong, data = new { } });

    /// <summary>socket_id and activity_timeout (seconds) from a connection_established frame.</summary>
    public (string SocketId, int ActivityTimeoutSeconds) ReadEstablished()
    {
        using var doc = JsonDocument.Parse(Data.Length == 0 ? "{}" : Data);
        return (doc.RootElement.Str("socket_id"), (int)doc.RootElement.Long("activity_timeout"));
    }

    /// <summary>code (0 when absent) and message from a pusher:error frame.</summary>
    public (int Code, string Message) ReadError()
    {
        try
        {
            using var doc = JsonDocument.Parse(Data.Length == 0 ? "{}" : Data);
            return ((int)doc.RootElement.Long("code"), doc.RootElement.Str("message"));
        }
        catch (JsonException)
        {
            return (0, Data);
        }
    }
}
