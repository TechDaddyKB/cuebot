using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Cuebot.Integrations.Common;

namespace Cuebot.Integrations.StreamLoots;

/// <summary>Bot event types of the StreamLoots integration.</summary>
public static class StreamLootsEvents
{
    public const string Prefix = "streamloots";
    public const string Connected = "streamloots.connected";
    public const string Disconnected = "streamloots.disconnected";

    /// <summary>A viewer bought chests (packs) for themselves. Args: user, quantity, message, ...</summary>
    public const string PackPurchased = "streamloots.packPurchased";

    /// <summary>A viewer gifted chests to another viewer (giftee) or to random viewers (communityGift = true).</summary>
    public const string PackGifted = "streamloots.packGifted";

    /// <summary>A viewer redeemed a card. Args: user, cardName, cardRarity, message, ...</summary>
    public const string CardRedeemed = "streamloots.cardRedeemed";

    /// <summary>Any other alert type StreamLoots sends (e.g. reactions); args carry the raw type and fields.</summary>
    public const string OtherAlert = "streamloots.alert";
}

/// <summary>A mapped StreamLoots alert: the bot event (without the "streamloots." prefix) and its args.</summary>
internal sealed record StreamLootsAlert(string Name, Dictionary<string, object?> Args);

/// <summary>
/// The StreamLoots alerts event stream (see PROTOCOL.md): <c>GET https://widgets.streamloots.com/alerts/&lt;alert id&gt;/media-stream</c>
/// with <c>Accept: text/event-stream</c>; each "message" event is one alert as JSON.
/// </summary>
internal static partial class StreamLootsProtocol
{
    public static readonly Uri DefaultBase = new("https://widgets.streamloots.com/");

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9_-]{7,127}$")]
    private static partial Regex AlertIdPattern();

    /// <summary>
    /// The alert id from the Alerts widget URL (…/alerts/&lt;id&gt;, optionally …/media-stream) or a bare id; null with a
    /// user-facing reason when the text is not one.
    /// </summary>
    public static (string? Id, string? Error) ParseAlertId(string? input)
    {
        var text = (input ?? "").Trim();
        if (text.Length == 0) return (null, "Paste your StreamLoots Alerts widget URL first.");
        if (Uri.TryCreate(text, UriKind.Absolute, out var url) && url.Scheme is "http" or "https")
        {
            var segments = url.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var at = Array.FindIndex(segments, s => s.Equals("alerts", StringComparison.OrdinalIgnoreCase));
            if (at >= 0 && at + 1 < segments.Length && AlertIdPattern().IsMatch(segments[at + 1])) return (segments[at + 1], null);
            return (null, segments.Any(s => s.Equals("widgets", StringComparison.OrdinalIgnoreCase))
                ? "That is the StreamLoots alerts settings page. Copy the widget URL it shows (https://widgets.streamloots.com/alerts/...)."
                : "That URL has no StreamLoots alert id. Copy the Alerts widget URL (https://widgets.streamloots.com/alerts/...).");
        }
        return AlertIdPattern().IsMatch(text) ? (text, null) : (null, "That does not look like a StreamLoots Alerts widget URL or alert id.");
    }

    public static Uri StreamUrl(Uri baseUri, string alertId) => new(baseUri, $"alerts/{Uri.EscapeDataString(alertId)}/media-stream");

    /// <summary>The value of the "fields" entry named <paramref name="name"/> (case-insensitive), as text.</summary>
    private static string Field(JsonElement data, string name)
    {
        foreach (var field in data.Array("fields"))
            if (string.Equals(field.Str("name"), name, StringComparison.OrdinalIgnoreCase)) return field.Str("value");
        return "";
    }

    /// <summary>Maps one alert (the JSON of a stream message) to a bot event; null for keep-alives and unusable data.</summary>
    public static StreamLootsAlert? Map(string json, bool isTest = false)
    {
        if (JsonUtil.TryParse(json) is not { ValueKind: JsonValueKind.Object } root) return null;
        var data = root.Obj("data");
        if (data.ValueKind != JsonValueKind.Object) return null;
        var type = data.Str("type").Trim();
        if (type.Length == 0) return null;

        var fields = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var field in data.Array("fields"))
            if (field.Str("name") is { Length: > 0 } fieldName) fields[fieldName] = field.Str("value");

        var user = Field(data, "username");
        var alertMessage = root.Str("message");
        var message = Field(data, "message") is { Length: > 0 } m ? m : Field(data, "longMessage");
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["user"] = user,
            ["userName"] = user,
            ["message"] = message,
            ["alertMessage"] = alertMessage,
            ["alertType"] = type,
            ["imageUrl"] = root.Str("imageUrl"),
            ["soundUrl"] = root.Str("soundUrl"),
            ["videoUrl"] = root.Str("videoUrl"),
            ["fields"] = JsonUtil.Serialize(fields),
            ["isTest"] = isTest,
        };

        switch (type.ToLowerInvariant())
        {
            case "purchase":
            {
                var giftee = Field(data, "giftee");
                args["quantity"] = Quantity(Field(data, "quantity"));
                args["giftee"] = giftee;
                args["recipient"] = giftee;
                args["communityGift"] = false;
                args["isGift"] = giftee.Length > 0;
                return new StreamLootsAlert(giftee.Length > 0 ? "packGifted" : "packPurchased", args);
            }
            case "random-community-gift":
                args["quantity"] = Quantity(Field(data, "quantity"));
                args["giftee"] = "";
                args["recipient"] = "";
                args["communityGift"] = true;
                args["isGift"] = true;
                return new StreamLootsAlert("packGifted", args);
            case "redemption":
            {
                var rarity = Field(data, "rarity") is { Length: > 0 } r ? r : data.Str("cardRarity");
                args["cardName"] = data.Str("cardName");
                args["cardId"] = data.Str("cardId");
                args["cardSetId"] = data.Str("cardSetId");
                args["cardDescription"] = data.Str("description");
                args["cardRarity"] = rarity.ToLowerInvariant();
                args["longMessage"] = Field(data, "longMessage");
                args["hasVideo"] = root.Str("videoUrl").Length > 0;
                return new StreamLootsAlert("cardRedeemed", args);
            }
            default:
                return new StreamLootsAlert("alert", args);
        }
    }

    private static int Quantity(string text) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var q) ? (int)Math.Max(0, Math.Round(q)) : 0;

    /// <summary>Realistic sample alerts (shapes as StreamLoots sends them) for "Send test event".</summary>
    public static readonly IReadOnlyDictionary<string, string> Samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["purchase"] = """
            {"imageUrl":"","soundUrl":"",
             "message":"Cuebot Tester just bought 3 chests!","data":{"type":"purchase",
             "fields":[{"name":"quantity","value":3},{"name":"username","value":"Cuebot Tester"}]}}
            """,
        ["gift"] = """
            {"imageUrl":"","soundUrl":"",
             "message":"Cuebot Tester gifted 2 chests to LuckyViewer!","data":{"type":"purchase",
             "fields":[{"name":"username","value":"Cuebot Tester"},{"name":"quantity","value":2},{"name":"giftee","value":"LuckyViewer"}]}}
            """,
        ["communityGift"] = """
            {"imageUrl":"","message":"Cuebot Tester gifted 5 chests to the community!",
             "data":{"type":"random-community-gift","fields":[{"name":"username","value":"Cuebot Tester"},{"name":"quantity","value":"5"}]}}
            """,
        ["redemption"] = """
            {"imageUrl":"","soundUrl":"","videoUrl":"",
             "message":"Cuebot Tester redeemed Hydrate!","data":{"type":"redemption","cardId":"5f0c0a0b0c0d0e0f10111213",
             "cardSetId":"5f0c0a0b0c0d0e0f10111200","cardName":"Hydrate","cardRarity":"epic","description":"The streamer drinks some water",
             "fields":[{"name":"username","value":"Cuebot Tester"},{"name":"message","value":"Stay hydrated!"},{"name":"rarity","value":"epic"}]}}
            """,
    };
}
