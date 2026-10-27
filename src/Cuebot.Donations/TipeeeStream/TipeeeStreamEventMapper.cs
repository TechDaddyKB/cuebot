using System.Globalization;
using System.Text.Json;

namespace Cuebot.Donations.TipeeeStream;

/// <summary>
/// Maps the argument of a TipeeeStream <c>new-event</c> (<c>{appKey, event:{id, type, ref, user, parameters, formattedAmount,
/// created_at, ...}}</c>) to a bot event. Donations are TipeeeStream's own; follows, subscriptions, cheers, raids and
/// hosts are relayed platform events. Unknown types yield null. See PROTOCOL.md.
/// </summary>
internal static class TipeeeStreamEventMapper
{
    public const string Service = "TipeeeStream";

    public static DonationEvent? Map(JsonElement payload, bool isTest = false)
    {
        // Some servers wrap the argument in an array.
        if (payload.ValueKind == JsonValueKind.Array)
            payload = payload.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
        if (payload.ValueKind != JsonValueKind.Object) return null;
        var ev = payload.Child("event") is { ValueKind: JsonValueKind.Object } inner ? inner : payload;
        var type = ev.Str("type").ToLowerInvariant();
        var p = ev.Child("parameters") is { ValueKind: JsonValueKind.Object } parameters ? parameters : default;
        var id = ev.Str("id");
        var reference = ev.Str("ref");
        var keys = new List<string>();
        if (id.Length > 0) keys.Add($"tipeee:id:{id}");
        if (reference.Length > 0) keys.Add($"tipeee:ref:{reference}");

        var user = p.ValueKind == JsonValueKind.Object ? p.Str("username") : "";
        if (user.Length == 0) user = ev.Child("user").Str("username");
        if (user.Length == 0) user = "Anonymous";
        var message = p.ValueKind == JsonValueKind.Object ? p.First("message", "formattedMessage") : "";

        DonationEvent? mapped;
        switch (type)
        {
            case "donation" or "tipeee":
                var amount = Math.Round(p.ValueKind == JsonValueKind.Object ? p.Number("amount") : 0, 2);
                if (amount == 0) amount = Math.Round(ev.Number("parameters.amount"), 2);
                var currency = p.ValueKind == JsonValueKind.Object ? Currency(p) : "";
                var formatted = ev.Str("formattedAmount") is { Length: > 0 } f ? f : Money.Format(amount, currency);
                var args = Base(user, message, amount, currency, formatted);
                args["username"] = user;
                args["fees"] = p.ValueKind == JsonValueKind.Object ? Math.Round(p.Number("fees"), 2) : 0d;
                args["avatar"] = Avatar(ev.Child("user"));
                args["tipeeStreamId"] = id;
                args["campaignId"] = p.ValueKind == JsonValueKind.Object ? p.Str("campaignId") : "";
                args["recurring"] = p.ValueKind == JsonValueKind.Object && p.Str("recurring") is "true" or "1";
                mapped = new DonationEvent(TipeeeStreamEventTypes.Donation, keys, args);
                break;
            case "follow":
                mapped = new DonationEvent(TipeeeStreamEventTypes.Follow, keys, Base(user, "", 0, "", ""), Relayed: true);
                break;
            case "subscription" or "resub":
                var sub = Base(user, message, 0, "", "");
                sub["months"] = (int)(p.ValueKind == JsonValueKind.Object ? p.Number("resub") : 0);
                mapped = new DonationEvent(TipeeeStreamEventTypes.Subscription, keys, sub, Relayed: true);
                break;
            case "cheer" or "bits":
                var bits = (int)(p.ValueKind == JsonValueKind.Object ? p.Number("amount") : 0);
                var cheer = Base(user, message, bits, "", $"{bits} bits");
                cheer["bits"] = bits;
                mapped = new DonationEvent(TipeeeStreamEventTypes.Cheer, keys, cheer, Relayed: true);
                break;
            case "raid":
                var raid = Base(user, "", 0, "", "");
                raid["viewers"] = (int)(p.ValueKind == JsonValueKind.Object ? p.Number("viewers") : 0);
                mapped = new DonationEvent(TipeeeStreamEventTypes.Raid, keys, raid, Relayed: true);
                break;
            case "hosting" or "host":
                var host = Base(user, "", 0, "", "");
                host["viewers"] = (int)(p.ValueKind == JsonValueKind.Object ? p.Number("viewers") : 0);
                mapped = new DonationEvent(TipeeeStreamEventTypes.Host, keys, host, Relayed: true);
                break;
            default:
                return null;
        }

        var a = mapped.Args;
        a["eventId"] = id;
        a["ref"] = reference;
        a["timestamp"] = Timestamp(ev.First("created_at", "inserted_at"));
        a["origin"] = ev.Str("origin");
        a["platform"] = Platform(ev.Str("origin"), reference);
        // TipeeeStream's own "simulate" button sends refs starting with "simulation".
        a["isSimulation"] = reference.StartsWith("simulation", StringComparison.OrdinalIgnoreCase);
        a["isTest"] = isTest;
        a["receivedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        return mapped;
    }

    private static string Currency(JsonElement p) => p.Child("currency") switch
    {
        { ValueKind: JsonValueKind.Object } c => c.First("code", "currency").ToUpperInvariant(),
        _ => p.Str("currency").ToUpperInvariant(),
    };

    /// <summary>The 2019 docs show the avatar as a string; newer payloads send <c>{path, ...}</c>.</summary>
    private static string Avatar(JsonElement? user) => user.Child("avatar") switch
    {
        { ValueKind: JsonValueKind.Object } avatar => avatar.First("path", "url", "src"),
        { } other when other.ValueKind != JsonValueKind.Array => user.Str("avatar"),
        _ => "",
    };

    /// <summary>TipeeeStream dates look like "2015-10-05T16:58:26+0200"; returned as ISO 8601 (raw when unreadable).</summary>
    internal static string Timestamp(string raw)
    {
        if (raw.Length == 0) return "";
        var normalized = System.Text.RegularExpressions.Regex.Replace(raw.Trim(), @"([+-]\d{2})(\d{2})$", "$1:$2");
        return DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time.ToString("O", CultureInfo.InvariantCulture)
            : raw;
    }

    private static string Platform(string origin, string reference)
    {
        if (reference.StartsWith("TWITCH_", StringComparison.OrdinalIgnoreCase)) return "Twitch";
        if (reference.StartsWith("YOUTUBE_", StringComparison.OrdinalIgnoreCase)) return "YouTube";
        return origin.ToLowerInvariant() switch
        {
            "twitch" => "Twitch",
            "youtube" => "YouTube",
            _ => Service,
        };
    }

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

    /// <summary>Sample <c>new-event</c> arguments (shaped like the official socket.io doc's sample) for "send test event".</summary>
    public static readonly IReadOnlyDictionary<string, string> Samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["donation"] = """{"appKey":"test","event":{"id":0,"type":"donation","user":{"username":"teststreamer","avatar":""},"ref":"test-donation","created_at":"2026-01-01T12:00:00+0000","parameters":{"username":"TestDonor","amount":5,"currency":"EUR","fees":0.25,"message":"This is a test donation"},"formattedAmount":"€5.00"}}""",
        ["follow"] = """{"appKey":"test","event":{"id":0,"type":"follow","origin":"twitch","ref":"TWITCH_follow_test","created_at":"2026-01-01T12:00:00+0000","parameters":{"username":"TestFollower"}}}""",
        ["subscription"] = """{"appKey":"test","event":{"id":0,"type":"subscription","origin":"twitch","ref":"TWITCH_subscription_test","created_at":"2026-01-01T12:00:00+0000","parameters":{"username":"TestSubscriber","resub":"3","message":"Three months!"}}}""",
    };
}
