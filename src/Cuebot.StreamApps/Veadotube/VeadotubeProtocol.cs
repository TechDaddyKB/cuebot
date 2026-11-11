using System.Text.Json;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Veadotube;

/// <summary>A node veadotube exposes to the API (veadotube mini: "avatar state" and "push-to-talk").</summary>
public sealed record VeadotubeNode(string Type, string Id, string Name)
{
    public string Label => Name.Length > 0 ? Name : Id;
}

/// <summary>An avatar state of a stateEvents node (<see cref="ThumbHash"/> changes when its picture does).</summary>
public sealed record VeadotubeState(string Id, string Name, string ThumbHash = "");

/// <summary>A number node's value and, when one was given, its range.</summary>
public sealed record VeadotubeNumber(double Value, double? Min, double? Max);

/// <summary>
/// One change for a node, written once and sent either over the WebSocket (as a payload object) or as a one-off GET
/// command (<c>?cmd0=nodes&amp;cmd1=&lt;type&gt;&amp;cmd2=&lt;id&gt;&amp;cmd3=&lt;event&gt;&amp;cmd4..</c>).
/// </summary>
public sealed record VeadotubeCommand(string Event, string? State = null, bool? Bool = null, double? Number = null, double? Min = null, double? Max = null)
{
    public object Payload() => Event switch
    {
        _ when State is not null => new { @event = Event, state = State },
        _ when Bool is not null => new { @event = Event, value = Bool.Value },
        _ when Number is not null && Min is not null && Max is not null => new { @event = Event, value = new { value = Number.Value, min = Min.Value, max = Max.Value } },
        _ when Number is not null => new { @event = Event, value = Number.Value },
        _ => (object)new { @event = Event },
    };

    public IReadOnlyList<string> Arguments()
    {
        var args = new List<string> { Event };
        if (State is not null) args.Add(State);
        if (Bool is not null) args.Add(Bool.Value ? "1" : "0");
        if (Number is not null)
        {
            args.Add(Invariant(Number.Value));
            if (Min is not null && Max is not null)
            {
                args.Add(Invariant(Min.Value));
                args.Add(Invariant(Max.Value));
            }
        }
        return args;
    }

    private static string Invariant(double value) => value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>A message veadotube sent on its nodes channel.</summary>
internal sealed record VeadotubeNodesMessage(string Event, IReadOnlyList<VeadotubeNode> Entries, VeadotubeNode? Node, JsonElement Payload);

/// <summary>
/// veadotube's WebSocket API (veado.tube/docs/tech/api): every message is <c>&lt;channel&gt;:&lt;json&gt;</c>. The
/// <c>nodes</c> channel lists the exposed nodes (<c>{"event":"list"}</c>) and talks to one of them with
/// <c>{"event":"payload","type":..,"id":..,"payload":{..}}</c>; "listen" payloads make veadotube send changes on its own.
/// </summary>
internal static class VeadotubeProtocol
{
    public const string NodesChannel = "nodes";
    public const string InstanceChannel = "instance";
    public const string StateEventsType = "stateEvents";
    public const string BooleanType = "boolean";
    public const string NumberType = "number";
    public const string ListenToken = "cuebot";

    public static string Nodes(object body) => NodesChannel + ":" + JsonUtil.Serialize(body);

    public static string ListNodes() => Nodes(new { @event = "list" });

    public static string ListenNodes() => Nodes(new { @event = "listen", token = ListenToken });

    public static string Payload(VeadotubeNode node, object payload) =>
        Nodes(new { @event = "payload", type = node.Type, id = node.Id, payload });

    public static string Listen(VeadotubeNode node) => Payload(node, new { @event = "listen", token = ListenToken });

    /// <summary>A stateEvents change: set, push, pop or toggle a state, or clear the stack (no state).</summary>
    public static string StateEvent(VeadotubeNode node, string @event, string? state) =>
        Payload(node, state is null ? new { @event } : new { @event, state });

    public static string StateList(VeadotubeNode node) => Payload(node, new { @event = "list" });

    public static string StatePeek(VeadotubeNode node) => Payload(node, new { @event = "peek" });

    public static string BooleanSet(VeadotubeNode node, bool value) => Payload(node, new { @event = "set", value });

    public static string BooleanToggle(VeadotubeNode node) => Payload(node, new { @event = "toggle" });

    public static string BooleanGet(VeadotubeNode node) => Payload(node, new { @event = "get" });

    public static string NumberChange(VeadotubeNode node, bool add, double value) => Payload(node, new { @event = add ? "add" : "set", value });

    public static string NumberGet(VeadotubeNode node) => Payload(node, new { @event = "get" });

    public static string Thumb(VeadotubeNode node, string state) => Payload(node, new { @event = "thumb", state });

    public static string Command(VeadotubeNode node, VeadotubeCommand command) => Payload(node, command.Payload());

    /// <summary>The GET address for a one-off command (veado.tube/docs/tech/api "using GET requests").</summary>
    public static Uri CommandUrl(string server, IEnumerable<string> arguments) =>
        new($"http://{server}/?" + string.Join("&", arguments.Select((a, i) => $"cmd{i}={Uri.EscapeDataString(a)}")));

    /// <summary>A number node's payload: {value} or {value, min, max}; null when it has no value yet.</summary>
    public static VeadotubeNumber? NumberValue(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.Number => new VeadotubeNumber(payload.GetDouble(), null, null),
        JsonValueKind.Object when payload.Num("value") is { } v => new VeadotubeNumber(v, payload.Num("min"), payload.Num("max")),
        _ => null,
    };

    /// <summary>The GET answer of a number "get": "3" or "3 0 10".</summary>
    public static VeadotubeNumber? NumberText(string text)
    {
        var parts = text.Split([' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(p => double.TryParse(p, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : (double?)null)
            .ToArray();
        return parts.Length switch
        {
            >= 3 when parts[0] is { } v && parts[1] is { } min && parts[2] is { } max => new VeadotubeNumber(v, min, max),
            >= 1 when parts[0] is { } v => new VeadotubeNumber(v, null, null),
            _ => null,
        };
    }

    /// <summary>The instance channel's info message (sent right after connecting).</summary>
    public static (string Name, string Id, string Version)? Info(string body) =>
        JsonUtil.TryParse(body) is { ValueKind: JsonValueKind.Object } root && root.Str("event") == "info"
            ? (root.Str("name"), root.Str("id"), root.Str("version"))
            : null;

    /// <summary>The channel and its body; null for text without a channel prefix.</summary>
    public static (string Channel, string Body)? Split(string text)
    {
        var colon = text.IndexOf(':');
        return colon <= 0 ? null : (text[..colon].Trim(), text[(colon + 1)..]);
    }

    public static VeadotubeNodesMessage? ParseNodes(string body)
    {
        if (JsonUtil.TryParse(body) is not { ValueKind: JsonValueKind.Object } root) return null;
        var entries = root.TryGetProperty("entries", out var e) && e.ValueKind == JsonValueKind.Array
            ? e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                .Select(x => new VeadotubeNode(x.Str("type"), x.Str("id"), x.Str("name"))).Where(n => n.Type.Length > 0).ToArray()
            : [];
        var node = root.Str("type") is { Length: > 0 } type ? new VeadotubeNode(type, root.Str("id"), root.Str("name")) : null;
        var payload = root.TryGetProperty("payload", out var p) ? p.Clone() : default;
        return new VeadotubeNodesMessage(root.Str("event"), entries, node, payload);
    }

    /// <summary>A boolean node's value: true/false, 1/0, or null for an empty object (no value set).</summary>
    public static bool? BooleanValue(JsonElement payload) => payload.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => payload.GetDouble() != 0,
        _ => null,
    };

    public static IReadOnlyList<VeadotubeState> States(JsonElement payload) =>
        payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("states", out var s) && s.ValueKind == JsonValueKind.Array
            ? [.. s.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object)
                .Select(x => new VeadotubeState(x.Str("id"), x.Str("name") is { Length: > 0 } n ? n : x.Str("id"), x.Str("thumbHash")))
                .Where(x => x.Id.Length > 0)]
            : [];
}
