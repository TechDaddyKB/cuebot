using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Variables;

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
        // A donation's "id" is the donation_id of GET /donations: lets the REST back-fill recognise socket donations.
        if (type.Equals("donation", StringComparison.OrdinalIgnoreCase) && item.Str("id") is { Length: > 0 } donationId)
            keys.Add(DonationKey(donationId));
        return [.. keys];
    }

    /// <summary>The dedupe key of a donation by its Streamlabs donation id (socket <c>id</c>, REST <c>donation_id</c>).</summary>
    internal static string DonationKey(string donationId) => $"donation:donation_id:{donationId}";

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
        args["emotes"] = m.Raw("emotes");
        args["isBackfill"] = false;
        return new DonationEvent(StreamlabsEventTypes.Donation, keys, args);
    }

    /// <summary>A donation from GET /donations (the back-fill after a reconnect), shaped like a socket donation.</summary>
    internal static DonationEvent Backfilled(StreamlabsDonation d)
    {
        var args = Base(d.Name, d.Message, d.Amount, d.Currency, d.FormattedAmount);
        args["userId"] = "";
        args["donationId"] = d.Id;
        args["recipient"] = "";
        args["iconClassName"] = "";
        args["donationAmount"] = d.Amount;
        args["donationCurrency"] = d.Currency;
        args["donationFormattedAmount"] = d.FormattedAmount;
        args["donationFrom"] = d.Name;
        args["donationMessage"] = d.Message;
        args["emotes"] = "";
        args["isBackfill"] = true;
        args["createdAt"] = d.CreatedAt;
        Common(args, "", false, "");
        return new DonationEvent(StreamlabsEventTypes.Donation, [DonationKey(d.Id)], args);
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
        var created = m.First("createdAt", "created_at");
        args["charityDonationCreatedAt"] = StreamerBotArgs.Time(created) is { } time ? time : created;   // Streamer.bot: DateTime
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
        // Follows (Twitch user id, YouTube channel id) and YouTube members carry the user in "id"; bits and
        // Super Chats do not (their "id" is the cheer or the chat message), see the cases below.
        args["userId"] = m.First("id", "from_user_id", "channelId");
        args["createdAt"] = m.First("created_at", "publishedAt", "createdAt");
        args["emotes"] = m.Raw("emotes");
        switch (type.ToLowerInvariant())
        {
            case "follow":
                return new DonationEvent(StreamlabsEventTypes.Follow, keys, args, Relayed: true);
            case "subscription" or "resub":
                args["message"] = m.Str("message");
                args["months"] = (int)m.Number("months");
                args["tier"] = m.First("sub_plan", "membershipLevelName");
                args["tierName"] = m.Str("sub_plan_name");
                var subType = m.Str("sub_type") is { Length: > 0 } st ? st : type;
                var gifter = m.First("gifter", "gifter_display_name");
                args["subType"] = subType;
                // YouTube sponsors carry "sponsorSince", Mixer subscribers "since" (Mixer is gone; kept for old payloads).
                args["since"] = m.First("sponsorSince", "since");
                args["recipient"] = gifter.Length > 0 ? name : "";
                args["gifter"] = gifter;
                // The docs only show sub_type "resub"; Twitch's other values are sub, subgift, anonsubgift, primepaidupgrade...
                args["isGift"] = gifter.Length > 0 || subType.Contains("gift", StringComparison.OrdinalIgnoreCase);
                args["isResub"] = subType.Equals("resub", StringComparison.OrdinalIgnoreCase) || m.Number("months") > 1;
                return new DonationEvent(StreamlabsEventTypes.Subscription, keys, args, Relayed: true);
            case "bits":
                // The documented sample's "id" is the cheer's transaction id (a UUID), not the user: it has no user id.
                args["bitsId"] = m.Str("id");
                args["userId"] = m.Str("from_user_id");
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
            // Legacy: Twitch removed hosting in October 2022 and Mixer shut down in July 2020, so this is never sent
            // today; the docs still list it. Kept so old payloads and existing triggers keep working.
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
