using System.Text.Json;

namespace Cuebot.Obs.Streamlabs;

/// <summary>One SockJS frame: 'o' open, 'h' heartbeat, 'a'/'m' messages, 'c' close.</summary>
internal sealed record SockJsFrame(char Type, IReadOnlyList<string> Messages, string? CloseReason = null);

/// <summary>
/// SockJS websocket-transport framing (pure). Streamlabs Desktop serves its Remote Control API through SockJS at
/// <c>/api</c>; the client talks to <c>/api/{server}/{session}/websocket</c> and every frame is prefixed with a type
/// letter. Unframed JSON (a raw WebSocket endpoint) is accepted too.
/// </summary>
internal static class SockJsFraming
{
    /// <summary>The websocket URL for a fresh SockJS session under <paramref name="baseUri"/> (e.g. ws://127.0.0.1:59650/api).</summary>
    public static Uri SessionUri(Uri baseUri)
    {
        var server = Random.Shared.Next(0, 1000).ToString("000", System.Globalization.CultureInfo.InvariantCulture);
        var session = Guid.NewGuid().ToString("N")[..12];
        return new Uri($"{baseUri.ToString().TrimEnd('/')}/{server}/{session}/websocket");
    }

    /// <summary>What the client sends for one message: a JSON array holding the message string.</summary>
    public static string Encode(string message) => JsonSerializer.Serialize(new[] { message });

    public static SockJsFrame Decode(string frame)
    {
        if (frame.Length == 0) return new SockJsFrame('h', []);
        switch (frame[0])
        {
            case 'o' or 'h':
                return new SockJsFrame(frame[0], []);
            case 'a':
                return new SockJsFrame('a', JsonSerializer.Deserialize<string[]>(frame.AsSpan(1)) ?? []);
            case 'm':
                return new SockJsFrame('a', [JsonSerializer.Deserialize<string>(frame.AsSpan(1)) ?? ""]);
            case 'c':
                using (var close = JsonDocument.Parse(frame.AsMemory(1)))
                {
                    var parts = close.RootElement.EnumerateArray().ToArray();
                    var reason = parts.Length > 1 ? parts[1].GetString() : null;
                    var code = parts.Length > 0 && parts[0].ValueKind == JsonValueKind.Number ? parts[0].GetInt32() : 0;
                    return new SockJsFrame('c', [], $"Streamlabs Desktop closed the connection ({code}{(reason is null ? "" : $": {reason}")}).");
                }
            case '{' or '[':
                return new SockJsFrame('a', [frame]);
            default:
                throw new FormatException($"Unknown SockJS frame '{frame[0]}'.");
        }
    }
}
