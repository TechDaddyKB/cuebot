using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.StreamDeck;

/// <summary>A key appearance change pushed to the plugin (Set Title, Set State, ...).</summary>
/// <param name="Command">One of <see cref="StreamDeckProtocol.Commands"/>.</param>
/// <param name="State">For setState: the state (0/1). For setTitle/setImage: the state to change, or null for the current one.</param>
/// <param name="Indicator">For setValue on a dial: the indicator bar (0-100), or null to leave it.</param>
public sealed record StreamDeckCommand(
    string Command,
    string ButtonId,
    string? Title = null,
    int? State = null,
    string? Image = null,
    string? Value = null,
    double? Indicator = null);

/// <summary>
/// Cuebot's Stream Deck plugin protocol (version 1): JSON text messages over <c>ws://host:port/api/v1/streamdeck</c>.
/// Pure functions only, so the format is unit-tested without sockets. The full description is in PROTOCOL.md.
/// </summary>
public static class StreamDeckProtocol
{
    public const int Version = 1;
    public const string RouteName = "streamdeck";

    /// <summary>Appearance commands Cuebot sends; the plugin applies them to every key with the button id.</summary>
    public static readonly string[] Commands = ["setTitle", "setState", "setImage", "setValue", "showAlert", "showOk", "toggleState"];

    /// <summary>Input events the plugin reports (Stream Deck SDK event names).</summary>
    public static readonly string[] InputEvents = ["keyDown", "keyUp", "dialDown", "dialUp", "dialRotate", "touchTap"];

    public static JsonElement? TryParse(string text)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object ? document.RootElement.Clone() : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Welcome(string sessionId, string appVersion) => new JsonObject
    {
        ["type"] = "welcome",
        ["protocol"] = Version,
        ["session"] = sessionId,
        ["app"] = new JsonObject { ["name"] = "Cuebot", ["version"] = appVersion },
    }.ToJsonString();

    public static string Response(long id, JsonNode? result) =>
        new JsonObject { ["type"] = "response", ["id"] = id, ["ok"] = true, ["result"] = result }.ToJsonString();

    public static string Error(long id, string message) =>
        new JsonObject { ["type"] = "response", ["id"] = id, ["ok"] = false, ["error"] = message }.ToJsonString();

    public static string Command(StreamDeckCommand c)
    {
        var o = new JsonObject { ["type"] = "command", ["command"] = c.Command, ["buttonId"] = c.ButtonId };
        if (c.Title is not null) o["title"] = c.Title;
        if (c.State is not null) o["state"] = c.State;
        if (c.Image is not null) o["image"] = c.Image;
        if (c.Value is not null) o["value"] = c.Value;
        if (c.Indicator is not null) o["indicator"] = c.Indicator;
        return o.ToJsonString();
    }

    public static string ActionsChanged(JsonArray actions) =>
        new JsonObject { ["type"] = "actionsChanged", ["actions"] = actions }.ToJsonString();

    public static string GlobalChanged(string name, string? value) =>
        new JsonObject { ["type"] = "globalChanged", ["name"] = name, ["value"] = value }.ToJsonString();

    // ---- Parsing ---------------------------------------------------------------------------------------------------

    public static string Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.String => v.GetString() ?? "", JsonValueKind.Null or JsonValueKind.Undefined => "", _ => v.ToString() }
            : "";

    public static int? Int(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.Number => v.TryGetInt32(out var i) ? i : (int)Math.Round(v.GetDouble()),
                JsonValueKind.String => int.TryParse(v.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : null,
                _ => null,
            }
            : null;

    public static bool? Bool(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null }
            : null;

    public static JsonElement? Obj(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    public static StreamDeckDevice ParseDevice(JsonElement d)
    {
        var size = d.Obj("size");
        return new StreamDeckDevice(
            d.Str("id"),
            d.Str("name") is { Length: > 0 } name ? name : "Stream Deck",
            d.Int("type") ?? 0,
            size?.Int("columns") ?? 0,
            size?.Int("rows") ?? 0,
            d.Bool("connected") ?? true);
    }

    public static IReadOnlyList<StreamDeckDevice> ParseDevices(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty("devices", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.Object).Select(ParseDevice).Where(d => d.Id.Length > 0)]
            : [];

    /// <summary>A key/dial description; null when it has no context.</summary>
    public static StreamDeckButton? ParseButton(JsonElement b)
    {
        if (b.ValueKind != JsonValueKind.Object) return null;
        var context = b.Str("context");
        if (context.Length == 0) return null;
        return new StreamDeckButton(
            context,
            b.Str("buttonId").Trim(),
            b.Str("kind") is { Length: > 0 } kind ? kind : StreamDeckActionKinds.Trigger,
            b.Str("controller") is { Length: > 0 } controller ? controller : "Keypad",
            b.Str("device"),
            b.Int("row"),
            b.Int("column"),
            b.Int("state") ?? 0,
            b.Bool("isInMultiAction") ?? false);
    }

    public static IReadOnlyList<StreamDeckButton> ParseButtons(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty("buttons", out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(ParseButton).OfType<StreamDeckButton>()]
            : [];

    /// <summary>
    /// Trigger arguments for an input message: the button and device it came from, the event details and the key's own
    /// arguments (from its settings; they never replace the built-in names).
    /// </summary>
    public static Dictionary<string, object?> InputArgs(JsonElement message, StreamDeckButton? button, StreamDeckDevice? device, string instanceId)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (message.Obj("args") is { } custom)
        {
            foreach (var p in custom.EnumerateObject()) args[p.Name] = ToArg(p.Value);
        }

        var buttonId = message.Str("buttonId").Trim();
        if (buttonId.Length == 0) buttonId = button?.ButtonId ?? "";
        var controller = button?.Controller ?? (message.Str("controller") is { Length: > 0 } c ? c : "Keypad");
        args["buttonId"] = buttonId;
        args["buttonEvent"] = message.Str("event");
        args["controller"] = controller;
        args["isDial"] = string.Equals(controller, "Encoder", StringComparison.OrdinalIgnoreCase);
        args["context"] = message.Str("context");
        args["deviceId"] = device?.Id ?? (button?.DeviceId is { Length: > 0 } d ? d : message.Str("device"));
        args["deviceName"] = device?.Name ?? "";
        args["deviceType"] = device?.TypeName ?? "";
        args["row"] = button?.Row;
        args["column"] = button?.Column;
        args["state"] = message.Int("state") ?? button?.State ?? 0;
        args["instanceId"] = instanceId;
        if (message.Int("ticks") is { } ticks) args["ticks"] = ticks;
        if (message.Bool("pressed") is { } pressed) args["pressed"] = pressed;
        if (message.Bool("hold") is { } hold) args["hold"] = hold;
        if (message.TryGetProperty("tapPos", out var tap) && tap.ValueKind == JsonValueKind.Array && tap.GetArrayLength() == 2)
        {
            args["tapX"] = tap[0].ValueKind == JsonValueKind.Number ? tap[0].GetInt32() : 0;
            args["tapY"] = tap[1].ValueKind == JsonValueKind.Number ? tap[1].GetInt32() : 0;
        }
        return args;
    }

    /// <summary>A JSON value as a trigger argument (objects and arrays stay JSON text).</summary>
    public static object? ToArg(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => v.TryGetInt64(out var l) ? (object)l : v.GetDouble(),
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => v.GetRawText(),
    };

    /// <summary>Image text for setImage: data URIs and http(s) URLs pass through; an existing file becomes a data URI.</summary>
    /// <exception cref="ArgumentException">Not a URL or existing file, an unsupported type, or a file over <paramref name="maxBytes"/>.</exception>
    public static string ResolveImage(string image, int maxBytes = 2 * 1024 * 1024)
    {
        var text = image.Trim().Trim('"');
        if (text.Length == 0) return "";
        if (text.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) return text;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") return text;
        var path = uri is { IsFile: true } ? uri.LocalPath : text;
        if (!File.Exists(path)) throw new ArgumentException($"Image '{text}' is not a file, an http(s) URL or a data: URI.");
        var mime = Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".bmp" => "image/bmp",
            var other => throw new ArgumentException($"Unsupported image type '{other}' (use PNG, JPEG, GIF, SVG, WebP or BMP)."),
        };
        var length = new FileInfo(path).Length;
        if (length > maxBytes) throw new ArgumentException($"Image '{Path.GetFileName(path)}' is {length / 1024} KB; the limit is {maxBytes / 1024} KB.");
        return $"data:{mime};base64,{Convert.ToBase64String(File.ReadAllBytes(path))}";
    }
}
