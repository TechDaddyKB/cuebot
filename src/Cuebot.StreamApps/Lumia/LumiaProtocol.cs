using System.Globalization;
using System.Text.Json;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Lumia;

/// <summary>A light connected to Lumia (from <c>/api/retrieve</c> <c>data.lights</c>).</summary>
public sealed record LumiaLight(string Id, string Name, string Type)
{
    public string Display => Name.Length > 0 ? Name : $"{Type}:{Id}";
}

/// <summary>What the user's Lumia offers: command, alert, studio and voice names plus lights.</summary>
public sealed record LumiaInfo(
    IReadOnlyDictionary<string, IReadOnlyList<string>> Options,
    IReadOnlyList<LumiaLight> Lights,
    IReadOnlyList<string> Voices,
    bool? Premium)
{
    public static LumiaInfo Empty { get; } = new(new Dictionary<string, IReadOnlyList<string>>(), [], [], null);

    public IReadOnlyList<string> Values(string type) => Options.TryGetValue(type, out var v) ? v : [];
}

/// <summary>
/// Lumia Stream's Developer API (dev.lumiastream.com, github.com/lumiastream/Developer-Docs): local REST on
/// http://host:39231 (<c>POST /api/send?token=</c> with <c>{"type":...,"params":{...}}</c>, <c>GET /api/retrieve?token=</c>)
/// and a websocket on ws://host:39231/api?token= that pushes every event as <c>{origin, type, event?, data}</c>.
/// </summary>
internal static class LumiaProtocol
{
    public static readonly string[] CommandTypes = ["chat-command", "chatbot-command", "twitch-points", "twitch-extension", "kick-points"];

    /// <summary>
    /// Chat bot platforms. The REST page lists twitch, youtube and facebook; Lumia's own lumia-types (which calls that
    /// page stale) adds kick and discord, and an omitted platform sends to every connected chat bot ("all" here).
    /// </summary>
    public static readonly string[] ChatbotPlatforms = ["twitch", "youtube", "facebook", "kick", "discord", AllPlatforms];

    public const string AllPlatforms = "all";

    public static string Send(string type, object? parameters = null) =>
        parameters is null ? JsonUtil.Serialize(new { type }) : JsonUtil.Serialize(new { type, @params = parameters });

    /// <summary>
    /// Command and alert params. <paramref name="skipQueue"/> is Lumia's <c>skipQueue</c> envelope param (lumia-types
    /// and the official SDKs; not on the Developer-Docs pages): run now instead of waiting in the activity queue.
    /// </summary>
    public static Dictionary<string, object?> CommandParams(string command, bool hold, IReadOnlyDictionary<string, object?>? extraSettings, bool skipQueue = false)
    {
        var p = new Dictionary<string, object?> { ["value"] = command };
        if (hold) p["hold"] = true;
        if (skipQueue) p["skipQueue"] = true;
        if (extraSettings is { Count: > 0 }) p["extraSettings"] = extraSettings;
        return p;
    }

    /// <summary>
    /// Extra settings from a sub-action: a JSON object keeps its value types (the docs send numbers such as
    /// <c>"bits": 1000</c>); name=value lines are text except true/false, which become booleans because Lumia reads
    /// flags by truthiness (the text "false" would count as true).
    /// </summary>
    public static Dictionary<string, object?> ParseExtraSettings(string text)
    {
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{'))
        {
            if (JsonUtil.TryParse(trimmed) is not { ValueKind: JsonValueKind.Object } obj) throw new ArgumentException("Extra settings must be a JSON object.");
            foreach (var p in obj.EnumerateObject()) result[p.Name] = p.Value.Clone();
            return result;
        }
        foreach (var (name, value) in Steps.ParsePairs(text))
            result[name] = bool.TryParse(value, out var flag) ? flag : value;
        return result;
    }

    /// <summary>A JSON object's members with their JSON types (script arguments).</summary>
    public static Dictionary<string, object?>? JsonObjectValues(JsonElement? element)
    {
        if (element is not { ValueKind: JsonValueKind.Object } obj) return null;
        var result = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in obj.EnumerateObject()) result[p.Name] = p.Value.Clone();
        return result;
    }

    /// <summary>Lumia's colour request: <c>hex-color</c> for #RRGGBB (an alpha prefix #AARRGGBB is dropped) or <c>rgb-color</c> for "r,g,b".</summary>
    public static (string Type, object Value) Color(string text)
    {
        var t = text.Trim();
        var parts = t.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 3)
        {
            var rgb = parts.Select(p => int.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n is >= 0 and <= 255
                ? n : throw new ArgumentException($"'{text}' is not a colour (use #RRGGBB or r,g,b with 0-255).")).ToArray();
            return ("rgb-color", new { r = rgb[0], g = rgb[1], b = rgb[2] });
        }
        var hex = t.TrimStart('#');
        if (hex.Length == 8) hex = hex[2..];
        if (hex.Length == 3) hex = string.Concat(hex.Select(c => $"{c}{c}"));
        if (hex.Length != 6 || !int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
            throw new ArgumentException($"'{text}' is not a colour (use #RRGGBB or r,g,b).");
        return ("hex-color", "#" + hex.ToUpperInvariant());
    }

    public static Dictionary<string, object?> ColorParams(object value, double? brightness, double? transition, double? duration, bool hold,
        IReadOnlyList<LumiaLight>? lights, bool skipQueue = false)
    {
        var p = new Dictionary<string, object?> { ["value"] = value };
        if (brightness is { } b) p["brightness"] = Math.Clamp(b, 0, 100);
        if (transition is { } t) p["transition"] = Math.Max(0, t);
        if (duration is { } d) p["duration"] = Math.Max(0, d);
        if (hold) p["hold"] = true;
        if (skipQueue) p["skipQueue"] = true;
        if (lights is { Count: > 0 }) p["lights"] = lights.Select(l => new { type = l.Type, value = l.Id }).ToArray();
        return p;
    }

    /// <summary>Parses the <c>data</c> of <c>/api/retrieve</c>.</summary>
    public static LumiaInfo ParseInfo(JsonElement data)
    {
        if (data.ValueKind != JsonValueKind.Object) return LumiaInfo.Empty;
        var options = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        var voices = new List<string>();
        if (data.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Object)
        {
            foreach (var option in opts.EnumerateObject())
            {
                if (option.Value.ValueKind != JsonValueKind.Object || !option.Value.TryGetProperty("values", out var values)) continue;
                if (values.ValueKind == JsonValueKind.Array)
                    options[option.Name] = values.EnumerateArray().Select(Name).Where(n => n.Length > 0).ToArray();
                else if (option.Name == "tts" && values.ValueKind == JsonValueKind.Object && values.TryGetProperty("voices", out var v) && v.ValueKind == JsonValueKind.Array)
                    voices.AddRange(v.EnumerateArray().Select(e => e.Str("id")).Where(n => n.Length > 0));
            }
        }
        var lights = data.TryGetProperty("lights", out var l) && l.ValueKind == JsonValueKind.Array
            ? l.EnumerateArray().Select(e => new LumiaLight(e.Str("id"), e.Str("name"), e.Str("type"))).Where(x => x.Id.Length > 0).ToArray()
            : [];
        return new LumiaInfo(options, lights, voices, data.Bool("premium"));
    }

    /// <summary>
    /// <c>data.states</c> (on, streamMode, fuze as 1/0) when a retrieve reply has it: the websocket retrieve documents
    /// it; the REST example does not show it, so it may be absent.
    /// </summary>
    public static (bool On, bool StreamMode, bool Fuze)? ParseStates(JsonElement data) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty("states", out var s) && s.ValueKind == JsonValueKind.Object
            ? (s.Bool("on") ?? false, s.Bool("streamMode") ?? false, s.Bool("fuze") ?? false)
            : null;

    private static string Name(JsonElement e) => e.ValueKind == JsonValueKind.String ? e.GetString() ?? "" : e.Str("name");

    /// <summary>Lights given as comma-separated names, ids or "type:id".</summary>
    public static IReadOnlyList<LumiaLight> ResolveLights(string text, IReadOnlyList<LumiaLight> known)
    {
        var result = new List<LumiaLight>();
        foreach (var entry in text.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = known.FirstOrDefault(l => string.Equals(l.Name, entry, StringComparison.OrdinalIgnoreCase))
                        ?? known.FirstOrDefault(l => string.Equals($"{l.Type}:{l.Id}", entry, StringComparison.OrdinalIgnoreCase))
                        ?? known.FirstOrDefault(l => string.Equals(l.Id, entry, StringComparison.OrdinalIgnoreCase));
            if (match is null && entry.IndexOf(':') is var colon and > 0)
                match = new LumiaLight(entry[(colon + 1)..].Trim(), "", entry[..colon].Trim());
            result.Add(match ?? throw new StreamAppRequestException($"Lumia Stream has no light '{entry}' (use its name, or brand:id such as hue:19)."));
        }
        return result;
    }

    /// <summary>A Lumia error body (<c>{"status":404,"code":"not_found","message":"..."}</c>, or plain text on older versions).</summary>
    public static string ErrorText(string body, int status)
    {
        if (JsonUtil.TryParse(body) is { ValueKind: JsonValueKind.Object } json)
        {
            var message = json.Str("message") is { Length: > 0 } m ? m : json.Str("error");
            if (message.Length > 0) return json.Str("code") is { Length: > 0 } code ? $"{message} ({code})" : message;
        }
        var text = body.Trim();
        return text.Length is > 0 and < 300 ? text : $"HTTP {status}";
    }

    // ---- Events ----

    /// <summary>The bot event type and arguments for one pushed websocket message, or null for socket replies.</summary>
    public static (string Type, Dictionary<string, object?> Args)? Event(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object || message.Str("event").StartsWith("socketapi:", StringComparison.Ordinal)) return null;
        var kind = message.Str("type");
        if (kind.Length == 0) return null;
        var data = message.TryGetProperty("data", out var d) ? d : default;
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["lumia.origin"] = message.Str("origin"),
            ["lumia.subOrigin"] = message.Str("subOrigin"),
            ["lumia.type"] = kind,
        };
        var extra = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("extraSettings", out var x) ? x : default;
        // Some alerts carry extraSettings keys such as "type" (Twitch power-ups, Kick kicks): those must not replace
        // lumia.type/origin/subOrigin, so they go under lumia.extraSettings.<key> instead.
        Flatten(args, "lumia.", extra, ReservedKeys);
        AddUser(args, extra);
        switch (kind)
        {
            case "chat":
                if (data.Str("shouldRemove") is { Length: > 0 } removed)
                {
                    args["messageId"] = removed;
                    return (LumiaEvents.ChatMessageDeleted, args);
                }
                args["message"] = data.Str("message");
                args["messageId"] = extra.Str("messageId") is { Length: > 0 } mid ? mid : data.Str("id");
                args["lumia.channel"] = data.Str("channel");
                return (LumiaEvents.ChatMessage, args);
            case "command":
                args["command"] = data.Str("command");
                if (data.Str("username") is { Length: > 0 } user)
                {
                    args["user"] = user;
                    args["userName"] = user;
                }
                return (LumiaEvents.Command, args);
            case "alert":
                args["lumia.alert"] = message.Str("event");
                args["lumia.replay"] = data.Bool("replay") ?? false;
                var dynamic = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("dynamic", out var dy) ? dy : default;
                Flatten(args, "lumia.dynamic.", dynamic);
                return (LumiaEvents.Alert, args);
            case "states":
                args["lumia.on"] = data.Bool("on") ?? false;
                args["lumia.streamMode"] = data.Bool("streamMode") ?? false;
                args["lumia.fuze"] = data.Bool("fuze") ?? false;
                return (LumiaEvents.StatesChanged, args);
            default:
                args["lumia.data"] = data.ValueKind == JsonValueKind.Undefined ? "" : data.GetRawText();
                return (LumiaEvents.Event, args);
        }
    }

    private static void AddUser(Dictionary<string, object?> args, JsonElement extra)
    {
        if (extra.Str("username") is not { Length: > 0 } user) return;
        args["user"] = user;
        args["userName"] = user;
        args["displayName"] = extra.Str("displayname") is { Length: > 0 } display ? display : user;
        args["userId"] = extra.Str("userId");
    }

    private static readonly HashSet<string> ReservedKeys = new(["origin", "subOrigin", "type"], StringComparer.OrdinalIgnoreCase);

    /// <summary>Scalars as prefix+key, nested objects one level deep as prefix+key.child; arrays stay JSON text.</summary>
    private static void Flatten(Dictionary<string, object?> args, string prefix, JsonElement obj, IReadOnlySet<string>? reserved = null)
    {
        if (obj.ValueKind != JsonValueKind.Object) return;
        foreach (var p in obj.EnumerateObject())
        {
            var name = reserved?.Contains(p.Name) == true ? "extraSettings." + p.Name : p.Name;
            if (p.Value.ValueKind == JsonValueKind.Object)
                foreach (var child in p.Value.EnumerateObject())
                    args[$"{prefix}{name}.{child.Name}"] = Cuebot.Core.Scripting.ScriptArgs.ToClr(child.Value);
            else
                args[prefix + name] = Cuebot.Core.Scripting.ScriptArgs.ToClr(p.Value);
        }
    }
}
