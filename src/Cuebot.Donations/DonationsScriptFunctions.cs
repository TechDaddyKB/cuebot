using Cuebot.Core;
using Cuebot.Core.Scripting;
using Cuebot.Donations.StreamElements;
using Cuebot.Donations.Streamlabs;
using Cuebot.Donations.TipeeeStream;
using Cuebot.Donations.DonorDrive;
using Cuebot.Donations.TreatStream;
using Cuebot.Donations.Pally;

namespace Cuebot.Donations;

/// <summary>Donation service functions available to user scripts.</summary>
internal sealed class DonationsScriptFunctions(StreamlabsService streamlabs, StreamElementsService streamElements, TipeeeStreamService tipeee,
    DonorDriveService donorDrive, TreatStreamService treatStream, PallyService pally) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("streamlabs.isConnected", "True while the Streamlabs socket is connected", [],
            (_, _, _) => Task.FromResult<object?>(streamlabs.State == ConnectionState.Connected)),
        new("streamlabs.sendTestEvent", "Publish a local test event (donation, charity, merch, loyalty)", ["kind"],
            (_, args, _) => Task.FromResult<object?>(streamlabs.SendTestEvent(args.GetString("kind", "donation")))),
        new("streamelements.isConnected", "True while the StreamElements socket is connected", [],
            (_, _, _) => Task.FromResult<object?>(streamElements.State == ConnectionState.Connected)),
        new("streamelements.sendTestEvent", "Publish a local test event (tip, merch, redemption)", ["kind"],
            (_, args, _) => Task.FromResult<object?>(streamElements.SendTestEvent(args.GetString("kind", "tip")))),
        new("streamelements.getUserPoints", "Get a user's StreamElements loyalty points", ["user"], async (_, args, ct) =>
        {
            var points = await streamElements.GetUserPointsAsync(args.GetString("user"), ct).ConfigureAwait(false);
            return new { user = points.UserName, points = points.Points, pointsAllTime = points.PointsAllTime, rank = points.Rank };
        }),
        new("streamelements.addUserPoints", "Add (or with a negative amount remove) StreamElements points; returns the new total",
            ["user", "amount"], async (_, args, ct) =>
            {
                var amount = args.GetElement("amount") is { ValueKind: System.Text.Json.JsonValueKind.Number } n ? (long)n.GetDouble()
                    : (long)JsonReaders.ParseNumber(args.GetString("amount"));
                return await streamElements.AddUserPointsAsync(args.GetString("user"), amount, ct).ConfigureAwait(false);
            }),
        new("streamelements.getTips", "Get the latest StreamElements tips", ["count"], async (_, args, ct) =>
        {
            var tips = await streamElements.GetTipsAsync(Count(args), ct).ConfigureAwait(false);
            return tips.Select(t => new { user = t.UserName, amount = t.Amount, currency = t.Currency, formattedAmount = t.FormattedAmount, message = t.Message, createdAt = t.CreatedAt }).ToArray();
        }),
        new("streamelements.getTipLeaderboard", "Get the top StreamElements tippers", ["count"], async (_, args, ct) =>
            (await streamElements.GetTipLeaderboardAsync(Count(args), ct).ConfigureAwait(false)).Select(r => new { user = r.UserName, total = r.Total }).ToArray()),
        new("streamelements.getPointsLeaderboard", "Get the StreamElements points leaderboard", ["count"], async (_, args, ct) =>
            (await streamElements.GetPointsLeaderboardAsync(Count(args), ct).ConfigureAwait(false)).Select(r => new { user = r.UserName, points = r.Total }).ToArray()),
        new("tipeeestream.isConnected", "True while the TipeeeStream socket is connected", [],
            (_, _, _) => Task.FromResult<object?>(tipeee.State == ConnectionState.Connected)),
        new("tipeeestream.sendTestEvent", "Publish a local test event (donation, follow, subscription)", ["kind"],
            (_, args, _) => Task.FromResult<object?>(tipeee.SendTestEvent(args.GetString("kind", "donation")))),
        new("donordrive.isConnected", "True while the DonorDrive participant/team is being polled", [],
            (_, _, _) => Task.FromResult<object?>(donorDrive.State == ConnectionState.Connected)),
        new("donordrive.sendTestEvent", "Publish a local test event (donation, incentive, profile)", ["kind"],
            (_, args, _) => Task.FromResult<object?>(donorDrive.SendTestEvent(args.GetString("kind", "donation")))),
        new("donordrive.getProfile", "The last DonorDrive profile read (name, goal, raised, donations), or null", [],
            (_, _, _) => Task.FromResult<object?>(donorDrive.Profile is { } p
                ? new { id = p.Id, name = p.Name, teamName = p.TeamName, eventName = p.EventName, goal = p.Goal, raised = p.Raised, donations = p.NumDonations, isTeam = p.IsTeam, url = p.PageUrl }
                : null)),
        new("treatstream.isConnected", "True while the TreatStream socket is connected", [],
            (_, _, _) => Task.FromResult<object?>(treatStream.State == ConnectionState.Connected)),
        new("treatstream.sendTestEvent", "Publish a local test treat", ["kind"],
            (_, args, _) => Task.FromResult<object?>(treatStream.SendTestEvent(args.GetString("kind", "treat")))),
        new("pally.isConnected", "True while the Pally.gg websocket is connected", [],
            (_, _, _) => Task.FromResult<object?>(pally.State == ConnectionState.Connected)),
        new("pally.sendTestEvent", "Publish a local test campaign tip", ["kind"],
            (_, args, _) => Task.FromResult<object?>(pally.SendTestEvent(args.GetString("kind", "campaignTip")))),
    ];

    private static int Count(System.Text.Json.JsonElement args) =>
        args.GetElement("count") is { ValueKind: System.Text.Json.JsonValueKind.Number } n ? (int)n.GetDouble()
            : JsonReaders.ParseNumber(args.GetString("count")) is > 0 and var c ? (int)c : 5;
}
