using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Discord.Gateway;

/// <summary>Gateway opcodes (discord.com/developers/docs/topics/opcodes-and-status-codes).</summary>
internal static class GatewayOp
{
    public const int Dispatch = 0;
    public const int Heartbeat = 1;
    public const int Identify = 2;
    public const int PresenceUpdate = 3;
    public const int Resume = 6;
    public const int Reconnect = 7;
    public const int InvalidSession = 9;
    public const int Hello = 10;
    public const int HeartbeatAck = 11;
}

/// <summary>Gateway intents Cuebot asks for.</summary>
[Flags]
public enum GatewayIntents
{
    None = 0,
    Guilds = 1 << 0,
    /// <summary>Privileged: "Server Members Intent" in the Developer Portal.</summary>
    GuildMembers = 1 << 1,
    GuildMessages = 1 << 9,
    /// <summary>Privileged: "Message Content Intent" in the Developer Portal.</summary>
    MessageContent = 1 << 15,
}

/// <summary>One frame from the Gateway.</summary>
internal readonly record struct GatewayMessage(int Op, int? Sequence, string? EventName, JsonElement Data)
{
    public static GatewayMessage Parse(string text)
    {
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        var op = root.GetProperty("op").GetInt32();
        int? s = root.TryGetProperty("s", out var sv) && sv.ValueKind == JsonValueKind.Number ? sv.GetInt32() : null;
        var t = root.TryGetProperty("t", out var tv) && tv.ValueKind == JsonValueKind.String ? tv.GetString() : null;
        var d = root.TryGetProperty("d", out var dv) ? dv.Clone() : default;
        return new GatewayMessage(op, s, t, d);
    }
}

/// <summary>What happened when a Gateway connection closed, and what to do next.</summary>
internal sealed record GatewayClose(string Message, bool Fatal, bool Resumable);

/// <summary>Payload builders and close-code rules (pure functions, unit-tested).</summary>
internal static class GatewayProtocol
{
    public const string Query = "v=10&encoding=json";
    public static readonly Uri DefaultUrl = new("wss://gateway.discord.gg/?" + Query);

    public static GatewayIntents Intents(DiscordBotSettings s)
    {
        var intents = GatewayIntents.Guilds | GatewayIntents.GuildMessages;
        if (s.ServerMembersIntent) intents |= GatewayIntents.GuildMembers;
        if (s.MessageContentIntent) intents |= GatewayIntents.MessageContent;
        return intents;
    }

    /// <summary>Adds v=10&amp;encoding=json to a Gateway or resume address (which Discord hands out without them).</summary>
    public static Uri WithQuery(string url)
    {
        var trimmed = url.Trim().TrimEnd('/');
        var q = trimmed.IndexOf('?');
        if (q >= 0) trimmed = trimmed[..q].TrimEnd('/');
        return new Uri(trimmed + "/?" + Query);
    }

    public static string Heartbeat(int? sequence) =>
        new JsonObject { ["op"] = GatewayOp.Heartbeat, ["d"] = sequence is { } s ? JsonValue.Create(s) : null }.ToJsonString();

    public static string Identify(string token, GatewayIntents intents, JsonObject? presence) =>
        new JsonObject
        {
            ["op"] = GatewayOp.Identify,
            ["d"] = new JsonObject
            {
                ["token"] = token,
                ["intents"] = (int)intents,
                ["properties"] = new JsonObject { ["os"] = Os(), ["browser"] = "Cuebot", ["device"] = "Cuebot" },
                ["presence"] = presence?.DeepClone(),
            },
        }.ToJsonString();

    public static string Resume(string token, string sessionId, int? sequence) =>
        new JsonObject
        {
            ["op"] = GatewayOp.Resume,
            ["d"] = new JsonObject { ["token"] = token, ["session_id"] = sessionId, ["seq"] = sequence is { } s ? JsonValue.Create(s) : null },
        }.ToJsonString();

    public static string PresenceUpdate(JsonObject presence) =>
        new JsonObject { ["op"] = GatewayOp.PresenceUpdate, ["d"] = presence.DeepClone() }.ToJsonString();

    /// <summary>The "d" of a presence update: status (online, idle, dnd, invisible) and one activity (or none).</summary>
    public static JsonObject Presence(string status, int? activityType, string text, string? streamUrl)
    {
        var activities = new JsonArray();
        if (activityType is { } type && text.Length > 0)
        {
            var activity = new JsonObject { ["name"] = type == ActivityTypes.Custom ? "Custom Status" : text, ["type"] = type };
            if (type == ActivityTypes.Custom) activity["state"] = text;
            if (type == ActivityTypes.Streaming && !string.IsNullOrWhiteSpace(streamUrl)) activity["url"] = streamUrl.Trim();
            activities.Add(activity);
        }
        return new JsonObject { ["since"] = null, ["activities"] = activities, ["status"] = status, ["afk"] = false };
    }

    /// <summary>Close codes (docs: Gateway Close Event Codes) as plain messages for streamers.</summary>
    public static GatewayClose DescribeClose(int? code, GatewayIntents intents) => code switch
    {
        4004 => new("Discord did not accept the bot token. In the Developer Portal open your app, then Bot, click Reset Token, " +
                    "copy the new token and paste it into Cuebot.", Fatal: true, Resumable: false),
        4014 => new(DisallowedIntents(intents), Fatal: true, Resumable: false),
        4013 => new("Discord refused the requested switches (invalid intents). Please report this to Cuebot.", Fatal: true, Resumable: false),
        4010 or 4011 => new("Discord says this bot is in too many servers for one connection (sharding). Cuebot supports bots in " +
                            "up to 2,500 servers.", Fatal: true, Resumable: false),
        4012 => new("Discord no longer accepts the Gateway version Cuebot uses. Please update Cuebot.", Fatal: true, Resumable: false),
        4007 or 4009 => new($"Discord ended the session ({code}); starting a new one", Fatal: false, Resumable: false),
        4008 => new("Cuebot sent too much too fast (4008); reconnecting", Fatal: false, Resumable: true),
        1000 or 1001 => new($"Discord closed the connection ({code})", Fatal: false, Resumable: false),
        null => new("connection lost", Fatal: false, Resumable: true),
        _ => new($"Discord closed the connection ({code})", Fatal: false, Resumable: true),
    };

    public static string DisallowedIntents(GatewayIntents intents)
    {
        var names = new List<string>();
        if (intents.HasFlag(GatewayIntents.MessageContent)) names.Add("\"Message Content Intent\"");
        if (intents.HasFlag(GatewayIntents.GuildMembers)) names.Add("\"Server Members Intent\"");
        var which = names.Count == 0 ? "the privileged switches you ticked" : string.Join(" and ", names);
        return $"Discord refused the bot's switches. In the Developer Portal open your app, then Bot, scroll to " +
               $"\"Privileged Gateway Intents\", turn on {which} and click Save Changes. Or untick them in Cuebot.";
    }

    private static string Os() =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
}

/// <summary>Activity types of a presence update.</summary>
internal static class ActivityTypes
{
    public const int Playing = 0, Streaming = 1, Listening = 2, Watching = 3, Custom = 4, Competing = 5;
}
