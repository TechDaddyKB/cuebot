using System.Text.Json;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Veadotube;

/// <summary>A node veadotube exposes to the API (veadotube mini: "avatar state" and "push-to-talk").</summary>
public sealed record VeadotubeNode(string Type, string Id, string Name)
{
    public string Label => Name.Length > 0 ? Name : Id;
}

/// <summary>An avatar state of a stateEvents node.</summary>
public sealed record VeadotubeState(string Id, string Name);

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
                .Select(x => new VeadotubeState(x.Str("id"), x.Str("name") is { Length: > 0 } n ? n : x.Str("id")))
                .Where(x => x.Id.Length > 0)]
            : [];
}
