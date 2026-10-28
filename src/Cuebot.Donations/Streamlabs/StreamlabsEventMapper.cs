using System.Globalization;
using System.Text.Json;

namespace Cuebot.Donations.Streamlabs;

/// <summary>
/// Maps the payload of a Streamlabs Socket API <c>event</c> (<c>{type, for, message: [...], event_id}</c>) to
/// bot events, one per entry of <c>message</c>. Unknown types yield nothing. See PROTOCOL.md for the samples.
/// </summary>
internal static class StreamlabsEventMapper
{
    public const string Service = "Streamlabs";

    public static IReadOnlyList<DonationEvent> Map(JsonElement payload, bool isTest = false)
    {
        if (payload.ValueKind != JsonValueKind.Object) return [];
        var type = payload.Str("type");
        var target = payload.Str("for");
        var eventId = payload.Str("event_id");
        var items = payload.Child("message") switch
        {
            { ValueKind: JsonValueKind.Array } array => array.EnumerateArray().ToArray(),
            { ValueKind: JsonValueKind.Object } single => [single],
            _ => [],
        };
        var list = new List<DonationEvent>();
        for (var i = 0; i < items.Length; i++)
        {
            if (items[i].ValueKind != JsonValueKind.Object) continue;
            var keys = Keys(type, eventId, i, items[i]);
            var mapped = IsStreamlabsOwn(target)
                ? Own(type, items[i], keys)
                : Relayed(type, target, items[i], keys);
            if (mapped is null) continue;
            Common(mapped.Args, eventId, isTest, target);
            list.Add(mapped);
        }
        return list;
    }

    /// <summary>Events Streamlabs itself produces carry no "for" (or "streamlabs").</summary>
    private static bool IsStreamlabsOwn(string target) =>
        target.Length == 0 || target.Equals("streamlabs", StringComparison.OrdinalIgnoreCase);

    private static string[] Keys(string type, string eventId, int index, JsonElement item)
    {
        var keys = new List<string>();
        if (eventId.Length > 0) keys.Add($"evt:{eventId}:{index}");
        if (item.Str("_id") is { Length: > 0 } alertId) keys.Add($"{type}:_id:{alertId}");
        else if (item.Str("id") is { Length: > 0 } id) keys.Add($"{type}:id:{id}");
        return [.. keys];
    }

    private static DonationEvent? Own(string type, JsonElement m, string[] keys) => type.ToLowerInvariant() switch
    {
        "donation" => Donation(m, keys),
        "streamlabscharitydonation" or "streamlabs_charity_donation" or "charitydonation" => Charity(m, keys),
        "merch" => Merch(StreamlabsEventTypes.Merch, "merchandise", m, keys),
        "loyalty_store_redemption" => Merch(StreamlabsEventTypes.LoyaltyStoreRedemption, "redemption", m, keys),
        "prime_sub_gift" => PrimeGift(m, keys),
        _ => null,
    };

    private static DonationEvent Donation(JsonElement m, string[] keys)
    {
        var (amount, currency, formatted) = Amount(m);
        var from = User(m);
        var message = m.Str("message");
        var args = Base(from, message, amount, currency, formatted);
        args["userId"] = m.Str("from_user_id");
        args["donationId"] = m.Str("id");
        // The documented sample names the receiving streamer in "to": {"name": …}.
        args["recipient"] = m.Child("to").Str("name");
        args["iconClassName"] = m.Str("iconClassName");
        args["donationAmount"] = amount;
        args["donationCurrency"] = currency;
        args["donationFormattedAmount"] = formatted;
        args["donationFrom"] = from;
        args["donationMessage"] = message;
        return new DonationEvent(StreamlabsEventTypes.Donation, keys, args);
    }

    private static DonationEvent Charity(JsonElement m, string[] keys)
    {
        var (amount, currency, formatted) = Amount(m);
        var from = User(m);
        var message = m.Str("message");
        var args = Base(from, message, amount, currency, formatted);
        args["campaignId"] = m.First("campaignId", "campaign_id");
        args["charityName"] = m.First("charityName", "charity_name", "beneficiary");
        args["charityDonationId"] = m.First("charityDonationId", "id");
        args["charityDonationAmount"] = amount;
        args["charityDonationCurrency"] = currency;
        args["charityDonationFormattedAmount"] = formatted;
        args["charityDonationFrom"] = from;
        args["charityDonationMessage"] = message;
        args["charityDonationCreatedAt"] = m.First("createdAt", "created_at");
        args["charityDonationGif"] = m.First("gif", "gifUrl");
        args["charityDonationIconClassName"] = m.First("iconClassName", "icon_class_name");
        return new DonationEvent(StreamlabsEventTypes.CharityDonation, keys, args);
    }

    /// <summary>Merch purchases and Cloudbot loyalty store redemptions share a shape (buyer, product, image).</summary>
    private static DonationEvent Merch(string eventType, string prefix, JsonElement m, string[] keys)
    {
        var from = User(m);
        var product = m.First("product", "productName", "item");
        var image = m.First("image_href", "imageHref", "image");
        var message = m.Str("message");
        var args = Base(from, message, 0, "", "");
        args["product"] = product;
        args["imageUrl"] = image;
        args[$"{prefix}From"] = from;
        args[$"{prefix}Product"] = product;
        args[$"{prefix}Message"] = message;
        args[$"{prefix}ImageUrl"] = image;
        args[$"{prefix}ImageEscaped"] = Uri.EscapeDataString(image);
        return new DonationEvent(eventType, keys, args);
    }

    private static DonationEvent PrimeGift(JsonElement m, string[] keys)
    {
        var from = User(m);
        var args = Base(from, m.Str("message"), 0, "", "");
        args["recipient"] = m.Has("from") ? m.Str("name") : "";
        args["gift"] = m.First("giftType", "gift_type", "product", "type");
        args["streamer"] = m.First("streamer", "to");
        return new DonationEvent(StreamlabsEventTypes.PrimeSubGift, keys, args);
    }

    private static DonationEvent? Relayed(string type, string target, JsonElement m, string[] keys)
    {
        var name = m.First("name", "from");
        var args = Base(name, "", 0, "", "");
        args["userId"] = m.First("id", "from_user_id", "channelId");
        args["createdAt"] = m.First("created_at", "publishedAt", "createdAt");
        switch (type.ToLowerInvariant())
        {
            case "follow":
                return new DonationEvent(StreamlabsEventTypes.Follow, keys, args, Relayed: true);
            case "subscription" or "resub":
                args["message"] = m.Str("message");
                args["months"] = (int)m.Number("months");
                args["tier"] = m.First("sub_plan", "membershipLevelName");
                args["tierName"] = m.Str("sub_plan_name");
                args["subType"] = m.Str("sub_type") is { Length: > 0 } subType ? subType : type;
                // YouTube sponsors carry "sponsorSince", Mixer subscribers "since".
                args["since"] = m.First("sponsorSince", "since");
                args["recipient"] = m.First("gifter") is { Length: > 0 } ? name : "";
                args["gifter"] = m.Str("gifter");
                return new DonationEvent(StreamlabsEventTypes.Subscription, keys, args, Relayed: true);
            case "bits":
                var bits = (int)m.Number("amount");
                args["bits"] = bits;
                args["amount"] = (double)bits;
                args["formattedAmount"] = $"{bits} bits";
                args["message"] = m.Str("message");
                return new DonationEvent(StreamlabsEventTypes.Bits, keys, args, Relayed: true);
            // The docs' sample says "raid", their type table says "raids".
            case "raid" or "raids":
                args["viewers"] = (int)m.Number("raiders");
                return new DonationEvent(StreamlabsEventTypes.Raid, keys, args, Relayed: true);
            case "host":
                args["viewers"] = (int)m.Number("viewers");
                args["hostType"] = m.Str("type");
                return new DonationEvent(StreamlabsEventTypes.Host, keys, args, Relayed: true);
            case "superchat":
                // YouTube amounts are in micros ("2000000" = 2.00).
                // Here "id" is the chat message ("LCC.…"); the sender is "channelId".
                args["userId"] = m.First("channelId", "id");
                args["superchatId"] = m.Str("id");
                var currency = m.Str("currency");
                var amount = m.Number("amount") / 1_000_000d;
                args["amount"] = amount;
                args["currency"] = currency;
                args["formattedAmount"] = m.Str("displayString") is { Length: > 0 } display ? display : Money.Format(amount, currency);
                args["message"] = m.Str("comment");
                return new DonationEvent(StreamlabsEventTypes.Superchat, keys, args, Relayed: true);
            default:
                return null;
        }
    }

    /// <summary>Amount, currency and display amount, tolerant of numbers sent as strings.</summary>
    private static (double Amount, string Currency, string Formatted) Amount(JsonElement m)
    {
        var amount = Math.Round(m.Number("amount"), 2);
        var currency = m.Str("currency").ToUpperInvariant();
        var formatted = m.First("formatted_amount", "formattedAmount");
        return (amount, currency, formatted.Length > 0 ? formatted : Money.Format(amount, currency));
    }

    private static string User(JsonElement m) => m.First("from", "name") is { Length: > 0 } user ? user : "Anonymous";

    private static Dictionary<string, object?> Base(string user, string message, double amount, string currency, string formatted) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["service"] = Service,
            ["user"] = user,
            ["userName"] = user,
            ["message"] = message,
            ["amount"] = amount,
            ["currency"] = currency,
            ["formattedAmount"] = formatted,
        };

    private static void Common(Dictionary<string, object?> args, string eventId, bool isTest, string target)
    {
        args["eventId"] = eventId;
        args["isTest"] = isTest;
        args["platform"] = Platform(target);
        args["receivedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>"twitch_account" → "Twitch"; Streamlabs' own events → "Streamlabs".</summary>
    internal static string Platform(string target) => target.ToLowerInvariant() switch
    {
        "" or "streamlabs" => Service,
        "twitch_account" => "Twitch",
        "youtube_account" => "YouTube",
        "facebook_account" => "Facebook",
        "trovo_account" => "Trovo",
        "tiktok_account" => "TikTok",
        var other => other.EndsWith("_account", StringComparison.Ordinal) ? other[..^"_account".Length] : other,
    };

    /// <summary>Sample payloads (from the public docs) for "send test event".</summary>
    public static readonly IReadOnlyDictionary<string, string> Samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["donation"] = """{"type":"donation","message":[{"id":96164121,"name":"TestDonor","amount":"13.37","formatted_amount":"$13.37","formattedAmount":"$13.37","message":"This is a test donation","currency":"USD","from":"TestDonor","from_user_id":null,"_id":"test"}],"event_id":"test"}""",
        ["charity"] = """{"type":"streamlabscharitydonation","message":[{"charityDonationId":"test","campaignId":"test","name":"TestDonor","from":"TestDonor","amount":"10.00","formattedAmount":"$10.00","currency":"USD","message":"For a good cause","createdAt":"2026-01-01T00:00:00Z","_id":"test"}],"event_id":"test"}""",
        ["merch"] = """{"type":"merch","message":[{"from":"TestBuyer","product":"Test Hoodie","message":"Love the merch","image_href":"https://example.com/hoodie.png","_id":"test"}],"event_id":"test"}""",
        ["loyalty"] = """{"type":"loyalty_store_redemption","message":[{"from":"TestViewer","product":"Hydrate!","message":"Drink water","imageHref":"","_id":"test"}],"event_id":"test"}""",
    };
}
