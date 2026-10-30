using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Widgets;

namespace Cuebot.Server.Overlays;

/// <summary>
/// The server side of StreamElements-compatible widgets: each layer's widget document (panes with field placeholders
/// filled in, plus jQuery and Cuebot's <c>se-widget.js</c> shim) and the answers to the <c>SE_API</c> requests widgets make
/// through their overlay page (<c>store_get</c>, <c>store_set</c>, <c>counter_get</c>, <c>sanitize</c>, <c>cheer_filter</c>).
/// </summary>
internal static partial class OverlayWidgetHost
{
    /// <summary>jQuery, which StreamElements preloads for widgets (loaded from jQuery's own CDN, as StreamElements uses its CDN).</summary>
    public const string JQueryUrl = "https://code.jquery.com/jquery-3.7.1.min.js";

    /// <summary>The page config entry for one layer (position, size and look; the document is fetched separately).</summary>
    public static JsonObject LayerConfig(OverlayLayer layer) => new()
    {
        ["id"] = layer.Id.ToString("D"),
        ["name"] = layer.Name,
        ["type"] = layer.Type,
        ["x"] = layer.X,
        ["y"] = layer.Y,
        ["width"] = layer.Width,
        ["height"] = layer.Height,
        ["opacity"] = layer.Opacity,
        ["rotation"] = layer.Rotation,
        ["visible"] = layer.Visible,
    };

    /// <summary><c>{fieldData, widgetDuration}</c>: the widget's settings and its queue hold in seconds.</summary>
    public static JsonObject Settings(WidgetCode code)
    {
        var fieldData = code.FieldData();
        return new JsonObject { ["fieldData"] = fieldData, ["widgetDuration"] = WidgetCode.WidgetDuration(fieldData) };
    }

    /// <summary>
    /// The key that opens one layer's widget document on the widget port: HMAC-SHA256 of the layer id with the overlay's
    /// token, so it cannot be turned back into the token and opens nothing else. Changes when the overlay gets a new link.
    /// </summary>
    public static string FrameKey(string overlayToken, Guid layerId)
    {
        var mac = System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes(overlayToken),
            System.Text.Encoding.UTF8.GetBytes(layerId.ToString("D")));
        return Convert.ToBase64String(mac, 0, 24).Replace('+', '-').Replace('/', '_');
    }

    /// <summary>The widget document: panes with field placeholders filled in, after jQuery and Cuebot's SE_API shim.</summary>
    public static string Document(WidgetCode code)
    {
        var fieldData = code.FieldData();
        return $"""
            <!doctype html>
            <html>
            <head>
            <meta charset="utf-8">
            <meta name="referrer" content="no-referrer">
            <script src="{JQueryUrl}" crossorigin="anonymous"></script>
            <script src="/runtime/se-widget.js"></script>
            <style>
            html, body {"{"} margin: 0; padding: 0; background: transparent; overflow: hidden; {"}"}
            </style>
            <style>
            {WidgetFields.Substitute(code.Css, fieldData)}
            </style>
            </head>
            <body>
            {WidgetFields.Substitute(code.Html, fieldData)}
            <script>
            {WidgetFields.Substitute(code.Js, fieldData)}
            </script>
            </body>
            </html>
            """;
    }

    /// <summary>
    /// Answers one widget request; returns the result, or throws <see cref="ArgumentException"/> for an unknown or
    /// malformed request (the page passes the message back to the widget as a rejected promise).
    /// </summary>
    public static JsonNode? Answer(string request, JsonObject? data, ServerServices services)
    {
        switch (request)
        {
            case "store_get":
                return services.WidgetStore?.Get(Key(data));
            case "store_set":
                if (services.WidgetStore?.Set(Key(data), data?["value"]) != true) throw new ArgumentException("The value could not be stored");
                return null;
            case "counter_get":
            {
                // StreamElements' bot counters are Cuebot's global variables; only numeric ones, so widgets cannot read
                // other things kept in globals.
                var name = data?["name"]?.GetValue<string>() ?? data?["counter"]?.GetValue<string>() ?? "";
                var value = services.Globals.GetValue(name);
                var number = value is long or int or decimal or double ? Convert.ToDouble(value, CultureInfo.InvariantCulture) : 0;
                return new JsonObject { ["counter"] = name, ["value"] = number };
            }
            case "sanitize":
                // No word filter is applied yet; the message comes back as it is.
                return new JsonObject { ["skip"] = false, ["result"] = new JsonObject { ["message"] = data?["message"]?.DeepClone() ?? "" } };
            case "cheer_filter":
                return CheerFilter(data?["message"]?.GetValue<string>() ?? "");
            default:
                throw new ArgumentException($"Unknown widget request '{request}'");
        }
    }

    private static string Key(JsonObject? data) =>
        data?["key"] is JsonValue key && key.GetValueKind() == JsonValueKind.String ? key.GetValue<string>() : throw new ArgumentException("A key is needed");

    /// <summary>The message without Twitch cheermotes ("Cheer100", "Kappa50", ...), spaces tidied.</summary>
    public static string CheerFilter(string message) =>
        Spaces().Replace(Cheermote().Replace(message, ""), " ").Trim();

    [GeneratedRegex(@"(?i)\b(cheer|biblethump|cheerwhal|corgo|uni|showlove|party|seemsgood|pride|kappa|frankerz|heyguys|dansgame|elegiggle|trihard|kreygasm|4head|swiftrage|notlikethis|failfish|vohiyo|pjsalt|mrdestructoid|bday|ripcheer|shamrock|streamlabs|doodlecheer|bitboss|anon)\d+\b")]
    private static partial Regex Cheermote();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();
}
