using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Donations.Triggers;

namespace Cuebot.Donations.StreamElements;

/// <summary>A StreamElements REST step described by a table row.</summary>
internal sealed class StreamElementsStep(string typeId, string displayName, IReadOnlyList<ParameterDescriptor> parameters,
    Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) : ISubActionHandler
{
    public string TypeId => typeId;
    public string Category => StreamElementsTriggers.Category;
    public string DisplayName => displayName;
    public IReadOnlyList<ParameterDescriptor> Parameters => parameters;

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await run(context, p, ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>The steps made once per service (DI hands out each as an <see cref="ISubActionHandler"/>).</summary>
internal sealed record StreamElementsMoreSubActionSet(IReadOnlyList<ISubActionHandler> Steps);

/// <summary>
/// The StreamElements steps beyond points and tips: chatbot, overlays, session data, loyalty store, contests,
/// giveaways and song requests. Type ids are stored in actions.json and must never change.
/// </summary>
internal static class StreamElementsMoreSubActions
{
    /// <summary>Every type id <see cref="Create"/> makes (DI registers them without building the service).</summary>
    public static readonly string[] TypeIds =
    [
        "streamelements.botSay", "streamelements.botMute", "streamelements.getBotStatus", "streamelements.getCounter",
        "streamelements.setTimer", "streamelements.setCommand", "streamelements.overlayAction", "streamelements.setSessionValue",
        "streamelements.resetSession", "streamelements.replayLatest", "streamelements.getUserRank", "streamelements.resetUserPoints",
        "streamelements.bulkPoints", "streamelements.resetPoints", "streamelements.getCheerLeaderboard", "streamelements.createTip",
        "streamelements.getPendingTips", "streamelements.getStoreItem", "streamelements.setStoreItem", "streamelements.completeRedemption",
        "streamelements.refundRedemption", "streamelements.getPendingRedemptions", "streamelements.createContest", "streamelements.contestAction",
        "streamelements.getContest", "streamelements.giveawayAction", "streamelements.getGiveaway", "streamelements.songPlayer",
        "streamelements.getSong", "streamelements.addSong", "streamelements.removeSong", "streamelements.getSongQueue",
    ];

    public static IReadOnlyList<ISubActionHandler> Create(StreamElementsService se)
    {
        static ParameterDescriptor Text(string key, string label, string defaultValue = "", string? help = null) =>
            new(key, label) { DefaultValue = defaultValue, Help = help };
        static ParameterDescriptor Number(string key, string label, string defaultValue) =>
            new(key, label, ParameterKind.Number) { DefaultValue = defaultValue };
        static ParameterDescriptor Choice(string key, string label, string defaultValue, params string[] choices) =>
            new(key, label, ParameterKind.Choice) { DefaultValue = defaultValue, Choices = choices };
        static ParameterDescriptor User() =>
            new("user", "User") { DefaultValue = "%userName%", Help = "StreamElements loyalty user (the platform login name)" };
        static bool? OnOff(string value) => value.Trim().ToLowerInvariant() switch
        {
            "on" or "enable" or "enabled" or "true" => true,
            "off" or "disable" or "disabled" or "false" => false,
            _ => null,
        };
        static double? OptionalNumber(string value, string label) => value.Trim().Length == 0 ? null
            : double.TryParse(value.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n
            : throw new ArgumentException($"{label} must be a number (got '{value}')");
        static string N(int i) => i.ToString(CultureInfo.InvariantCulture);

        void Song(ActionRunContext ctx, StreamElementsSong? song)
        {
            ctx.Args["seSongFound"] = song is not null;
            ctx.Args["seSongId"] = song?.Id ?? "";
            ctx.Args["seSongTitle"] = song?.Title ?? "";
            ctx.Args["seSongUrl"] = song?.Url ?? "";
            ctx.Args["seSongUser"] = song?.UserName ?? "";
            ctx.Args["seSongDuration"] = song?.Duration ?? 0;
        }

        void Item(ActionRunContext ctx, StreamElementsStoreItem item)
        {
            ctx.Args["seItemId"] = item.Id;
            ctx.Args["seItemName"] = item.Name;
            ctx.Args["seItemEnabled"] = item.Enabled;
            ctx.Args["seItemCost"] = item.Cost;
            ctx.Args["seItemStock"] = item.Stock;
        }

        return
        [
            // ---- chatbot ----
            new StreamElementsStep("streamelements.botSay", "Send chat message as the StreamElements bot", [Text("message", "Message")],
                (_, p, ct) => se.SayAsync(p["message"], ct)),
            new StreamElementsStep("streamelements.botMute", "Mute or unmute the StreamElements bot", [Choice("state", "State", "Mute", "Mute", "Unmute")],
                (_, p, ct) => se.SetBotMutedAsync(!p["state"].Equals("Unmute", StringComparison.OrdinalIgnoreCase), ct)),
            new StreamElementsStep("streamelements.getBotStatus", "Get StreamElements bot status", [], async (ctx, _, ct) =>
            {
                var bot = await se.GetBotAsync(ct).ConfigureAwait(false);
                ctx.Args["seBotName"] = bot.Name;
                ctx.Args["seBotJoined"] = bot.Joined;
                ctx.Args["seBotMuted"] = bot.Muted;
                ctx.Args["seBotMod"] = bot.Mod;
            }),
            new StreamElementsStep("streamelements.getCounter", "Get chatbot counter", [Text("counter", "Counter", "deaths")], async (ctx, p, ct) =>
            {
                ctx.Args["seCounter"] = p["counter"].Trim();
                ctx.Args["seCounterValue"] = await se.GetBotCounterAsync(p["counter"], ct).ConfigureAwait(false);
            }),
            new StreamElementsStep("streamelements.setTimer", "Turn a chatbot timer on or off",
                [Text("timer", "Timer"), Choice("state", "State", "Toggle", "On", "Off", "Toggle")], async (ctx, p, ct) =>
                {
                    var timer = await se.SetTimerEnabledAsync(p["timer"], OnOff(p["state"]), ct).ConfigureAwait(false);
                    ctx.Args["seTimerEnabled"] = timer.Enabled;
                }),
            new StreamElementsStep("streamelements.setCommand", "Turn a chatbot command on or off",
                [Text("command", "Command"), Choice("state", "State", "Toggle", "On", "Off", "Toggle")], async (ctx, p, ct) =>
                {
                    var command = await se.SetCommandEnabledAsync(p["command"], OnOff(p["state"]), ct).ConfigureAwait(false);
                    ctx.Args["seCommandEnabled"] = command.Enabled;
                }),

            // ---- overlays and session ----
            new StreamElementsStep("streamelements.overlayAction", "Control StreamElements overlays",
                [Choice("action", "Action", "pause", [.. StreamElementsService.OverlayActions])], (_, p, ct) => se.OverlayActionAsync(p["action"], ct)),
            new StreamElementsStep("streamelements.setSessionValue", "Set session value",
                [Text("key", "Session key", "tip-goal"), Text("field", "Field", "amount"), Text("value", "Value")],
                (_, p, ct) => se.SetSessionValueAsync(p["key"], p["field"], p["value"], ct)),
            new StreamElementsStep("streamelements.resetSession", "Reset session", [], (_, _, ct) => se.ResetSessionAsync(ct)),
            new StreamElementsStep("streamelements.replayLatest", "Replay the latest activity",
                [Choice("type", "Activity", "tip", "tip", "merch", "redemption", "cheer", "subscriber", "follow", "raid")], async (ctx, p, ct) =>
                {
                    ctx.Args["seReplayedId"] = await se.ReplayLatestAsync(p["type"], ct).ConfigureAwait(false);
                }),

            // ---- points ----
            new StreamElementsStep("streamelements.getUserRank", "Get user rank", [User()], async (ctx, p, ct) =>
            {
                ctx.Args["sePointsRank"] = await se.GetUserRankAsync(p["user"], ct).ConfigureAwait(false);
            }),
            new StreamElementsStep("streamelements.resetUserPoints", "Reset user points", [User()], (_, p, ct) => se.ResetUserPointsAsync(p["user"], ct)),
            new StreamElementsStep("streamelements.bulkPoints", "Give points to many users",
                [Text("users", "Users"), Number("amount", "Points", "50"), Choice("mode", "Mode", "Add", "Add", "Set")],
                async (ctx, p, ct) =>
                {
                    var users = p["users"].Split([',', ' ', '\n', '\r', '\t', ';'], StringSplitOptions.RemoveEmptyEntries);
                    var amount = (long)Math.Round(p.GetDouble("amount"));
                    ctx.Args["seBulkCount"] = await se.BulkPointsAsync(users, amount, p["mode"].Equals("Set", StringComparison.OrdinalIgnoreCase), ct)
                        .ConfigureAwait(false);
                }),
            new StreamElementsStep("streamelements.resetPoints", "Reset everyone's points",
                [Choice("context", "Which points", "current", "current", "alltime")], (_, p, ct) => se.ResetPointsAsync(p["context"], ct)),
            new StreamElementsStep("streamelements.getCheerLeaderboard", "Get cheer leaderboard",
                [Number("count", "Cheerers", "5"), GetTipLeaderboardSubAction.Period], async (ctx, p, ct) =>
                {
                    var rows = await se.GetCheerLeaderboardAsync(p.GetInt("count", 5), p["period"], ct).ConfigureAwait(false);
                    GetPointsLeaderboardSubAction.WriteRanks(ctx, "seCheerLeader", rows);
                }),

            // ---- tips ----
            new StreamElementsStep("streamelements.createTip", "Add a tip to StreamElements",
                [Text("user", "Tipper", "%user%"), Number("amount", "Amount", "5"), Text("currency", "Currency", "USD"), Text("message", "Message")],
                async (ctx, p, ct) =>
                {
                    var tip = await se.CreateTipAsync(p["user"], p.GetDouble("amount"), p["currency"], p["message"], true, ct).ConfigureAwait(false);
                    ctx.Args["seTipId"] = tip.Id;
                }),
            new StreamElementsStep("streamelements.getPendingTips", "Get tips waiting for approval", [], async (ctx, _, ct) =>
                GetTipsSubAction.WriteTips(ctx, await se.GetPendingTipsAsync(ct).ConfigureAwait(false))),

            // ---- loyalty store ----
            new StreamElementsStep("streamelements.getStoreItem", "Get store item", [Text("item", "Item")], async (ctx, p, ct) =>
            {
                var wanted = p["item"].Trim();
                var item = (await se.GetStoreItemsAsync(ct).ConfigureAwait(false))
                    .FirstOrDefault(i => i.Id == wanted || i.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                    ?? throw new StreamElementsApiException($"No StreamElements store item named '{wanted}'");
                Item(ctx, item);
            }),
            new StreamElementsStep("streamelements.setStoreItem", "Change store item",
                [Text("item", "Item"), Choice("state", "On/off", "Keep", "Keep", "On", "Off", "Toggle"), Text("cost", "Cost"), Text("stock", "Stock")],
                async (ctx, p, ct) =>
                {
                    var state = p["state"].Trim();
                    var stock = OptionalNumber(p["stock"], "Stock");
                    var item = await se.UpdateStoreItemAsync(p["item"], OnOff(state), state.Equals("Toggle", StringComparison.OrdinalIgnoreCase),
                        OptionalNumber(p["cost"], "Cost"), stock is { } s ? (long)Math.Round(s) : null, ct).ConfigureAwait(false);
                    Item(ctx, item);
                }),
            new StreamElementsStep("streamelements.completeRedemption", "Complete store redemption",
                [Text("redemptionId", "Redemption id", "%redemptionId%")], (_, p, ct) => se.CompleteRedemptionAsync(p["redemptionId"], ct)),
            new StreamElementsStep("streamelements.refundRedemption", "Refund store redemption",
                [Text("redemptionId", "Redemption id", "%redemptionId%")], (_, p, ct) => se.RefundRedemptionAsync(p["redemptionId"], ct)),
            new StreamElementsStep("streamelements.getPendingRedemptions", "Get pending store redemptions", [Number("count", "Redemptions", "5")],
                async (ctx, p, ct) =>
                {
                    var rows = await se.GetRedemptionsAsync(true, p.GetInt("count", 5), ct).ConfigureAwait(false);
                    ctx.Args["seRedemptionCount"] = rows.Count;
                    for (var i = 0; i < rows.Count; i++)
                    {
                        ctx.Args[$"seRedemption{N(i)}Id"] = rows[i].Id;
                        ctx.Args[$"seRedemption{N(i)}Item"] = rows[i].Item;
                        ctx.Args[$"seRedemption{N(i)}User"] = rows[i].UserName;
                        ctx.Args[$"seRedemption{N(i)}Input"] = rows[i].Input;
                    }
                }),

            // ---- contests ----
            new StreamElementsStep("streamelements.createContest", "Create contest",
                [Text("title", "Title"), Text("options", "Options", "Yes, No"), Number("minBet", "Smallest bet", "10"), Number("maxBet", "Biggest bet", "1000"),
                    Number("minutes", "Minutes open", "5")],
                async (ctx, p, ct) =>
                {
                    var contest = await se.CreateContestAsync(p["title"], p["options"].Split(','), (long)p.GetDouble("minBet"), (long)p.GetDouble("maxBet"),
                        p.GetInt("minutes", 5), ct).ConfigureAwait(false);
                    ctx.Args["seContestId"] = contest.Id;
                }),
            new StreamElementsStep("streamelements.contestAction", "Run contest",
                [Choice("action", "Action", "start", "start", "close", "winner", "refund"), Text("winner", "Winning option"),
                    Text("contestId", "Contest id")],
                async (ctx, p, ct) =>
                {
                    var contest = await se.ContestActionAsync(p["action"].Trim().ToLowerInvariant(), p["contestId"], p["winner"], ct).ConfigureAwait(false);
                    ctx.Args["seContestId"] = contest.Id;
                }),
            new StreamElementsStep("streamelements.getContest", "Get contest", [], async (ctx, _, ct) =>
            {
                var contest = await se.GetContestAsync(ct).ConfigureAwait(false);
                ctx.Args["seContestFound"] = contest is not null;
                ctx.Args["seContestId"] = contest?.Id ?? "";
                ctx.Args["seContestTitle"] = contest?.Title ?? "";
                ctx.Args["seContestState"] = contest?.State ?? "";
                ctx.Args["seContestTotal"] = contest?.TotalAmount ?? 0;
                ctx.Args["seContestUsers"] = contest?.TotalUsers ?? 0;
                var options = contest?.Options ?? [];
                ctx.Args["seContestOptionCount"] = options.Count;
                for (var i = 0; i < options.Count; i++)
                {
                    ctx.Args[$"seContestOption{N(i)}Title"] = options[i].Title;
                    ctx.Args[$"seContestOption{N(i)}Total"] = options[i].TotalAmount;
                }
            }),

            // ---- giveaways ----
            new StreamElementsStep("streamelements.giveawayAction", "Run giveaway",
                [Choice("action", "Action", "start", "start", "close", "winner", "complete", "reopen", "refund"), Text("giveawayId", "Giveaway id")],
                async (ctx, p, ct) =>
                {
                    ctx.Args["seGiveawayId"] = await se.GiveawayActionAsync(p["action"].Trim().ToLowerInvariant(), p["giveawayId"], ct).ConfigureAwait(false);
                }),
            new StreamElementsStep("streamelements.getGiveaway", "Get giveaway", [], async (ctx, _, ct) =>
            {
                var giveaway = await se.GetGiveawayAsync(ct).ConfigureAwait(false);
                ctx.Args["seGiveawayFound"] = giveaway is not null;
                ctx.Args["seGiveawayId"] = giveaway?.Id ?? "";
                ctx.Args["seGiveawayTitle"] = giveaway?.Title ?? "";
                ctx.Args["seGiveawayState"] = giveaway?.State ?? "";
                ctx.Args["seGiveawayUsers"] = giveaway?.TotalUsers ?? 0;
            }),

            // ---- song requests ----
            new StreamElementsStep("streamelements.songPlayer", "Play or pause song requests", [Choice("state", "State", "play", "play", "pause")],
                (_, p, ct) => se.SetSongPlayerAsync(!p["state"].Equals("pause", StringComparison.OrdinalIgnoreCase), ct)),
            new StreamElementsStep("streamelements.getSong", "Get song request", [Choice("which", "Which song", "playing", "playing", "next")],
                async (ctx, p, ct) => Song(ctx, p["which"].Equals("next", StringComparison.OrdinalIgnoreCase)
                    ? await se.GetNextSongAsync(ct).ConfigureAwait(false)
                    : await se.GetPlayingSongAsync(ct).ConfigureAwait(false))),
            new StreamElementsStep("streamelements.addSong", "Add song request", [Text("video", "Song", "%rawInput%")], async (ctx, p, ct) =>
                Song(ctx, await se.AddSongAsync(p["video"], ct).ConfigureAwait(false))),
            new StreamElementsStep("streamelements.removeSong", "Remove song request", [Text("songId", "Song id", "%songId%")],
                (_, p, ct) => se.RemoveSongAsync(p["songId"], ct)),
            new StreamElementsStep("streamelements.getSongQueue", "Get song request queue", [Number("count", "Songs", "5")], async (ctx, p, ct) =>
            {
                var queue = await se.GetSongQueueAsync(ct).ConfigureAwait(false);
                ctx.Args["seQueueCount"] = queue.Count;
                for (var i = 0; i < Math.Min(queue.Count, Math.Max(0, p.GetInt("count", 5))); i++)
                {
                    ctx.Args[$"seQueue{N(i)}Title"] = queue[i].Title;
                    ctx.Args[$"seQueue{N(i)}User"] = queue[i].UserName;
                    ctx.Args[$"seQueue{N(i)}Id"] = queue[i].Id;
                }
            }),
        ];
    }
}
