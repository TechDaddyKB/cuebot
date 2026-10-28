using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Events;
using Cuebot.Scripting.Compat;

namespace Cuebot.Server.StreamerBot;

/// <summary>Builds Streamer.bot WebSocket event messages: <c>{timeStamp, event: {source, type}, data}</c>.</summary>
public static class StreamerBotEnvelope
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static JsonObject Create(StreamerBotEventName name, JsonNode? data, DateTimeOffset time) => new()
    {
        ["timeStamp"] = time.ToString("o"),
        ["event"] = new JsonObject { ["source"] = name.Source, ["type"] = name.Type },
        ["data"] = data ?? new JsonObject(),
    };

    /// <summary>
    /// A Cuebot bus event as a Streamer.bot event: the flat args, the documented nested fields of
    /// <see cref="StreamerBotPayloads"/> on top, and <c>cuebotEvent</c> (the Cuebot event type).
    /// </summary>
    public static (StreamerBotEventName Name, JsonObject Message) FromBotEvent(BotEvent e)
    {
        var name = StreamerBotEventMap.Map(e.Type, e.Args);
        var data = Data(e.Args);
        data["cuebotEvent"] = e.Type;
        // The flat user name stays available as userName when the user object replaces "user".
        if (data["userName"] is null && e.Args.GetValueOrDefault("user") is { } user) data["userName"] = ToNode(user);
        StreamerBotPayloads.Apply(name, e.Args, data);
        if (data["text"] is null && e.Args.GetValueOrDefault("message") is string text && name.Type is "ChatMessage" or "Message" or "Whisper")
        {
            data["text"] = text;
        }
        return (name, Create(name, data, e.Time));
    }

    public static JsonObject Data(IReadOnlyDictionary<string, object?> args)
    {
        var data = new JsonObject();
        foreach (var (key, value) in args)
        {
            data[key] = ToNode(value);
        }
        return data;
    }

    private static JsonNode? ToNode(object? value)
    {
        try
        {
            return value switch
            {
                null => null,
                JsonElement e => JsonNode.Parse(e.GetRawText()),
                JsonNode n => n.DeepClone(),
                _ => JsonSerializer.SerializeToNode(value, value.GetType(), Options),
            };
        }
        catch (Exception ex) when (ex is NotSupportedException or JsonException or InvalidOperationException)
        {
            return value?.ToString();
        }
    }
}
