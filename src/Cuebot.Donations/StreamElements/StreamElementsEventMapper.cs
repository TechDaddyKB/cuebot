using System.Globalization;
using System.Text.Json;

namespace Cuebot.Donations.StreamElements;

/// <summary>
/// Maps a StreamElements activity (<c>{type, provider, channel, data: {...}, _id, activityId}</c>), as delivered by
/// the Astro <c>channel.activities</c> topic and by the legacy realtime socket's <c>event</c>, to bot events.
/// Tips, merch and store redemptions are StreamElements' own; everything else is a relayed platform event.
/// </summary>
internal static class StreamElementsEventMapper
{
    public const string Service = "StreamElements";

    public static DonationEvent? Map(JsonElement activity, bool isTest = false)
    {
        if (activity.ValueKind != JsonValueKind.Object) return null;
        var type = activity.Str("type");
        if (type.Length == 0) return null;
        var d = activity.Child("data") ?? default;
        var keys = new List<string>();
        if (activity.First("activityId", "_id") is { Length: > 0 } activityId) keys.Add($"activity:{activityId}");

        var user = d.ValueKind == JsonValueKind.Object ? d.First("displayName", "username", "name") : "";
        if (user.Length == 0) user = "Anonymous";
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["service"] = Service,
            ["user"] = user,
            ["userName"] = d.ValueKind == JsonValueKind.Object && d.Str("username") is { Length: > 0 } login ? login : user,
            ["userId"] = d.ValueKind == JsonValueKind.Object ? d.Str("providerId") : "",
            ["avatar"] = d.ValueKind == JsonValueKind.Object ? d.Str("avatar") : "",
            ["message"] = d.ValueKind == JsonValueKind.Object ? d.Str("message") : "",
            ["activityType"] = type,
            ["activityId"] = activity.First("activityId", "_id"),
            ["provider"] = activity.Str("provider"),
            ["platform"] = Platform(activity.Str("provider")),
            ["channelId"] = activity.Str("channel"),
            ["createdAt"] = activity.Str("createdAt"),
            ["isTest"] = isTest,
        };

        DonationEvent? result = type switch
        {
            "tip" => Tip(d, args, keys),
            "merch" => Merch(d, args, keys),
            "redemption" => Redemption(d, args, keys),
            _ => Relayed(type, d, args, keys),
        };
        return result;
    }

    private static DonationEvent Tip(JsonElement d, Dictionary<string, object?> args, List<string> keys)
    {
        if (d.Str("tipId") is { Length: > 0 } tipId)
        {
            keys.Add($"tip:{tipId}");
            args["tipId"] = tipId;
        }
        var (amount, currency, formatted) = Amount(d);
        SetAmount(args, amount, currency, formatted);
        args["tipUsername"] = args["user"];
        args["tipAmount"] = (decimal)amount;   // Streamer.bot: decimal
        args["tipProvider"] = args["provider"];
        args["tipCurrency"] = currency;
        args["tipFormattedAmount"] = formatted;
        args["tipMessage"] = args["message"];
        args["tipAvatar"] = args["avatar"];
        args["platform"] = Service;
        return new DonationEvent(StreamElementsEventTypes.Tip, keys, args);
    }

    private static DonationEvent Merch(JsonElement d, Dictionary<string, object?> args, List<string> keys)
    {
        var (amount, currency, formatted) = Amount(d);
        SetAmount(args, amount, currency, formatted);
        var items = d.Items("items").Where(i => i.ValueKind == JsonValueKind.Object).ToArray();
        var quantity = 0;
        var names = new List<string>();
        for (var i = 0; i < items.Length; i++)
        {
            var q = Math.Max(1, (int)items[i].Number("quantity"));
            quantity += q;
            var name = items[i].Str("name");
            names.Add(q > 1 ? $"{q}x {name}" : name);
            args[$"merchItem{i}.name"] = name;
            args[$"merchItem{i}.price"] = (decimal)items[i].Number("price");
            args[$"merchItem{i}.quantity"] = q;
        }
        args["product"] = string.Join(", ", names);
        args["merchUsername"] = args["user"];
        args["merchAmount"] = (decimal)amount;
        args["merchProvider"] = args["provider"];
        args["merchCurrency"] = currency;
        args["merchFormattedAmount"] = formatted;
        args["merchMessage"] = args["message"];
        args["merchAvatar"] = args["avatar"];
        args["merchItems"] = items.Length;
        args["merchQuantity"] = quantity;
        args["platform"] = Service;
        return new DonationEvent(StreamElementsEventTypes.Merch, keys, args);
    }

    private static DonationEvent Redemption(JsonElement d, Dictionary<string, object?> args, List<string> keys)
    {
        var item = d.First("itemName", "item", "redemption", "name");
        var cost = d.Number("amount");
        args["item"] = item;
        args["product"] = item;
        args["itemId"] = d.Str("itemId");
        args["cost"] = cost;
        args["redeemer"] = d.First("redeemer") is { Length: > 0 } redeemer ? redeemer : args["user"];
        args["platform"] = Service;
        return new DonationEvent(StreamElementsEventTypes.Redemption, keys, args);
    }

    private static DonationEvent Relayed(string type, JsonElement d, Dictionary<string, object?> args, List<string> keys)
    {
        var amount = d.Number("amount");
        string eventType;
        switch (type)
        {
            case "follow" or "follower":
                eventType = StreamElementsEventTypes.Follow;
                break;
            case "subscriber":
                eventType = StreamElementsEventTypes.Subscriber;
                args["months"] = (int)amount;
                args["tier"] = d.Str("tier");
                args["isGift"] = d.Child("gifted") is { ValueKind: JsonValueKind.True };
                args["gifter"] = d.Str("sender");
                break;
            case "cheer":
                eventType = StreamElementsEventTypes.Cheer;
                args["bits"] = (int)amount;
                args["amount"] = amount;
                args["formattedAmount"] = $"{(int)amount} bits";
                break;
            case "raid":
                eventType = StreamElementsEventTypes.Raid;
                args["viewers"] = (int)amount;
                break;
            case "host":
                eventType = StreamElementsEventTypes.Host;
                args["viewers"] = (int)amount;
                break;
            case "superchat":
                eventType = StreamElementsEventTypes.Superchat;
                var (value, currency, formatted) = Amount(d);
                SetAmount(args, value, currency, formatted);
                break;
            case "sponsor":
                eventType = StreamElementsEventTypes.Sponsor;
                args["months"] = (int)amount;
                args["tier"] = d.Str("tier");
                break;
            case "communityGiftPurchase":
                eventType = StreamElementsEventTypes.CommunityGift;
                args["gift"] = (int)amount;
                args["tier"] = d.Str("tier");
                break;
            case "charityCampaignDonation":
                eventType = StreamElementsEventTypes.CharityDonation;
                var (charity, charityCurrency, charityFormatted) = Amount(d);
                SetAmount(args, charity, charityCurrency, charityFormatted);
                break;
            default:
                eventType = StreamElementsEventTypes.Activity;
                args["amount"] = amount;
                break;
        }
        return new DonationEvent(eventType, keys, args, Relayed: true);
    }

    private static (double Amount, string Currency, string Formatted) Amount(JsonElement d)
    {
        var amount = Math.Round(d.Number("amount"), 2);
        var currency = d.Str("currency").ToUpperInvariant();
        return (amount, currency, Money.Format(amount, currency));
    }

    private static void SetAmount(Dictionary<string, object?> args, double amount, string currency, string formatted)
    {
        args["amount"] = amount;
        args["currency"] = currency;
        args["formattedAmount"] = formatted;
    }

    internal static string Platform(string provider) => provider.ToLowerInvariant() switch
    {
        "twitch" => "Twitch",
        "youtube" => "YouTube",
        "kick" => "Kick",
        "" or "streamelements" => Service,
        _ => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(provider),
    };

    /// <summary>
    /// Maps the legacy realtime socket's <c>event:test</c> (what the dashboard's "emulate" buttons send). Its
    /// argument has the widget shape <c>{listener: "tip-latest", event: {name, amount, message, items, …}}</c>
    /// (dev.streamelements.com "Connecting via Websocket", "Structure as on … on-event"); only tips and merch are
    /// mapped, always marked <c>isTest</c>.
    /// </summary>
    public static DonationEvent? MapWidgetTest(JsonElement test)
    {
        if (test.ValueKind != JsonValueKind.Object || test.Child("event") is not { ValueKind: JsonValueKind.Object } e) return null;
        var type = test.Str("listener") switch
        {
            "tip-latest" => "tip",
            "merch-latest" => "merch",
            _ => "",
        };
        if (type.Length == 0) return null;
        var name = e.First("name", "username", "displayName");
        var data = new Dictionary<string, object?>
        {
            ["username"] = name,
            ["displayName"] = e.First("displayName", "name"),
            ["amount"] = e.Number("amount"),
            ["currency"] = e.Str("currency"),
            ["message"] = e.Str("message"),
            ["avatar"] = e.Str("avatar"),
        };
        if (e.Child("items") is { ValueKind: JsonValueKind.Array } items) data["items"] = items;
        var activity = JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["type"] = type,
            ["provider"] = "streamelements",
            ["data"] = data,
            ["_id"] = e.Str("_id"),
        });
        return Map(activity, isTest: true);
    }

    /// <summary>Sample activities for "send test event".</summary>
    public static readonly IReadOnlyDictionary<string, string> Samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["tip"] = """{"type":"tip","provider":"twitch","channel":"test","data":{"tipId":"test","username":"testtipper","displayName":"TestTipper","amount":4.2,"currency":"USD","message":"This is a test tip","avatar":""},"_id":"test","activityId":"test"}""",
        ["merch"] = """{"type":"merch","provider":"twitch","channel":"test","data":{"username":"testbuyer","displayName":"TestBuyer","amount":45,"currency":"USD","message":"Love it","items":[{"name":"Test Hoodie","price":30,"quantity":1},{"name":"Test Mug","price":7.5,"quantity":2}]},"_id":"test","activityId":"test"}""",
        ["redemption"] = """{"type":"redemption","provider":"twitch","channel":"test","data":{"username":"testviewer","displayName":"TestViewer","itemName":"Hydrate!","amount":500,"message":"Drink water"},"_id":"test","activityId":"test"}""",
    };
}
