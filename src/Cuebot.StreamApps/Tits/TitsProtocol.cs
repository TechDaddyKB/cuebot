using System.Text.Json;
using Cuebot.StreamApps.Common;

namespace Cuebot.StreamApps.Tits;

/// <summary>An item in T.I.T.S.'s model importer, or one of its triggers.</summary>
public sealed record TitsEntry(string Id, string Name);

/// <summary>
/// The T.I.T.S. public API (github.com/Remasuri/TITSAPI): JSON over two websockets on port 42069, <c>/websocket</c>
/// for requests and <c>/events</c> for broadcasts. Every message carries <c>apiName</c> "TITSPublicApi",
/// <c>apiVersion</c> "1.0", a <c>requestID</c> (echoed in the response) and a <c>messageType</c>.
/// </summary>
internal static class TitsProtocol
{
    public const string ApiName = "TITSPublicApi";
    public const string ApiVersion = "1.0";
    public const string DataPath = "/websocket";
    public const string EventsPath = "/events";

    public const string ItemList = "TITSItemListRequest";
    public const string ItemInfo = "TITSItemInfoRequest";
    public const string ThrowItems = "TITSThrowItemsRequest";
    public const string TriggerList = "TITSTriggerListRequest";
    public const string TriggerActivate = "TITSTriggerActivateRequest";

    public const string HitEvent = "TITSHitEvent";
    public const string TriggerActivatedEvent = "TITSTriggerActivatedEvent";
    public const string TriggerEndedEvent = "TITSTriggerEndedEvent";

    public static string Request(string messageType, string requestId, object? data = null) =>
        JsonUtil.Serialize(new { apiName = ApiName, apiVersion = ApiVersion, requestID = requestId, messageType, data });

    public static object ItemListData() => new { sendImage = false };

    public static object ThrowData(IReadOnlyList<string> itemIds, double delaySeconds, int amount, bool errorOnMissingId) =>
        new { items = itemIds, delayTime = delaySeconds, amountOfThrows = amount, errorOnMissingID = errorOnMissingId };

    public static object ActivateData(string triggerId, string triggerName) => new { triggerID = triggerId, triggerName };

    public static string MessageType(JsonElement message) => message.Str("messageType");

    public static string RequestId(JsonElement message) => message.Str("requestID");

    public static JsonElement Data(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty("data", out var d) ? d : default;

    /// <summary>T.I.T.S. answers failures with an error message type (e.g. "APIError", like VTube Studio's API).</summary>
    public static bool IsError(JsonElement message) => MessageType(message).Contains("Error", StringComparison.OrdinalIgnoreCase);

    public static string ErrorText(JsonElement message)
    {
        var data = Data(message);
        foreach (var key in new[] { "message", "errorMessage", "error" })
            if (data.Str(key) is { Length: > 0 } text) return text;
        return message.Str("message") is { Length: > 0 } top ? top : MessageType(message);
    }

    /// <summary><c>data.items</c> or <c>data.triggers</c> as id/name pairs (images are never requested).</summary>
    public static IReadOnlyList<TitsEntry> Entries(JsonElement response, string listName)
    {
        var data = Data(response);
        if (data.ValueKind != JsonValueKind.Object || !data.TryGetProperty(listName, out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray()
            .Select(e => new TitsEntry(e.Str("ID") is { Length: > 0 } id ? id : e.Str("id"), e.Str("name")))
            .Where(e => e.Id.Length > 0)
            .ToArray();
    }

    /// <summary>Arguments of a broadcast event, named like Streamer.bot's T.I.T.S. trigger variables.</summary>
    public static Dictionary<string, object?> EventArgs(JsonElement message)
    {
        var data = Data(message);
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["triggerId"] = data.Str("triggerID"),
            ["triggerName"] = data.Str("triggerName"),
        };
        if (MessageType(message) == HitEvent)
        {
            args["itemId"] = data.Str("itemID");
            args["itemName"] = data.Str("itemName");
            args["strength"] = data.Num("strength") ?? 0;
            var direction = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("direction", out var d) ? d : default;
            args["direction.x"] = direction.Num("x") ?? 0;
            args["direction.y"] = direction.Num("y") ?? 0;
            args["direction.z"] = direction.Num("z") ?? 0;
        }
        return args;
    }

    /// <summary>Names or ids separated by commas or new lines.</summary>
    public static string[] SplitList(string text) =>
        text.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
