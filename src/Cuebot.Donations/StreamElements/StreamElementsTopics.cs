using System.Globalization;
using System.Text.Json;

namespace Cuebot.Donations.StreamElements;

/// <summary>The Astro topics Cuebot subscribes to (docs.streamelements.com/websockets/topics).</summary>
public static class StreamElementsTopics
{
    public const string Activities = "channel.activities";
    public const string Tips = "channel.tips";
    public const string TipsModeration = "channel.tips.moderation";
    public const string StreamStatus = "channel.stream.status";
    public const string SessionUpdate = "channel.session.update";
    public const string SessionReset = "channel.session.reset";
    public const string OverlayAction = "channel.overlay.action";
    public const string OverlayUpdate = "channel.overlay.update";
    public const string OverlayBroadcast = "channel.overlay.broadcast";
    public const string Counters = "channel.chatbot.counters";
    public const string Contests = "channel.contests";
    public const string Giveaways = "channel.giveaways";
    public const string SongRequest = "channel.songrequest";
    public const string AudienceQueue = "channel.chatbot.audiencequeue";
    public const string EmoteCombo = "channel.chatbot.modules.emotecombo";
    public const string Pyramid = "channel.chatbot.modules.pyramid";
    public const string BotStatus = "channel.chatbot.status";
    public const string Chat = "channel.chat";

    /// <summary>Every topic, activities first (its subscription decides the connection status).</summary>
    public static IReadOnlyList<string> For(bool chat) =>
    [
        Activities, Tips, TipsModeration, StreamStatus, SessionUpdate, SessionReset, OverlayAction, OverlayUpdate, OverlayBroadcast,
        Counters, Contests, Giveaways, SongRequest, AudienceQueue, EmoteCombo, Pyramid, BotStatus, .. (chat ? new[] { Chat } : Array.Empty<string>()),
    ];
}

/// <summary>
/// Maps the payloads of the Astro topics other than activities, tips and session data to bot events. Payload shapes
/// are those of the topic pages; unknown fields are ignored and missing ones read as empty.
/// </summary>
internal static class StreamElementsTopicMapper
{
    /// <summary>Keys never copied into event args: donor e-mail and location ride along in tip documents.</summary>
    private static readonly HashSet<string> Private = new(StringComparer.OrdinalIgnoreCase) { "email", "geo" };

    public static DonationEvent? Map(string topic, JsonElement data) => data.ValueKind != JsonValueKind.Object ? null : topic switch
    {
        StreamElementsTopics.OverlayAction => Event(StreamElementsEventTypes.OverlayAction, new() { ["action"] = data.Str("action") }),
        StreamElementsTopics.OverlayUpdate => OverlayUpdate(data),
        StreamElementsTopics.OverlayBroadcast => Broadcast(data),
        StreamElementsTopics.Counters => Event(StreamElementsEventTypes.CounterChanged,
            new() { ["counter"] = data.Str("counter"), ["value"] = (long)data.Number("value") }),
        StreamElementsTopics.Contests => Contest(data),
        StreamElementsTopics.Giveaways => Giveaway(data),
        StreamElementsTopics.SongRequest => SongRequest(data),
        StreamElementsTopics.AudienceQueue => AudienceQueue(data),
        StreamElementsTopics.EmoteCombo => Event(StreamElementsEventTypes.EmoteCombo, new()
        {
            ["user"] = data.Str("user_name"), ["userName"] = data.Str("user_name"), ["userId"] = data.Str("user_id"),
            ["emote"] = data.Str("emote"), ["count"] = (int)data.Number("count"), ["msgId"] = data.Str("msg_id"),
        }),
        StreamElementsTopics.Pyramid => Event(StreamElementsEventTypes.Pyramid, new()
        {
            ["user"] = data.Str("user_name"), ["userName"] = data.Str("user_name"), ["userId"] = data.Str("user_id"),
            ["word"] = data.Str("word"), ["width"] = (int)data.Number("width"), ["msgId"] = data.Str("msg_id"),
        }),
        StreamElementsTopics.BotStatus => BotStatus(data),
        _ => null,
    };

    private static DonationEvent Event(string type, Dictionary<string, object?> args, IReadOnlyList<string>? keys = null)
    {
        args["service"] = StreamElementsEventMapper.Service;
        args["isTest"] = false;
        return new DonationEvent(type, keys ?? [], new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase));
    }

    private static DonationEvent OverlayUpdate(JsonElement data) => Event(StreamElementsEventTypes.OverlayUpdated, new()
    {
        ["overlayId"] = data.Str("_id"),
        ["overlayName"] = data.Str("name"),
        ["overlayType"] = data.Str("type"),
        ["widgetCount"] = data.Items("widgets").Count(),
        ["width"] = (int)(data.Child("settings")?.Number("width") ?? 0),
        ["height"] = (int)(data.Child("settings")?.Number("height") ?? 0),
        ["updatedAt"] = data.Str("updatedAt"),
    });

    private static DonationEvent Broadcast(JsonElement data)
    {
        var args = new Dictionary<string, object?>
        {
            ["event"] = data.Str("event"),
            ["target"] = data.Str("target"),
            ["dataJson"] = data.Child("data") is { } payload ? payload.GetRawText() : "",
        };
        if (data.Child("data") is { ValueKind: JsonValueKind.Object } d) Flatten(d, "data.", args);
        return Event(StreamElementsEventTypes.OverlayBroadcast, args);
    }

    private static DonationEvent? Contest(JsonElement data)
    {
        var payload = data.Child("payload") ?? default;
        return data.Str("event") switch
        {
            "update" => Event(StreamElementsEventTypes.ContestBet, new()
            {
                ["optionId"] = payload.Str("optionId"), ["amount"] = payload.Number("amount"), ["userId"] = payload.Str("userId"),
            }),
            "state" => Event(StreamElementsEventTypes.ContestState, new()
            {
                ["state"] = payload.Str("state"), ["contestId"] = payload.Str("contestId"),
            }),
            "winner" => Event(StreamElementsEventTypes.ContestWinner, new() { ["winnerId"] = payload.Str("winnerId") }),
            _ => null,
        };
    }

    private static DonationEvent? Giveaway(JsonElement data)
    {
        var inner = data.Child("data") ?? default;
        var args = new Dictionary<string, object?>
        {
            ["giveawayId"] = data.Str("giveawayId"),
            ["event"] = data.Str("event"),
            ["state"] = data.Str("state"),
            ["totalUsers"] = (int)inner.Number("totalUsers"),
            ["totalTickets"] = (long)inner.Number("totalTickets"),
        };
        void User(JsonElement? u)
        {
            if (u is not { ValueKind: JsonValueKind.Object } user) return;
            args["user"] = user.Str("username");
            args["userName"] = user.Str("username");
            args["userId"] = user.Str("providerId");
            args["tickets"] = (long)user.Number("tickets");
            args["isSubscriber"] = user.Child("subscriber") is { ValueKind: JsonValueKind.True };
            args["isNew"] = user.Child("new") is { ValueKind: JsonValueKind.True };
        }
        switch (data.Str("event"))
        {
            case "enter":
                User(inner.Child("entry"));
                return Event(StreamElementsEventTypes.GiveawayEntered, args);
            case "winner":
                User(inner.Child("winner"));
                return Event(StreamElementsEventTypes.GiveawayWinner, args);
            case "":
                return null;
            default:
                User(inner.Child("entry"));
                return Event(StreamElementsEventTypes.GiveawayState, args);
        }
    }

    private static DonationEvent? SongRequest(JsonElement data)
    {
        var name = data.Str("event");
        if (name.Length == 0) return null;
        var payload = data.Child("payload") ?? default;
        var args = new Dictionary<string, object?>
        {
            ["event"] = name,
            ["queue"] = payload.Str("queue"),
            ["songId"] = payload.Str("songId"),
        };
        if (payload.Child("volume") is not null) args["volume"] = (int)payload.Number("volume");
        if (payload.Child("position") is not null) args["position"] = (int)payload.Number("position");
        if (payload.Child("approvedBy") is not null) args["approvedBy"] = payload.Str("approvedBy");
        if (payload.Child("totalVotes") is not null)
        {
            args["totalVotes"] = (int)payload.Number("totalVotes");
            args["votesRequired"] = (int)payload.Number("votesRequired");
            args["completed"] = payload.Child("completed") is { ValueKind: JsonValueKind.True };
        }
        if (payload.Child("user") is { ValueKind: JsonValueKind.String }) args["purgedUser"] = payload.Str("user");
        if ((payload.Child("song") ?? payload.Child("nextSong")) is { ValueKind: JsonValueKind.Object } song)
        {
            var s = StreamElementsApi.Song(song);
            args["songId"] = s.Id;
            args["songTitle"] = s.Title;
            args["songUrl"] = song.Str("url") is { Length: > 0 } url ? url : s.Url;
            args["songDuration"] = s.Duration;
            args["songSource"] = s.Source;
            args["user"] = s.UserName;
            args["userName"] = song.Child("user")?.Str("username") ?? "";
            args["message"] = song.Str("message");
            args["amount"] = song.Number("amount");
        }
        return Event(StreamElementsEventTypes.SongRequest, args);
    }

    private static DonationEvent? AudienceQueue(JsonElement data)
    {
        var name = data.Str("event");
        if (name.Length == 0) return null;
        var payload = data.Child("payload") ?? default;
        var args = new Dictionary<string, object?> { ["event"] = name };
        if (name.StartsWith("member.", StringComparison.Ordinal))
        {
            args["user"] = payload.First("displayName", "username");
            args["userName"] = payload.Str("username");
            args["userId"] = payload.Str("id");
            args["position"] = (int)payload.Number("position");
            args["watchTime"] = (long)payload.Number("watchTime");
            args["groups"] = string.Join(", ", payload.Items("audienceGroups").Select(g => g.ToString()));
        }
        else if (payload.ValueKind == JsonValueKind.Object)
        {
            args["title"] = payload.Str("title");
            args["isSuspended"] = payload.Child("isSuspended") is { ValueKind: JsonValueKind.True };
            args["maxQueueSize"] = (int)payload.Number("maxQueueSize");
            args["status"] = payload.Items("statusChangeLog").LastOrDefault() is { ValueKind: JsonValueKind.Object } last ? last.Str("status") : "";
        }
        return Event(StreamElementsEventTypes.ViewerQueue, args);
    }

    private static DonationEvent? BotStatus(JsonElement data)
    {
        var args = new Dictionary<string, object?>();
        if (data.Child("mod") is { } mod) args["isMod"] = mod.ValueKind == JsonValueKind.True;
        if (data.Child("banned") is { } banned) args["isBanned"] = banned.ValueKind == JsonValueKind.True;
        if (args.Count == 0) return null;
        args["change"] = args.ContainsKey("isMod") ? "mod" : "banned";
        return Event(StreamElementsEventTypes.BotStatus, args);
    }

    /// <summary>
    /// Copies an object's fields into args as <c>{prefix}{key}</c> (nested objects as <c>{prefix}{key}.{child}</c>, up to
    /// three levels; arrays as JSON text). Never copies e-mail or location fields.
    /// </summary>
    public static void Flatten(JsonElement obj, string prefix, Dictionary<string, object?> args, int depth = 0)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (Private.Contains(property.Name)) continue;
            var key = prefix + property.Name;
            var value = property.Value;
            switch (value.ValueKind)
            {
                case JsonValueKind.Object when depth < 2:
                    Flatten(value, key + ".", args, depth + 1);
                    break;
                case JsonValueKind.Object or JsonValueKind.Array:
                    args[key] = value.GetRawText();
                    break;
                default:
                    args[key] = Scalar(value);
                    break;
            }
        }
    }

    /// <summary>A JSON scalar as a typed value (string, long, double, bool or null).</summary>
    public static object? Scalar(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.TryGetInt64(out var l) ? (object)l : value.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => value.GetRawText(),
    };

    internal static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
}
