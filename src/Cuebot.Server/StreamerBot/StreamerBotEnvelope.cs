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

    /// <summary>A Cuebot bus event as a Streamer.bot event (args become <c>data</c>).</summary>
    public static (StreamerBotEventName Name, JsonObject Message) FromBotEvent(BotEvent e)
    {
        var name = StreamerBotEventMap.Map(e.Type, e.Args);
        var data = Data(e.Args);
        data["cuebotEvent"] = e.Type;
        AddCompatFields(name, e.Args, data);
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

    /// <summary>
    /// Adds the nested <c>user</c> object and <c>text</c> that Streamer.bot chat payloads carry, so overlays written
    /// for them find a user name and message without knowing Cuebot's flat argument names.
    /// </summary>
    private static void AddCompatFields(StreamerBotEventName name, IReadOnlyDictionary<string, object?> args, JsonObject data)
    {
        var user = Arg(args, "user") ?? Arg(args, "userName");
        if (user is not null && data["user"] is null or JsonValue)
        {
            // The flat name stays available as userName.
            data["userName"] ??= user;
            data["user"] = new JsonObject
            {
                ["id"] = Arg(args, "userId") ?? "",
                ["login"] = (Arg(args, "userName") ?? user).ToLowerInvariant(),
                ["name"] = user,
                ["display_name"] = user,
                ["color"] = Arg(args, "color") ?? "",
                ["role"] = Role(args),
            };
        }
        if (Arg(args, "message") is { } text && name.Type is "ChatMessage" or "Message" or "Whisper")
        {
            data["text"] = text;
        }
    }

    private static string? Arg(IReadOnlyDictionary<string, object?> args, string key) =>
        args.TryGetValue(key, out var v) && v is not null ? v.ToString() : null;

    private static int Role(IReadOnlyDictionary<string, object?> args)
    {
        bool Flag(string key) => args.TryGetValue(key, out var v) && v is true;
        return Flag("isBroadcaster") ? 4 : Flag("isModerator") ? 3 : Flag("isVip") ? 2 : 1;
    }
}
