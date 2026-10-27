using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Cuebot.Donations.DonorDrive;

/// <summary>What Cuebot keeps of a participant or team; a change of any field is a "Profile Updated".</summary>
public sealed record DonorDriveProfile(
    string Id,
    bool IsTeam,
    string Name,
    string TeamName,
    string EventName,
    double Goal,
    double Raised,
    int NumDonations,
    string AvatarUrl,
    string PageUrl,
    string DonateUrl,
    bool StreamIsLive);

public sealed record DonorDriveIncentive(
    string Id,
    string Description,
    double Amount,
    int Quantity,
    int QuantityClaimed,
    bool IsActive,
    string StartDate,
    string EndDate,
    string ImageUrl,
    string DonateUrl);

/// <summary>Parses DonorDrive Public API objects and builds the bot event arguments (Streamer.bot's variable names).</summary>
internal static partial class DonorDriveEventMapper
{
    public const string Service = "DonorDrive";

    public static DonorDriveProfile? Profile(JsonElement json, bool isTeam)
    {
        if (json.ValueKind == JsonValueKind.Array) json = json.EnumerateArray().FirstOrDefault();
        if (json.ValueKind != JsonValueKind.Object) return null;
        var links = json.Child("links");
        return new DonorDriveProfile(
            json.First(isTeam ? "teamID" : "participantID", "teamID", "participantID"),
            isTeam,
            isTeam ? json.First("name", "displayName") : json.First("displayName", "name"),
            isTeam ? "" : json.Str("teamName"),
            json.Str("eventName"),
            Math.Round(json.Number("fundraisingGoal"), 2),
            Math.Round(json.Number("sumDonations"), 2),
            (int)json.Number("numDonations"),
            json.Str("avatarImageURL"),
            links.Str("page"),
            links.Str("donate"),
            json.Str("streamIsLive") == "true");
    }

    public static DonorDriveIncentive? Incentive(JsonElement json)
    {
        if (json.ValueKind != JsonValueKind.Object || json.Str("incentiveID") is not { Length: > 0 } id) return null;
        // No quantity = unlimited (-1, as Streamer.bot documents it).
        var quantity = json.Has("quantity") ? (int)json.Number("quantity") : -1;
        return new DonorDriveIncentive(id, json.Str("description"), Math.Round(json.Number("amount"), 2), quantity,
            (int)json.Number("quantityClaimed"), !json.Has("isActive") || json.Str("isActive") == "true",
            Date(json.Str("startDateUTC")), Date(json.Str("endDateUTC")), json.Str("incentiveImageURL"), json.Child("links").Str("donate"));
    }

    public static string DonationId(JsonElement donation) => donation.Str("donationID");

    /// <summary>A donation (and its incentive, when it claimed one). Anonymous donors have no displayName/amount.</summary>
    public static DonationEvent Donation(JsonElement d, DonorDriveProfile profile, DonorDriveIncentive? incentive, string currency, bool isTest = false)
    {
        var name = d.Str("displayName");
        var anonymous = name.Length == 0;
        if (anonymous) name = "Anonymous";
        var hasAmount = d.Has("amount");
        var amount = Math.Round(d.Number("amount"), 2);
        var message = d.Str("message");
        var formatted = hasAmount ? Money.Format(amount, currency) : "";
        var args = Base(name, message, amount, currency, formatted, profile, isTest);
        args["donationId"] = DonationId(d);
        args["donorId"] = d.Str("donorID");
        args["donorName"] = name;
        args["donorAmount"] = amount;
        args["donorMessage"] = message;
        args["donorAvatarUrl"] = d.Str("avatarImageURL");
        args["donorIsAnonymous"] = anonymous;
        args["donorIsRecipient"] = d.Str("donorIsRecipient") == "true";
        args["isRegFee"] = d.Str("isRegFee") == "true";
        args["recipientName"] = d.Str("recipientName");
        args["recipientImageUrl"] = d.Str("recipientImageURL");
        args["createdAt"] = Date(d.Str("createdDateUTC"));
        args["participantId"] = d.Str("participantID");
        args["teamId"] = d.Str("teamID");
        args["incentiveId"] = d.Str("incentiveID");
        AddIncentive(args, incentive);
        return new DonationEvent(DonorDriveEventTypes.Donation, [$"donordrive:donation:{DonationId(d)}"], args);
    }

    /// <summary>The Incentive event for a donation that claimed one: the donation's arguments plus the incentive.</summary>
    public static DonationEvent IncentiveEvent(DonationEvent donation, string donationId) =>
        new(DonorDriveEventTypes.Incentive, [$"donordrive:incentive:{donationId}"], new Dictionary<string, object?>(donation.Args, StringComparer.OrdinalIgnoreCase));

    public static DonationEvent ProfileUpdated(DonorDriveProfile profile, DonorDriveProfile? previous, string currency, bool isTest = false)
    {
        var args = Base(profile.Name, "", 0, currency, "", profile, isTest);
        args["name"] = profile.Name;
        args["teamName"] = profile.TeamName;
        args["avatarUrl"] = profile.AvatarUrl;
        args["streamIsLive"] = profile.StreamIsLive;
        args["previousRaised"] = previous?.Raised ?? 0d;
        args["previousGoal"] = previous?.Goal ?? 0d;
        args["previousDonations"] = previous?.NumDonations ?? 0;
        // Never deduplicated: the poller only publishes it when something changed.
        return new DonationEvent(DonorDriveEventTypes.ProfileUpdated, [], args);
    }

    private static Dictionary<string, object?> Base(string user, string message, double amount, string currency, string formatted,
        DonorDriveProfile profile, bool isTest) =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["service"] = Service,
            ["platform"] = Service,
            ["user"] = user,
            ["userName"] = user,
            ["message"] = message,
            ["amount"] = amount,
            ["currency"] = currency,
            ["formattedAmount"] = formatted,
            ["profileId"] = profile.Id,
            ["profileName"] = profile.Name,
            ["profileTeamName"] = profile.TeamName,
            ["profileUrl"] = profile.PageUrl,
            ["donateUrl"] = profile.DonateUrl,
            ["eventName"] = profile.EventName,
            ["goal"] = profile.Goal,
            ["raised"] = profile.Raised,
            ["formattedGoal"] = Money.Format(profile.Goal, currency),
            ["formattedRaised"] = Money.Format(profile.Raised, currency),
            ["donations"] = profile.NumDonations,
            ["isParticipant"] = !profile.IsTeam,
            ["isTeam"] = profile.IsTeam,
            ["isTest"] = isTest,
            ["receivedAt"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
        };

    private static void AddIncentive(Dictionary<string, object?> args, DonorDriveIncentive? i)
    {
        args["isIncentive"] = i is not null;
        args["incentive.id"] = i?.Id ?? "";
        args["incentive.description"] = i?.Description ?? "";
        args["incentive.amount"] = i?.Amount ?? 0d;
        args["incentive.quantity"] = i?.Quantity ?? 0;
        args["incentive.quantityClaimed"] = i?.QuantityClaimed ?? 0;
        args["incentive.isActive"] = i?.IsActive ?? false;
        args["incentive.startDate"] = i?.StartDate ?? "";
        args["incentive.endDate"] = i?.EndDate ?? "";
        args["incentive.image"] = i?.ImageUrl ?? "";
        args["incentive.links.donate"] = i?.DonateUrl ?? "";
    }

    /// <summary>DonorDrive dates look like "2019-10-28T16:02:42.583+0000" (fraction digits vary); returned as ISO 8601 UTC.</summary>
    internal static string Date(string raw)
    {
        if (raw.Length == 0) return "";
        var normalized = Offset().Replace(raw.Trim(), "$1:$2");
        return DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)
            ? time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture)
            : raw;
    }

    [GeneratedRegex(@"([+-]\d{2})(\d{2})$")]
    private static partial Regex Offset();

    /// <summary>Participant, donation and incentive samples, as in the DonorDrive Public API docs, for "send test event".</summary>
    public const string SampleParticipant = """{"displayName":"Test Participant","fundraisingGoal":1000,"eventName":"Extra Life 2026","links":{"donate":"https://www.extra-life.org/participants/1#donate","page":"https://www.extra-life.org/participants/1"},"createdDateUTC":"2026-01-01T15:02:38.93+0000","eventID":1,"sumDonations":250,"participantID":1,"teamName":"Test Team","avatarImageURL":"","teamID":2,"isTeamCaptain":true,"sumPledges":0,"numDonations":12}""";

    public const string SampleDonation = """{"displayName":"TestDonor","participantID":1,"amount":25,"donorID":"TESTDONOR","isRegFee":false,"avatarImageURL":"","createdDateUTC":"2026-01-01T16:02:42.583+0000","eventID":1,"teamID":2,"donationID":"TESTDONATION","recipientName":"Test Participant","message":"For the kids!","donorIsRecipient":false}""";

    public const string SampleIncentive = """{"endDateUTC":"2026-12-31T07:00:00.0+0000","amount":25,"description":"Spin the prize wheel!","quantity":5,"quantityClaimed":1,"incentiveID":"TEST-INCENTIVE","isActive":true,"startDateUTC":"2026-01-01T07:00:00.0+0000"}""";

    public static IReadOnlyList<string> TestKinds { get; } = ["donation", "incentive", "profile"];
}
