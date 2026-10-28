using System.Globalization;
using System.Text.Json;

namespace Cuebot.Donations.Pally;

/// <summary>
/// Maps a Pally.gg websocket message <c>{"type":"campaigntip.notify","payload":{"campaignTip":{…},"page":{…}}}</c> to
/// <see cref="PallyEventTypes.CampaignTip"/> with Streamer.bot's variable names. Amounts are in US cents.
/// </summary>
internal static class PallyEventMapper
{
    public const string Service = "Pally.gg";
    public const string TipType = "campaigntip.notify";

    /// <summary>Tip ids starting with this come from Cuebot's own echo test through Pally's server.</summary>
    public const string EchoPrefix = "cuebot-echo-";

    public static DonationEvent? Map(JsonElement message, bool isTest = false)
    {
        if (message.ValueKind != JsonValueKind.Object) return null;
        if (!string.Equals(message.Str("type"), TipType, StringComparison.OrdinalIgnoreCase)) return null;
        var payload = message.Child("payload");
        if (payload.Child("campaignTip") is not { ValueKind: JsonValueKind.Object } tip) return null;
        var page = payload.Child("page") is { ValueKind: JsonValueKind.Object } p ? p : default;

        var id = tip.Str("id");
        var name = tip.First("displayName", "name");
        if (name.Length == 0) name = "Anonymous";
        var text = tip.Str("message");
        var gross = (long)tip.Number("grossAmountInCents");
        var net = (long)tip.Number("netAmountInCents");
        var fee = (long)tip.Number("processingFeeInCents");
        var amount = gross / 100d;
        var formatted = Money.Format(amount, "USD");
        var echo = id.StartsWith(EchoPrefix, StringComparison.Ordinal);

        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["service"] = Service,
            ["platform"] = Service,
            ["user"] = name,
            ["userName"] = name,
            ["message"] = text,
            ["amount"] = amount,
            ["currency"] = "USD",
            ["formattedAmount"] = formatted,
            ["tipId"] = id,
            ["tipDisplayName"] = name,
            ["tipMessage"] = text,
            ["createdAt"] = tip.Str("createdAt"),
            ["updatedAt"] = tip.Str("updatedAt"),
            ["tipGrossAmount"] = amount,
            ["tipGrossAmountFormatted"] = formatted,
            ["tipGrossAmountInCents"] = gross,
            ["tipNetAmount"] = net / 100d,
            ["tipNetAmountFormatted"] = Money.Format(net / 100d, "USD"),
            ["tipNetAmountInCents"] = net,
            // Streamer.bot documents these with the "Proccessing" spelling; both spellings are set.
            ["tipProccessingFee"] = fee / 100d,
            ["tipProccessingFeeFormatted"] = Money.Format(fee / 100d, "USD"),
            ["tipProccessingFeeInCents"] = fee,
            ["tipProcessingFee"] = fee / 100d,
            ["tipProcessingFeeFormatted"] = Money.Format(fee / 100d, "USD"),
            ["tipProcessingFeeInCents"] = fee,
            ["campaignId"] = page.ValueKind == JsonValueKind.Object ? page.First("campaignId", "id") : "",
            ["campaignSlug"] = page.ValueKind == JsonValueKind.Object ? page.Str("slug") : "",
            ["campaignTitle"] = page.ValueKind == JsonValueKind.Object ? page.Str("title") : "",
            ["campaignUrl"] = page.ValueKind == JsonValueKind.Object ? page.Str("url") : "",
            ["isEcho"] = echo,
            ["isReplay"] = false,
            ["isTest"] = isTest || echo,
            ["receivedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };
        // Not deduplicated: Pally sends each tip once, and sends it again only when someone presses "Replay Alert"
        // (docs.pally.gg/features/activity-feed: "re-broadcast … to all WebSockets"). PallyService flags those.
        return new DonationEvent(PallyEventTypes.CampaignTip, [], args);
    }

    /// <summary>The documented sample event (docs.pally.gg/advanced/websockets).</summary>
    public static string Sample(string tipId = "b1w2pjwjtb9fx0v1se9ex4n2") =>
        "{\"type\":\"campaigntip.notify\",\"payload\":{\"campaignTip\":{\"createdAt\":\"2026-01-01T18:02:33.743Z\",\"displayName\":\"Someone\"," +
        "\"grossAmountInCents\":500,\"id\":\"" + tipId + "\",\"message\":\"Keep it up!\",\"netAmountInCents\":470,\"processingFeeInCents\":30," +
        "\"updatedAt\":\"2026-01-01T18:02:33.743Z\"},\"page\":{\"id\":\"1627451579049x550722173620715500\",\"slug\":\"pally\"," +
        "\"title\":\"Pally.gg's Team Page\",\"url\":\"https://pally.gg/p/pally\"}}}";

    public static IReadOnlyList<string> TestKinds { get; } = ["campaignTip"];

    /// <summary>Pally's echo request: the server sends <c>payload</c> back on this connection.</summary>
    public static string Echo(string eventJson) => "{\"type\":\"echo\",\"payload\":" + eventJson + "}";
}
