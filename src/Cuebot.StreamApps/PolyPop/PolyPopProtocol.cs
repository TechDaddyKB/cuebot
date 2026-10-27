using System.Text;
using System.Text.Json;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.PolyPop;

/// <summary>A message PolyPop's websocket plugin sent to Cuebot.</summary>
public sealed record PolyPopMessage(string Type, string Name, string Data, string SourceId, string Raw);

/// <summary>
/// The wire format of the community "WebSocket" plugin for PolyPop (github.com/Jabbey92/PolyPopWebsocketPlugin, MIT):
/// alerts go to PolyPop as <c>{"type":"ALERT","title":"&lt;alert name&gt;","variables":{"text":{"name":"value"}}}</c>; the
/// plugin raises the WebSocket Alert whose name equals <c>title</c> with those variables (wired as &lt;name&gt; in PolyPop).
/// Its own sends are <c>&lt;payload&gt;:&lt;plugin instance id&gt;[:&lt;data&gt;]</c>, where payload is JSON
/// (<c>{"type":"TRIGGER","name":...,"data":...}</c>) or a word such as <c>disconnect</c>.
/// </summary>
internal static class PolyPopProtocol
{
    public static string Alert(string title, IReadOnlyDictionary<string, string> variables) =>
        JsonUtil.Serialize(new { type = "ALERT", title, variables = new { text = variables } });

    public static PolyPopMessage Parse(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') && ReadObject(trimmed) is var (json, rest))
        {
            var (source, data) = SplitSuffix(rest);
            var type = json.Str("type");
            var payloadData = json.TryGetProperty("data", out var d) ? (d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : d.GetRawText()) : "";
            return new PolyPopMessage(type.Length > 0 ? type : "JSON", json.Str("name").Length > 0 ? json.Str("name") : json.Str("title"),
                payloadData.Length > 0 ? payloadData : data, source, text);
        }
        var parts = trimmed.Split(':', 3);
        return new PolyPopMessage(parts[0], "", parts.Length > 2 ? parts[2] : "", parts.Length > 1 ? parts[1] : "", text);
    }

    /// <summary>The leading JSON object and the text after it.</summary>
    private static (JsonElement Json, string Remainder)? ReadObject(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        try
        {
            var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { AllowTrailingCommas = true, AllowMultipleValues = true });
            using var document = JsonDocument.ParseValue(ref reader);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            var rest = Encoding.UTF8.GetString(bytes, (int)reader.BytesConsumed, bytes.Length - (int)reader.BytesConsumed);
            return (document.RootElement.Clone(), rest);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (string Source, string Data) SplitSuffix(string rest)
    {
        var r = rest.Trim();
        if (!r.StartsWith(':')) return ("", "");
        var parts = r[1..].Split(':', 2);
        return (parts[0], parts.Length > 1 ? parts[1] : "");
    }
}
