using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Remote;

/// <summary>Who is on the other end of a link, as announced in its hello.</summary>
public sealed record RemoteInstanceInfo(string Id, string Name, string Os, string Version);

/// <summary>A signal received from another instance.</summary>
/// <param name="Sender">Name of the action that sent it (empty from scripts without a run).</param>
/// <param name="Label">The link's local name: the instance's name in remote.json for outgoing links, else its announced name.</param>
public sealed record RemoteSignal(string Name, string Sender, IReadOnlyDictionary<string, object?> Args, RemoteInstanceInfo From, string Label);

/// <summary>One parsed wire message.</summary>
internal sealed record RemoteMessage(string Type, int Protocol, RemoteInstanceInfo? Instance, string Name, string Sender,
    IReadOnlyDictionary<string, object?> Args, string Message);

/// <summary>
/// The wire format between instances (see PROTOCOL.md): UTF-8 JSON text frames on <c>/api/v1/remote</c>. Pure functions,
/// unit tested without sockets.
/// </summary>
internal static class RemoteProtocol
{
    public const string RouteName = "remote";
    public const int Version = 1;

    public static string Hello(RemoteInstanceInfo self) => new JsonObject
    {
        ["type"] = "hello",
        ["protocol"] = Version,
        ["instance"] = new JsonObject { ["id"] = self.Id, ["name"] = self.Name, ["os"] = self.Os, ["version"] = self.Version },
    }.ToJsonString();

    public static string Signal(string name, string sender, IReadOnlyDictionary<string, object?> args)
    {
        var json = new JsonObject();
        foreach (var (key, value) in args)
        {
            if (key.Length > 0) json[key] = ToJson(value);
        }
        return new JsonObject { ["type"] = "signal", ["name"] = name, ["sender"] = sender, ["args"] = json }.ToJsonString();
    }

    public static string Error(string message) => new JsonObject { ["type"] = "error", ["message"] = message }.ToJsonString();

    /// <summary>Parses a frame; null when it is not a JSON object with a "type".</summary>
    public static RemoteMessage? TryParse(string text)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return null;
        }
        if (node is not JsonObject obj || Str(obj, "type") is not { Length: > 0 } type) return null;

        RemoteInstanceInfo? instance = null;
        if (obj["instance"] is JsonObject i && Str(i, "id") is { Length: > 0 } id)
            instance = new RemoteInstanceInfo(id, Str(i, "name") ?? "", Str(i, "os") ?? "", Str(i, "version") ?? "");

        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (obj["args"] is JsonObject a)
        {
            foreach (var (key, value) in a) args[key] = FromJson(value);
        }
        var protocol = obj["protocol"] is JsonValue p && p.TryGetValue<int>(out var v) ? v : 0;
        return new RemoteMessage(type, protocol, instance, Str(obj, "name") ?? "", Str(obj, "sender") ?? "", args, Str(obj, "message") ?? "");
    }

    /// <summary>
    /// The WebSocket URL for an instance: <c>ws://host:port/api/v1/remote</c>, or a given ws:// / wss:// / http(s):// URL
    /// (its path is kept when it has one). Throws <see cref="UriFormatException"/> for an unusable host.
    /// </summary>
    public static Uri BuildUri(string host, int port)
    {
        var text = host.Trim();
        if (text.Length == 0) throw new UriFormatException("No host");
        var path = Cuebot.Server.Api.ApiServer.Prefix + "/" + RouteName;
        if (text.Contains("://", StringComparison.Ordinal))
        {
            var given = new Uri(text);
            var scheme = given.Scheme.ToLowerInvariant() switch
            {
                "ws" or "http" => "ws",
                "wss" or "https" => "wss",
                _ => throw new UriFormatException($"Unsupported scheme '{given.Scheme}'"),
            };
            var builder = new UriBuilder(given) { Scheme = scheme, Port = given.IsDefaultPort ? DefaultPort(scheme) : given.Port };
            if (given.AbsolutePath is "" or "/") builder.Path = path;
            return builder.Uri;
        }
        if (port is < 1 or > 65535) throw new UriFormatException($"Invalid port {port}");
        return new UriBuilder("ws", text, port, path).Uri;
    }

    private static int DefaultPort(string scheme) => scheme == "wss" ? 443 : 80;

    /// <summary>A plain value for the wire: strings, numbers, bools and null as-is, anything else as text.</summary>
    internal static JsonNode? ToJson(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int or long or short or byte or sbyte or uint or ulong or ushort => JsonValue.Create(Convert.ToInt64(value, CultureInfo.InvariantCulture)),
        float or double or decimal => JsonValue.Create(Convert.ToDouble(value, CultureInfo.InvariantCulture)),
        DateTime d => JsonValue.Create(d.ToString("o", CultureInfo.InvariantCulture)),
        DateTimeOffset d => JsonValue.Create(d.ToString("o", CultureInfo.InvariantCulture)),
        JsonElement e => JsonNode.Parse(e.GetRawText()) is JsonValue jv ? jv : JsonValue.Create(e.GetRawText()),
        JsonNode n => n is JsonValue ? n.DeepClone() : JsonValue.Create(n.ToJsonString()),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    internal static object? FromJson(JsonNode? node)
    {
        if (node is not JsonValue value) return node?.ToJsonString();
        return value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => long.TryParse(value.ToJsonString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var l)
                ? (object)l
                : double.Parse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture),
            _ => null,
        };
    }

    private static string? Str(JsonObject obj, string key) =>
        obj[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    /// <summary>
    /// "key=value" lines (the sub-action's Arguments box) as a dictionary. Blank lines and lines without '=' or with an
    /// empty key are skipped; later keys win.
    /// </summary>
    public static Dictionary<string, object?> ParseArgumentLines(string text)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = raw.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0) continue;
            var key = raw[..eq].Trim();
            if (key.Length > 0) args[key] = raw[(eq + 1)..].Trim();
        }
        return args;
    }
}
