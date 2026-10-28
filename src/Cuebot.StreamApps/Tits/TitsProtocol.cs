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

    /// <summary>
    /// The request envelope. The item list also carries the top-level <c>sendImage</c> that the README marks obsolete
    /// "to keep integrity" with older apps: builds before mid-2023 read only that one and would otherwise send every image.
    /// </summary>
    public static string Request(string messageType, string requestId, object? data = null) =>
        JsonUtil.Serialize(new
        {
            apiName = ApiName, apiVersion = ApiVersion, requestID = requestId, messageType,
            sendImage = messageType == ItemList ? false : (bool?)null, data,
        });

    public static object ItemListData() => new { sendImage = false };

    public static object ThrowData(IReadOnlyList<string> itemIds, double delaySeconds, int amount, bool errorOnMissingId) =>
        new { items = itemIds, delayTime = delaySeconds, amountOfThrows = amount, errorOnMissingID = errorOnMissingId };

    public static object ActivateData(string triggerId, string triggerName) => new { triggerID = triggerId, triggerName };

    public static string MessageType(JsonElement message) => message.Str("messageType");

    public static string RequestId(JsonElement message) => message.Str("requestID");

    public static JsonElement Data(JsonElement message) =>
        message.ValueKind == JsonValueKind.Object && message.TryGetProperty("data", out var d) ? d : default;

    /// <summary>
    /// Errors are not documented (TITSAPI issue #9). Open-source clients expect "APIError" or "TITSApiError" with
    /// <c>data.errorID</c> and <c>data.message</c>, so any message type containing "Error" counts as one.
    /// </summary>
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

    /// <summary>
    /// Arguments of a broadcast event, named like Streamer.bot's T.I.T.S. trigger variables. Names missing from the
    /// payload (trigger events only gained <c>triggerName</c> in the README's October 2025 update, and early hit events
    /// carried ids only) are filled from the cached lists.
    /// </summary>
    public static Dictionary<string, object?> EventArgs(JsonElement message, IReadOnlyList<TitsEntry>? items = null, IReadOnlyList<TitsEntry>? triggers = null)
    {
        var data = Data(message);
        var triggerId = data.Str("triggerID");
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["triggerId"] = triggerId,
            ["triggerName"] = data.Str("triggerName") is { Length: > 0 } triggerName ? triggerName : NameOf(triggers, triggerId),
        };
        if (MessageType(message) == HitEvent)
        {
            var itemId = data.Str("itemID");
            args["itemId"] = itemId;
            args["itemName"] = data.Str("itemName") is { Length: > 0 } itemName ? itemName : NameOf(items, itemId);
            args["strength"] = data.Num("strength") ?? 0;
            var direction = data.ValueKind == JsonValueKind.Object && data.TryGetProperty("direction", out var d) ? d : default;
            args["direction.x"] = direction.Num("x") ?? 0;
            args["direction.y"] = direction.Num("y") ?? 0;
            args["direction.z"] = direction.Num("z") ?? 0;
        }
        return args;
    }

    private static string NameOf(IReadOnlyList<TitsEntry>? entries, string id) =>
        id.Length == 0 ? "" : entries?.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))?.Name ?? "";

    /// <summary>Names or ids separated by commas or new lines.</summary>
    public static string[] SplitList(string text) =>
        text.Split([',', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
