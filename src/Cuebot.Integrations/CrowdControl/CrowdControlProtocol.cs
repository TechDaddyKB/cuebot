using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Web;
using Cuebot.Integrations.Common;

namespace Cuebot.Integrations.CrowdControl;

/// <summary>Bot event types of the Crowd Control integration.</summary>
public static class CrowdControlEvents
{
    public const string Prefix = "crowdcontrol";
    public const string Connected = "crowdcontrol.connected";
    public const string Disconnected = "crowdcontrol.disconnected";
    public const string EffectRequest = "crowdcontrol.effectRequest";
    public const string EffectSuccess = "crowdcontrol.effectSuccess";
    public const string EffectFailure = "crowdcontrol.effectFailure";

    /// <summary>Cuebot extra: the coins of a request were refunded.</summary>
    public const string EffectRefund = "crowdcontrol.effectRefund";

    /// <summary>Cuebot extra: the game asked to retry a request.</summary>
    public const string EffectRetry = "crowdcontrol.effectRetry";

    public const string TimedEffectStarted = "crowdcontrol.timedEffectStarted";
    public const string TimedEffectEnded = "crowdcontrol.timedEffectEnded";

    /// <summary>A timed effect was paused or resumed (timedStatus = "pause" / "resume").</summary>
    public const string TimedEffectUpdated = "crowdcontrol.timedEffectUpdated";

    public const string CoinExchange = "crowdcontrol.coinExchange";
    public const string GameSessionStart = "crowdcontrol.gameSessionStart";
    public const string GameSessionEnd = "crowdcontrol.gameSessionEnd";

    /// <summary>Cuebot extra: the game session was paused or resumed (paused = true / false).</summary>
    public const string GameSessionPaused = "crowdcontrol.gameSessionPaused";
}

/// <summary>How Cuebot authenticates the PubSub subscription: an overlay key, or a Crowd Control JWT.</summary>
internal sealed record CrowdControlCredential(string Value, bool IsToken);

/// <summary>A Crowd Control PubSub message: <c>{ domain, type, payload, timestamp }</c>.</summary>
internal sealed record CrowdControlMessage(string Domain, string Type, JsonElement Payload);

/// <summary>A mapped PubSub event: the bot event name (without "crowdcontrol.") and its args.</summary>
internal sealed record CrowdControlEvent(string Name, Dictionary<string, object?> Args);

/// <summary>Crowd Control PubSub framing (see PROTOCOL.md; developer.crowdcontrol.live/sockets).</summary>
internal static partial class CrowdControlProtocol
{
    public static readonly Uri DefaultPubSub = new("wss://pubsub.crowdcontrol.live/");

    /// <summary>The topics the Crowd Control overlay itself subscribes to; "self" is the owner of the key.</summary>
    public static readonly string[] Topics = ["pub/self", "prv/self", "overlay/self"];

    public const string PrivateTopic = "prv/self";

    [GeneratedRegex(@"^[A-Za-z0-9_\-+/=]{8,512}$")]
    private static partial Regex KeyPattern();

    /// <summary>
    /// The overlay key from the Overlay URL (its <c>key</c> query parameter, also inside a #fragment), a bare key, or a
    /// pasted JWT (three dot-separated parts). Null with a user-facing reason otherwise.
    /// </summary>
    public static (CrowdControlCredential? Credential, string? Error) ParseCredential(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) return (null, "Paste your Crowd Control Overlay URL first.");
        if (Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
        {
            var key = Query(url.Query, "key") ?? Query(FragmentQuery(url.Fragment), "key");
            if (key is { Length: > 0 }) return Credential(key);
            var token = Query(url.Query, "token") ?? Query(FragmentQuery(url.Fragment), "token");
            if (token is { Length: > 0 }) return Credential(token);
            return (null, "That URL has no overlay key. In the Crowd Control app open Overlays & Assets and copy your overlay URL (it ends in ?key=...).");
        }
        return Credential(text);

        static (CrowdControlCredential?, string?) Credential(string value)
        {
            value = value.Trim();
            if (value.Count(c => c == '.') == 2 && value.Split('.').All(p => p.Length > 0)) return (new CrowdControlCredential(value, IsToken: true), null);
            return KeyPattern().IsMatch(value)
                ? (new CrowdControlCredential(value, IsToken: false), null)
                : (null, "That does not look like a Crowd Control overlay URL or key.");
        }
    }

    private static string? Query(string query, string name) =>
        query.Length == 0 ? null : HttpUtility.ParseQueryString(query.TrimStart('?')).Get(name);

    private static string FragmentQuery(string fragment)
    {
        var at = fragment.IndexOf('?');
        return at < 0 ? "" : fragment[at..];
    }

    /// <summary>The subscribe request, as the Crowd Control overlay sends it (<c>key</c> for overlay keys, <c>token</c> for JWTs).</summary>
    public static string Subscribe(CrowdControlCredential credential) => JsonUtil.Serialize(new Dictionary<string, object>
    {
        ["action"] = "subscribe",
        ["data"] = new Dictionary<string, object> { ["topics"] = Topics, [credential.IsToken ? "token" : "key"] = credential.Value },
    });

    public const string Ping = """{"action":"ping"}""";

    public const string Pong = "pong";

    public static CrowdControlMessage? Parse(string text)
    {
        if (JsonUtil.TryParse(text) is not { ValueKind: JsonValueKind.Object } root) return null;
        var payload = root.Prop("payload") is { } p ? p : default;
        return new CrowdControlMessage(root.Str("domain"), root.Str("type"), payload);
    }

    /// <summary>The success and failure topic lists of a <c>direct/subscription-result</c>.</summary>
    public static (string[] Success, string[] Failure) SubscriptionResult(JsonElement payload) =>
        ([.. payload.Array("success").Select(e => e.ToString())], [.. payload.Array("failure").Select(e => e.ToString())]);

    /// <summary>
    /// Maps a PubSub message to a bot event. Effect and coin events are taken from the private domain only (it carries
    /// prices; the public copies would duplicate them); session events from the public domain. Null for everything else.
    /// </summary>
    public static CrowdControlEvent? Map(CrowdControlMessage message, bool isTest = false)
    {
        var p = message.Payload;
        if (p.ValueKind != JsonValueKind.Object) return null;
        switch (message.Domain, message.Type)
        {
            case ("prv", "effect-request"): return Effect("effectRequest", p, isTest);
            case ("prv", "effect-success"): return Effect("effectSuccess", p, isTest);
            case ("prv", "effect-refund"): return Effect("effectRefund", p, isTest);
            case ("prv", "effect-retry"): return Effect("effectRetry", p, isTest);
            case ("prv", "effect-failure"):
            {
                var e = Effect("effectFailure", p, isTest);
                e.Args["temporary"] = p.Bool("temporary") ?? false;
                return e;
            }
            case ("prv", "timed-effect-update"):
            {
                var status = p.Str("status").ToLowerInvariant();
                var name = status switch
                {
                    "begin" => "timedEffectStarted",
                    "end" => "timedEffectEnded",
                    "pause" or "resume" => "timedEffectUpdated",
                    _ => null,
                };
                if (name is null) return null;
                var e = Effect(name, p, isTest);
                e.Args["timedStatus"] = status;
                var remaining = p.Num("timeRemaining") ?? 0;
                e.Args["timeRemaining"] = (long)remaining;
                e.Args["timeRemainingSeconds"] = Math.Round(remaining / 1000.0, 1);
                return e;
            }
            case ("prv", "coin-exchange"): return Coins(p, isTest);
            case ("pub", "game-session-start"): return Session("gameSessionStart", p, isTest);
            case ("pub", "game-session-stop"): return Session("gameSessionEnd", p, isTest);
            case ("pub", "game-session-pause"):
            {
                var e = Session("gameSessionPaused", p, isTest);
                var paused = p.Prop("gamePausedAt") is { ValueKind: JsonValueKind.Number };
                e.Args["paused"] = paused;
                e.Args["pausedUntil"] = p.Num("gamePausedUntil") is { } until ? Time(until) : "";
                return e;
            }
            default:
                return null;
        }
    }

    private static CrowdControlEvent Effect(string name, JsonElement p, bool isTest)
    {
        var effect = p.Obj("effect");
        var game = p.Obj("game");
        var pack = p.Obj("gamePack");
        var requester = p.Obj("requester");
        var display = p.Obj("userDisplay");
        var target = p.Obj("target");
        var anonymous = p.Bool("anonymous") ?? false;
        var user = requester.Str("name") is { Length: > 0 } rn ? rn : display.Str("name");
        if (user.Length == 0 && anonymous) user = "Anonymous";
        var quantity = p.Num("quantity") is { } q && q > 0 ? (int)q : 1;
        var price = p.Num("price");
        var unitPrice = p.Num("unitPrice");

        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["requestId"] = p.Str("requestID"),
            ["effectId"] = effect.Str("effectID", "id"),
            ["effectName"] = Name(effect.Prop("name")),
            ["effectDescription"] = effect.Str("description"),
            ["effectType"] = effect.Str("type"),
            ["effectImage"] = effect.Str("image"),
            ["effectDuration"] = effect.Num("duration") ?? 0,
            ["effectCategory"] = string.Join(", ", effect.Array("category").Select(c => c.ToString())),
            ["gameId"] = game.Str("gameID"),
            ["gameName"] = Name(game.Prop("name")),
            ["gamePackId"] = pack.Str("gamePackID"),
            ["gamePackName"] = pack.Str("name") is { Length: > 0 } pn ? pn : Name(pack.Obj("meta").Prop("name")),
            ["gamePackPlatform"] = pack.Str("platform"),
            ["user"] = user,
            ["userName"] = user,
            ["userId"] = requester.Str("originID"),
            ["userPlatform"] = requester.Str("profile"),
            ["userCcUid"] = requester.Str("ccUID") is { Length: > 0 } uid ? uid : display.Str("ccUID"),
            ["userImage"] = requester.Str("image") is { Length: > 0 } img ? img : display.Str("image"),
            ["anonymous"] = anonymous,
            ["targetName"] = target.Str("name"),
            ["targetPlatform"] = target.Str("profile"),
            ["quantity"] = quantity,
            ["price"] = price is { } pr ? (long)pr : 0L,
            ["unitPrice"] = unitPrice is { } up ? (long)up : price is { } total ? (long)Math.Round(total / quantity) : 0L,
            ["message"] = p.Str("message"),
            ["parameters"] = Parameters(p.Prop("parameters")),
            ["sourceType"] = p.Obj("sourceDetails").Str("type"),
            ["pooled"] = p.Bool("pooled") ?? false,
            ["admin"] = p.Bool("admin") ?? false,
            ["timestamp"] = p.Num("timestamp") is { } ts ? Time(ts) : "",
            ["isTest"] = isTest,
        };
        return new CrowdControlEvent(name, args);
    }

    private static CrowdControlEvent Coins(JsonElement p, bool isTest)
    {
        var exchanger = p.Obj("exchanger");
        var display = p.Obj("userDisplay");
        var recipient = p.Obj("recipient");
        var anonymous = p.Bool("anonymous") ?? false;
        var user = exchanger.Str("name") is { Length: > 0 } n ? n : display.Str("name");
        if (user.Length == 0 && anonymous) user = "Anonymous";
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["transactionId"] = p.Str("transactionID"),
            ["coinType"] = p.Str("coinType"),
            ["amount"] = (long)(p.Num("amount") ?? 0),
            ["bonus"] = (long)(p.Num("bonus") ?? 0),
            ["source"] = p.Str("source"),
            ["user"] = user,
            ["userName"] = user,
            ["userId"] = exchanger.Str("originID"),
            ["userPlatform"] = exchanger.Str("profile"),
            ["userCcUid"] = exchanger.Str("ccUID"),
            ["userImage"] = exchanger.Str("image"),
            ["recipientName"] = recipient.Str("name"),
            ["anonymous"] = anonymous,
            ["timestamp"] = p.Num("timestamp") is { } ts ? Time(ts) : "",
            ["isTest"] = isTest,
        };
        return new CrowdControlEvent("coinExchange", args);
    }

    private static CrowdControlEvent Session(string name, JsonElement p, bool isTest)
    {
        var game = p.Obj("game");
        var pack = p.Obj("gamePack");
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["gameSessionId"] = p.Str("gameSessionID"),
            ["gameId"] = game.Str("gameID"),
            ["gameName"] = Name(game.Prop("name")),
            ["gamePackId"] = p.Str("gamePackID") is { Length: > 0 } id ? id : pack.Str("gamePackID"),
            ["gamePackName"] = pack.Str("name") is { Length: > 0 } pn ? pn : Name(pack.Obj("meta").Prop("name")),
            ["platform"] = p.Str("profileType"),
            ["isTest"] = isTest,
        };
        return new CrowdControlEvent(name, args);
    }

    /// <summary>Names are plain strings or "sortable strings" <c>{ public, sort }</c>.</summary>
    private static string Name(JsonElement? name) => name switch
    {
        { ValueKind: JsonValueKind.String } s => s.GetString() ?? "",
        { ValueKind: JsonValueKind.Object } o => o.Str("public") is { Length: > 0 } pub ? pub : o.Str("sort"),
        _ => "",
    };

    /// <summary>Effect parameters (a map or list of <c>{ title, value, name }</c>) as "Title: value, ...".</summary>
    private static string Parameters(JsonElement? parameters)
    {
        IEnumerable<JsonElement> items = parameters switch
        {
            { ValueKind: JsonValueKind.Object } o => o.EnumerateObject().Select(x => x.Value),
            { ValueKind: JsonValueKind.Array } a => a.EnumerateArray(),
            _ => [],
        };
        return string.Join(", ", items.Where(i => i.ValueKind == JsonValueKind.Object).Select(i =>
        {
            var label = i.Str("title");
            var value = i.Str("name") is { Length: > 0 } n ? n : i.Str("value");
            return label.Length > 0 ? $"{label}: {value}" : value;
        }));
    }

    private static string Time(double unix) =>
        (unix > 1e11 ? DateTimeOffset.FromUnixTimeMilliseconds((long)unix) : DateTimeOffset.FromUnixTimeSeconds((long)unix))
        .ToString("o", CultureInfo.InvariantCulture);

    /// <summary>Realistic sample messages (field names as documented for the PubSub prv/pub domains) for "Send test event".</summary>
    public static readonly IReadOnlyDictionary<string, string> Samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["effectRequest"] = EffectSample("effect-request", ""),
        ["effectSuccess"] = EffectSample("effect-success", ""),
        ["effectFailure"] = EffectSample("effect-failure", ""","temporary":false"""),
        ["effectRefund"] = EffectSample("effect-refund", ""),
        ["timedEffectStarted"] = EffectSample("timed-effect-update", ""","status":"begin","timeRemaining":30000"""),
        ["timedEffectUpdated"] = EffectSample("timed-effect-update", ""","status":"pause","timeRemaining":12500"""),
        ["timedEffectEnded"] = EffectSample("timed-effect-update", ""","status":"end","timeRemaining":0"""),
        ["coinExchange"] = """
            {"domain":"prv","type":"coin-exchange","payload":{"amount":500,"bonus":50,"coinType":"paid","source":"stripe",
             "transactionID":"00000000-test-0000-0000-000000000001","anonymous":false,"timestamp":1793140000000,
             "exchanger":{"ccUID":"ccuid-test-viewer","name":"Cuebot Tester","profile":"twitch","originID":"12345","image":""},
             "userDisplay":{"name":"Cuebot Tester","safeName":"cuebot tester","badges":[]},
             "target":{"ccUID":"ccuid-test-streamer","name":"Streamer","profile":"twitch","originID":"67890","image":""}}}
            """,
        ["gameSessionStart"] = """
            {"domain":"pub","type":"game-session-start","payload":{"gameSessionID":"gs-test-0001","gamePackID":"MinecraftServer",
             "game":{"gameID":"Minecraft","name":{"public":"Minecraft","sort":"minecraft"}},"gamePack":{"gamePackID":"MinecraftServer",
             "name":"Minecraft (Server)","platform":"pc"},"profileType":"twitch","ccUID":"ccuid-test-streamer","createdAt":1793140000,"updatedAt":1793140000}}
            """,
        ["gameSessionEnd"] = """{"domain":"pub","type":"game-session-stop","payload":{"gameSessionID":"gs-test-0001"}}""",
    };

    private static string EffectSample(string type, string extra) => $$$"""
        {"domain":"prv","type":"{{{type}}}","payload":{"requestID":"00000000-test-0000-0000-000000000002","timestamp":1793140000000,
         "effect":{"effectID":"spawn_creeper","name":"Spawn Creeper","description":"Spawns a creeper behind the streamer","type":"game",
          "image":"","duration":30},
         "game":{"gameID":"Minecraft","name":"Minecraft"},"gamePack":{"gamePackID":"MinecraftServer","name":"Minecraft (Server)","platform":"pc"},
         "requester":{"ccUID":"ccuid-test-viewer","name":"Cuebot Tester","profile":"twitch","originID":"12345","image":""},
         "userDisplay":{"name":"Cuebot Tester","safeName":"cuebot tester","badges":[]},
         "target":{"ccUID":"ccuid-test-streamer","name":"Streamer","profile":"twitch","originID":"67890","image":""},
         "quantity":1,"price":150,"unitPrice":150,"message":"",
         "parameters":{"color":{"type":"options","title":"Color","value":"green","name":"Green"}}{{{extra}}}}}
        """;
}
