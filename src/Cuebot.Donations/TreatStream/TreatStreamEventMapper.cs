using System.Globalization;
using System.Text.Json;

namespace Cuebot.Donations.TreatStream;

/// <summary>
/// Maps a TreatStream <c>realTimeTreat</c> (<c>{message, sender, receiver, title, sender_type, receiver_type,
/// date_created}</c>) to <see cref="TreatStreamEventTypes.Treat"/>. Treats are food items, so there is no amount.
/// </summary>
internal static class TreatStreamEventMapper
{
    public const string Service = "TreatStream";

    public static DonationEvent? Map(JsonElement payload, bool isTest = false)
    {
        if (payload.ValueKind == JsonValueKind.Array)
            payload = payload.EnumerateArray().FirstOrDefault(e => e.ValueKind == JsonValueKind.Object);
        if (payload.ValueKind != JsonValueKind.Object) return null;
        var sender = payload.First("sender", "username", "from");
        var title = payload.First("title", "treat", "type");
        if (sender.Length == 0 && title.Length == 0) return null;
        if (sender.Length == 0) sender = "Anonymous";
        var message = payload.Str("message");
        var created = payload.Str("date_created").Trim();
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["service"] = Service,
            ["platform"] = Service,
            ["user"] = sender,
            ["userName"] = sender,
            ["username"] = sender,
            ["message"] = message,
            ["type"] = title,
            ["treat"] = title,
            ["title"] = title,
            ["receiver"] = payload.Str("receiver"),
            ["senderType"] = Platform(payload.Str("sender_type")),
            ["receiverType"] = Platform(payload.Str("receiver_type")),
            ["timestamp"] = Timestamp(created),
            ["amount"] = 0d,
            ["currency"] = "",
            ["formattedAmount"] = "",
            ["isTest"] = isTest,
            ["receivedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        // No id is sent; the same sender, treat, time and message is the same treat.
        return new DonationEvent(TreatStreamEventTypes.Treat, [$"treat:{sender}|{title}|{created}|{message}"], args);
    }

    /// <summary>The REST API uses letter codes ("t", "f", "y"), the socket full names ("Twitch").</summary>
    internal static string Platform(string code) => code.Trim().ToLowerInvariant() switch
    {
        "t" or "twitch" => "Twitch",
        "y" or "youtube" => "YouTube",
        "f" or "facebook" => "Facebook",
        "k" or "kick" => "Kick",
        _ => code.Trim(),
    };

    /// <summary>"2024-03-16 14:21:44 " (no time zone; treated as UTC) → ISO 8601; raw when unreadable.</summary>
    internal static string Timestamp(string raw) =>
        DateTime.TryParseExact(raw.Trim(), "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time)
            ? new DateTimeOffset(time, TimeSpan.Zero).ToString("O", CultureInfo.InvariantCulture)
            : raw.Trim();

    /// <summary>The sample from TreatStream's API page, for "send test event".</summary>
    public static readonly IReadOnlyDictionary<string, string> Samples = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["treat"] = """{"message":"enjoy treat","sender":"TestViewer","receiver":"teststreamer","title":"Pizza","sender_type":"Twitch","receiver_type":"Twitch","date_created":"2026-01-01 14:21:44 "}""",
    };
}
