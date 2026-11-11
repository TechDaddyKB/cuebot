using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cuebot.StreamApps.Common;
using Cuebot.StreamApps.Obsbot;

namespace Cuebot.StreamApps.Vnyan;

/// <summary>How the plain-text values of a "Send to VNyan" step are sent.</summary>
public enum VnyanNumbers
{
    /// <summary>Every name=value value is a JSON string, as VNyan's docs describe the payload ("key/value pairs of strings").</summary>
    Text,

    /// <summary>Values written as plain numbers (100, -2, 2.5) are JSON numbers, like the docs' example <c>"key2":2</c>.</summary>
    Numbers,
}

/// <summary>
/// VNyan's message formats as pure functions (unit-tested byte for byte): the API Message payload, WebSocket command
/// text, OSC parameter messages, and what VNyan's "Websocket Message" node sends to Cuebot.
/// </summary>
public static partial class VnyanProtocol
{
    /// <summary>VNyan stores OSC values sent here in its numeric parameters ([name]).</summary>
    public const string FloatParamAddress = "/VNyan/Param/Float";

    /// <summary>VNyan stores OSC values sent here in its text parameters (&lt;name&gt;).</summary>
    public const string StringParamAddress = "/VNyan/Param/String";

    /// <summary>VRChat-style avatar parameters, which VNyan also accepts on its OSC port.</summary>
    public const string AvatarParametersPrefix = "/avatar/parameters/";

    /// <summary>The path of VNyan's WebSocket receiver.</summary>
    public const string WebSocketPath = "/vnyan";

    /// <summary>Argument names a "VNyan message" trigger always sets; JSON keys with these names are not copied over them.</summary>
    public static readonly IReadOnlySet<string> MessageArguments =
        new HashSet<string>(["message", "vnyanCommand", "vnyanValue", "vnyanJson"], StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex PlainNumberPattern();

    /// <summary>
    /// True for text that is a number written the plain way (<c>100</c>, <c>-2</c>, <c>2.5</c>): no thousands
    /// separators, no comma decimals, no leading zeros (so codes such as <c>007</c> stay text), no exponent.
    /// </summary>
    public static bool IsPlainNumber(string text) => PlainNumberPattern().IsMatch(text);

    /// <summary>The API Message payload: JSON values keep their type; text values become numbers only in <see cref="VnyanNumbers.Numbers"/> mode.</summary>
    public static JsonObject Payload(IEnumerable<(string Name, JsonNode? Value)> pairs, VnyanNumbers numbers)
    {
        var payload = new JsonObject();
        foreach (var (name, value) in pairs)
            payload[name] = numbers == VnyanNumbers.Numbers && value is JsonValue v && v.GetValueKind() == JsonValueKind.String
                                                     && v.GetValue<string>().Trim() is var s && IsPlainNumber(s)
                ? JsonNode.Parse(s)
                : value?.DeepClone();
        return payload;
    }

    /// <summary>A payload from text pairs (the card's test button, older callers).</summary>
    public static JsonObject Payload(IReadOnlyDictionary<string, string> pairs, VnyanNumbers numbers = VnyanNumbers.Text) =>
        Payload(pairs.Select(p => (p.Key, (JsonNode?)JsonValue.Create(p.Value))), numbers);

    /// <summary>The POST body: <c>{"action":"&lt;name&gt;","payload":{..}}</c>.</summary>
    public static string Body(string action, JsonObject payload) =>
        new JsonObject { ["action"] = action, ["payload"] = payload.DeepClone() }.ToJsonString();

    /// <summary>"Text" (default) or "Numbers" from a step's choice; also accepts "As text"/"As numbers".</summary>
    public static VnyanNumbers ParseNumbers(string value) => value.Trim().ToLowerInvariant().Replace("as ", "") switch
    {
        "" or "text" => VnyanNumbers.Text,
        "numbers" or "number" => VnyanNumbers.Numbers,
        _ => throw new ArgumentException($"Numbers must be \"As text\" or \"As numbers\" (got '{value}')."),
    };

    /// <summary>
    /// The text for VNyan's WebSocket node: the command, then the number VNyan reads as the node's Value. VNyan's Value
    /// output is a whole number, so a value with decimals is still sent but VNyan may round or drop it.
    /// </summary>
    public static string Command(string command, string value)
    {
        var text = Steps.Require(command, "Command");
        var v = value.Trim();
        if (v.Length == 0) return text;
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number))
            throw new ArgumentException($"Value must be a number (got '{value}'). VNyan reads the number after the command as the node's Value; put text into the command instead.");
        return $"{text} {(IsPlainNumber(v) ? v : number.ToString("R", CultureInfo.InvariantCulture))}";
    }

    /// <summary>A number for an OSC float: invariant text (2.5, not 2,5), or true/false as 1/0.</summary>
    public static float ParseFloat(string value, string label = "Value")
    {
        var v = value.Trim();
        if (bool.TryParse(v, out var b)) return b ? 1f : 0f;
        if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || !double.IsFinite(d)
            || Math.Abs(d) > float.MaxValue)
            throw new ArgumentException($"{label} must be a number such as 5 or 2.5 (got '{value}').");
        return (float)d;
    }

    /// <summary>An OSC message that sets a VNyan parameter: a number ([name]) or text (&lt;name&gt;).</summary>
    public static OscMessage Parameter(string name, string value, bool number)
    {
        var n = Steps.Require(name, "Parameter");
        return number ? new OscMessage(FloatParamAddress, n, ParseFloat(value)) : new OscMessage(StringParamAddress, n, value);
    }

    /// <summary>A VRChat-style avatar parameter (a float), which VNyan accepts on its OSC port too.</summary>
    public static OscMessage AvatarParameter(string name, string value)
    {
        var n = Steps.Require(name, "Parameter").TrimStart('/');
        return new OscMessage(AvatarParametersPrefix + n, ParseFloat(value));
    }

    /// <summary>
    /// The arguments of a "VNyan message" trigger. JSON objects: <c>vnyanCommand</c> from the "command", "event",
    /// "action" or "name" key, <c>vnyanValue</c> from "value", and every key as an argument of its own. Other text is
    /// split like VNyan's own WebSocket node: the first word is the command, the rest the value (a number when it is
    /// one, otherwise text).
    /// </summary>
    public static Dictionary<string, object?> ParseIncoming(string text)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["message"] = text };
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') && JsonUtil.TryParse(trimmed) is { ValueKind: JsonValueKind.Object } json)
        {
            args["vnyanJson"] = true;
            foreach (var p in json.EnumerateObject())
                if (!MessageArguments.Contains(p.Name)) args[p.Name] = JsonUtil.ToArg(p.Value);
            args["vnyanCommand"] = new[] { "command", "event", "action", "name" }
                .Select(k => json.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null)
                .FirstOrDefault(v => v is not null) ?? "";
            args["vnyanValue"] = json.TryGetProperty("value", out var value) ? JsonUtil.ToArg(value) : "";
            return args;
        }
        args["vnyanJson"] = false;
        var space = trimmed.IndexOfAny([' ', '\t', '\r', '\n']);
        args["vnyanCommand"] = space < 0 ? trimmed : trimmed[..space];
        var rest = space < 0 ? "" : trimmed[(space + 1)..].Trim();
        args["vnyanValue"] = Number(rest) ?? (object)rest;
        return args;
    }

    /// <summary>A plain number as a long (whole) or double, or null.</summary>
    public static object? Number(string text)
    {
        if (!IsPlainNumber(text)) return null;
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return l;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
    }
}
