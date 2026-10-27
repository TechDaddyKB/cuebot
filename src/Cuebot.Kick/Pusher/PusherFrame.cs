using System.Text.Json;

namespace Cuebot.Kick.Pusher;

/// <summary>
/// One Kick real-time event: <c>{"event": ..., "channel": ..., "data": ...}</c>. The shape comes from Kick's former
/// Pusher feed and is kept for the Centrifugo envelopes that replaced it. Kick sends <c>data</c> as a JSON-encoded
/// string; <see cref="Data"/> holds that string decoded (or the raw object/value).
/// </summary>
internal sealed record PusherFrame(string Event, string? Channel, string Data)
{
    /// <summary>Parses a frame; throws <see cref="JsonException"/> when it has no event name.</summary>
    public static PusherFrame Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.Prop("event") is not { ValueKind: JsonValueKind.String } ev)
            throw new JsonException("Not a Kick event");
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
}
