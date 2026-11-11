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
        new("streamlabs.isSignedIn", "True while your Streamlabs app is connected (the REST features work)", [],
            (_, _, _) => Task.FromResult<object?>(streamlabs.IsSignedIn)),
        new("streamlabs.alertQueue", "Skip the alert, pause/unpause the queue, mute/unmute alert sounds, show/hide video", ["command"],
            async (_, args, ct) =>
            {
                await streamlabs.AlertCommandAsync(args.GetString("command", "skip"), ct).ConfigureAwait(false);
                return true;
            }),
        new("streamlabs.createAlert", "Show a custom alert in the Streamlabs alert box", ["type", "message", "userMessage", "image", "sound", "duration"],
            async (_, args, ct) =>
            {
                await streamlabs.CreateAlertAsync(args.GetString("type", "donation"), args.GetString("message"), args.GetString("userMessage"),
                    args.GetString("image"), args.GetString("sound"), (int)Math.Max(0, Number(args, "duration")), args.GetString("color"), ct).ConfigureAwait(false);
                return true;
            }),
        new("streamlabs.getDonations", "Get the latest Streamlabs donations", ["count", "currency"], async (_, args, ct) =>
            (await streamlabs.GetDonationsAsync(Count(args), args.GetString("currency"), ct).ConfigureAwait(false))
                .Select(d => new { id = d.Id, user = d.Name, amount = d.Amount, currency = d.Currency, formattedAmount = d.FormattedAmount, message = d.Message, createdAt = d.CreatedAt })
                .ToArray()),
        new("streamlabs.getPoints", "Get a viewer's Streamlabs (Cloudbot) loyalty points", ["user"], async (_, args, ct) =>
            await streamlabs.GetPointsAsync(args.GetString("user"), null, ct).ConfigureAwait(false)),
        new("streamlabs.addPoints", "Add (or with a negative amount remove) Streamlabs points; returns the new total", ["user", "amount"],
            async (_, args, ct) => await streamlabs.AddPointsAsync(args.GetString("user"), (long)Math.Round(Number(args, "amount")), null, ct).ConfigureAwait(false)),
        new("streamlabs.widget", "Spin the wheel, empty the tip jar or roll the credits", ["widget"], async (_, args, ct) =>
        {
            var widget = args.GetString("widget", "wheel").Trim().ToLowerInvariant();
            await (widget switch
            {
                "wheel" => streamlabs.SpinWheelAsync(ct),
                "jar" => streamlabs.EmptyJarAsync(ct),
                "credits" => streamlabs.RollCreditsAsync(ct),
                _ => throw new ScriptException("streamlabs.widget takes \"wheel\", \"jar\" or \"credits\""),
            }).ConfigureAwait(false);
            return true;
        }),
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
        new("streamelements.isLive", "True while StreamElements says the channel is live (null before it has said)", [],
            (_, _, _) => Task.FromResult<object?>(streamElements.IsLive)),
        new("streamelements.say", "Send a chat message as the StreamElements bot", ["message"], async (_, args, ct) =>
        {
            await streamElements.SayAsync(args.GetString("message"), ct).ConfigureAwait(false);
            return true;
        }),
        new("streamelements.getCounter", "Get a StreamElements chatbot counter's value", ["counter"], async (_, args, ct) =>
            await streamElements.GetBotCounterAsync(args.GetString("counter"), ct).ConfigureAwait(false)),
        new("streamelements.overlayAction", "Pause, play, mute, unmute or reload StreamElements overlays", ["action"], async (_, args, ct) =>
        {
            await streamElements.OverlayActionAsync(args.GetString("action", "pause"), ct).ConfigureAwait(false);
            return true;
        }),
        new("streamelements.setSessionValue", "Change a StreamElements session value, like tip-goal amount", ["key", "field", "value"],
            async (_, args, ct) =>
            {
                await streamElements.SetSessionValueAsync(args.GetString("key"), args.GetString("field"), args.GetString("value"), ct).ConfigureAwait(false);
                return true;
            }),
        new("streamelements.getUserRank", "Get a viewer's place on the StreamElements points list", ["user"], async (_, args, ct) =>
            await streamElements.GetUserRankAsync(args.GetString("user"), ct).ConfigureAwait(false)),
        new("streamelements.bulkPoints", "Add or set StreamElements points for many viewers at once; returns how many", ["users", "amount", "mode"],
            async (_, args, ct) =>
            {
                var users = args.GetElement("users") is { ValueKind: System.Text.Json.JsonValueKind.Array } list
                    ? list.EnumerateArray().Select(u => u.ToString())
                    : args.GetString("users").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries);
                return await streamElements.BulkPointsAsync(users, (long)Number(args, "amount"),
                    args.GetString("mode", "add").Equals("set", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
            }),
        new("streamelements.createTip", "Record a tip in StreamElements (goals and leaderboards count it)", ["user", "amount", "currency", "message"],
            async (_, args, ct) =>
            {
                var tip = await streamElements.CreateTipAsync(args.GetString("user"), Number(args, "amount"), args.GetString("currency", "USD"),
                    args.GetString("message"), true, ct).ConfigureAwait(false);
                return tip.Id;
            }),
        new("streamelements.getPendingTips", "Get the StreamElements tips waiting for a moderator", [], async (_, _, ct) =>
            (await streamElements.GetPendingTipsAsync(ct).ConfigureAwait(false))
                .Select(t => new { id = t.Id, user = t.UserName, amount = t.Amount, currency = t.Currency, formattedAmount = t.FormattedAmount, message = t.Message })
                .ToArray()),
        new("streamelements.getCheerLeaderboard", "Get the top cheerers for a period", ["count", "period"], async (_, args, ct) =>
            (await streamElements.GetCheerLeaderboardAsync(Count(args), args.GetString("period", "alltime"), ct).ConfigureAwait(false))
                .Select(r => new { user = r.UserName, total = r.Total }).ToArray()),
        new("streamelements.getStoreItems", "List the StreamElements loyalty store items", [], async (_, _, ct) =>
            (await streamElements.GetStoreItemsAsync(ct).ConfigureAwait(false))
                .Select(i => new { id = i.Id, name = i.Name, enabled = i.Enabled, cost = i.Cost, stock = i.Stock }).ToArray()),
        new("streamelements.setStoreItem", "Turn a store item on or off and change its cost or stock", ["item", "enabled", "cost", "stock"],
            async (_, args, ct) =>
            {
                bool? enabled = args.GetElement("enabled") is { ValueKind: System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False } e
                    ? e.ValueKind == System.Text.Json.JsonValueKind.True : null;
                double? cost = args.GetElement("cost") is { ValueKind: System.Text.Json.JsonValueKind.Number } c ? c.GetDouble() : null;
                long? stock = args.GetElement("stock") is { ValueKind: System.Text.Json.JsonValueKind.Number } n ? (long)n.GetDouble() : null;
                var item = await streamElements.UpdateStoreItemAsync(args.GetString("item"), enabled, false, cost, stock, ct).ConfigureAwait(false);
                return new { id = item.Id, name = item.Name, enabled = item.Enabled, cost = item.Cost, stock = item.Stock };
            }),
        new("streamelements.completeRedemption", "Mark a StreamElements store redemption done", ["redemptionId"], async (_, args, ct) =>
        {
            await streamElements.CompleteRedemptionAsync(args.GetString("redemptionId"), ct).ConfigureAwait(false);
            return true;
        }),
        new("streamelements.refundRedemption", "Cancel a StreamElements store redemption and give the points back", ["redemptionId"],
            async (_, args, ct) =>
            {
                await streamElements.RefundRedemptionAsync(args.GetString("redemptionId"), ct).ConfigureAwait(false);
                return true;
            }),
        new("streamelements.getPlayingSong", "The StreamElements song request playing now, or null", [], async (_, _, ct) =>
            await streamElements.GetPlayingSongAsync(ct).ConfigureAwait(false) is { } song
                ? new { id = song.Id, title = song.Title, url = song.Url, user = song.UserName, duration = song.Duration }
                : null),
        new("streamelements.addSong", "Add a song to the StreamElements song request queue", ["video"], async (_, args, ct) =>
        {
            var song = await streamElements.AddSongAsync(args.GetString("video"), ct).ConfigureAwait(false);
            return new { id = song.Id, title = song.Title, url = song.Url };
        }),
        new("streamelements.replayLatest", "Ask StreamElements to send its newest activity of a kind again; returns its id or \"\"", ["type"],
            async (_, args, ct) => await streamElements.ReplayLatestAsync(args.GetString("type", "tip"), ct).ConfigureAwait(false)),
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

    private static double Number(System.Text.Json.JsonElement args, string name) =>
        args.GetElement(name) is { ValueKind: System.Text.Json.JsonValueKind.Number } n ? n.GetDouble() : JsonReaders.ParseNumber(args.GetString(name));

    private static int Count(System.Text.Json.JsonElement args) =>
        args.GetElement("count") is { ValueKind: System.Text.Json.JsonValueKind.Number } n ? (int)n.GetDouble()
            : JsonReaders.ParseNumber(args.GetString("count")) is > 0 and var c ? (int)c : 5;
}
