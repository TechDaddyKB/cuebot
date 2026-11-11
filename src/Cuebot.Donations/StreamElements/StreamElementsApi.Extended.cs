using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Cuebot.Donations.StreamElements;

public sealed record StreamElementsBot(bool Enabled, bool Joined, bool Muted, bool Mod, string Name);

public sealed record StreamElementsStoreItem(string Id, string Name, bool Enabled, double Cost, long Stock, long Total, string Type);

public sealed record StreamElementsRedemption(string Id, string Item, string ItemId, string UserName, string Input, bool Completed, string CreatedAt);

public sealed record StreamElementsSong(string Id, string Title, string VideoId, string UserName, double Duration, string Source)
{
    public string Url => VideoId.Length > 0 ? $"https://www.youtube.com/watch?v={VideoId}" : "";
}

public sealed record StreamElementsContestOption(string Id, string Title, string Command, double TotalAmount, int TotalUsers);

public sealed record StreamElementsContest(string Id, string Title, string State, double TotalAmount, int TotalUsers,
    IReadOnlyList<StreamElementsContestOption> Options);

public sealed record StreamElementsGiveaway(string Id, string Title, string State, int TotalUsers, double TotalTickets);

/// <summary>A chatbot timer or command, with its on/off switch.</summary>
public sealed record StreamElementsToggle(string Id, string Name, bool Enabled);

/// <summary>
/// The rest of the documented REST API Cuebot uses (kappa/v2, giveaways on kappa/v3). Paths and bodies follow the
/// public OpenAPI document ("SE API Docs"); where it gives no body schema the shape is noted at the call.
/// </summary>
internal sealed partial class StreamElementsApi
{
    private static readonly JsonElement EmptyObject = JsonDocument.Parse("{}").RootElement.Clone();

    /// <summary>kappa/v3 next to the configured kappa/v2 (giveaways are v3 only).</summary>
    private Uri V3 => new(_base.AbsoluteUri.Replace("/v2/", "/v3/", StringComparison.Ordinal));

    // ---- chatbot ----

    /// <summary><c>GET bot/{channel}</c> → <c>{bot: {enabled, name, joined, muted, mod, ...}}</c>.</summary>
    public async Task<StreamElementsBot> GetBotAsync(string jwt, string channel, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"bot/{Seg(channel)}", jwt, ct).ConfigureAwait(false);
        var bot = json.Child("bot") ?? json;
        return new StreamElementsBot(Flag(bot, "enabled"), Flag(bot, "joined"), Flag(bot, "muted"), Flag(bot, "mod"), bot.Str("name"));
    }

    /// <summary><c>POST bot/{channel}/say {message}</c>: the StreamElements bot writes in chat.</summary>
    public Task SayAsync(string jwt, string channel, string message, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"bot/{Seg(channel)}/say", jwt, ct, new { message });

    /// <summary><c>POST bot/{channel}/mute</c> or <c>/unmute</c> (400 when it already is).</summary>
    public Task SetBotMutedAsync(string jwt, string channel, bool mute, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"bot/{Seg(channel)}/{(mute ? "mute" : "unmute")}", jwt, ct);

    /// <summary><c>GET bot/{channel}/counters/{counter}</c> → <c>{counter, value}</c>.</summary>
    public async Task<long> GetBotCounterAsync(string jwt, string channel, string counter, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"bot/{Seg(channel)}/counters/{Seg(counter)}", jwt, ct).ConfigureAwait(false);
        return (long)json.Number("value");
    }

    /// <summary><c>GET bot/timers/{channel}</c> → <c>[{_id, name, enabled, ...}]</c>.</summary>
    public async Task<IReadOnlyList<JsonElement>> GetTimersAsync(string jwt, string channel, CancellationToken ct) =>
        Rows(await SendAsync(HttpMethod.Get, $"bot/timers/{Seg(channel)}", jwt, ct).ConfigureAwait(false));

    /// <summary><c>GET bot/commands/{channel}</c> → <c>[{_id, command, enabled, ...}]</c>.</summary>
    public async Task<IReadOnlyList<JsonElement>> GetCommandsAsync(string jwt, string channel, CancellationToken ct) =>
        Rows(await SendAsync(HttpMethod.Get, $"bot/commands/{Seg(channel)}", jwt, ct).ConfigureAwait(false));

    /// <summary>
    /// Switches a chatbot timer on or off (null toggles): the timer as listed, with <c>enabled</c> changed, is sent back
    /// with <c>PUT bot/timers/{channel}/{timerId}</c> (the spec gives the body no schema; this is the GET shape).
    /// </summary>
    public async Task<StreamElementsToggle> SetTimerEnabledAsync(string jwt, string channel, string nameOrId, bool? enabled, CancellationToken ct)
    {
        var timer = Find(await GetTimersAsync(jwt, channel, ct).ConfigureAwait(false), nameOrId, "name")
                    ?? throw new StreamElementsApiException($"No StreamElements timer named '{nameOrId}'");
        return await PutToggleAsync(jwt, $"bot/timers/{Seg(channel)}", timer, "name", enabled, ct).ConfigureAwait(false);
    }

    /// <summary>Switches a chatbot command on or off (null toggles), like <see cref="SetTimerEnabledAsync"/>.</summary>
    public async Task<StreamElementsToggle> SetCommandEnabledAsync(string jwt, string channel, string nameOrId, bool? enabled, CancellationToken ct)
    {
        var command = Find(await GetCommandsAsync(jwt, channel, ct).ConfigureAwait(false), nameOrId.TrimStart('!'), "command")
                      ?? throw new StreamElementsApiException($"No StreamElements command named '{nameOrId}'");
        return await PutToggleAsync(jwt, $"bot/commands/{Seg(channel)}", command, "command", enabled, ct).ConfigureAwait(false);
    }

    private async Task<StreamElementsToggle> PutToggleAsync(string jwt, string collection, JsonElement row, string nameField, bool? enabled,
        CancellationToken ct)
    {
        var id = row.First("_id", "commandId");
        var now = enabled ?? !Flag(row, "enabled");
        var body = Writable(row);
        body["enabled"] = now;
        await SendAsync(HttpMethod.Put, $"{collection}/{Seg(id)}", jwt, ct, body).ConfigureAwait(false);
        return new StreamElementsToggle(id, row.Str(nameField), now);
    }

    // ---- overlays ----

    /// <summary><c>PUT overlays/{channel}/action/{action}</c>; the spec allows reload, mute, unmute, pause and play.</summary>
    public Task OverlayActionAsync(string jwt, string channel, string action, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"overlays/{Seg(channel)}/action/{Seg(action)}", jwt, ct);

    // ---- session data ----

    /// <summary><c>GET sessions/{channel}</c> → <c>{data: {"tip-session": {...}, ...}, provider, lastReset, channel}</c>.</summary>
    public Task<JsonElement> GetSessionAsync(string jwt, string channel, CancellationToken ct) =>
        SendAsync(HttpMethod.Get, $"sessions/{Seg(channel)}", jwt, ct);

    /// <summary>
    /// Changes one session value (e.g. <c>tip-goal</c> / <c>amount</c>): the session is read, the value changed and the
    /// whole session written back with <c>PUT sessions/{channel}</c>, in the shape the GET returned (the spec documents
    /// no body; sending the full document cannot clear other keys).
    /// </summary>
    public async Task<JsonElement> SetSessionValueAsync(string jwt, string channel, string key, string field, JsonNode? value, CancellationToken ct)
    {
        var current = await GetSessionAsync(jwt, channel, ct).ConfigureAwait(false);
        var body = JsonNode.Parse(current.GetRawText()) as JsonObject ?? [];
        var data = body["data"] as JsonObject ?? body;
        if (data[key] is not JsonObject entry)
        {
            entry = [];
            data[key] = entry;
        }
        entry[field] = value;
        return await SendAsync(HttpMethod.Put, $"sessions/{Seg(channel)}", jwt, ct, body).ConfigureAwait(false);
    }

    /// <summary><c>PUT sessions/{channel}/reset</c>: starts a new session (session counters back to 0).</summary>
    public Task ResetSessionAsync(string jwt, string channel, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"sessions/{Seg(channel)}/reset", jwt, ct);

    // ---- points ----

    /// <summary><c>GET points/{channel}/{user}/rank</c> → <c>{rank}</c> (response not documented; 0 when missing).</summary>
    public async Task<int> GetUserRankAsync(string jwt, string channel, string user, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"points/{Seg(channel)}/{Seg(user)}/rank", jwt, ct).ConfigureAwait(false);
        return (int)json.Number("rank");
    }

    /// <summary><c>DELETE points/{channel}/{user}</c>: the viewer's current points go back to 0.</summary>
    public Task ResetUserPointsAsync(string jwt, string channel, string user, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"points/{Seg(channel)}/{Seg(user)}", jwt, ct);

    /// <summary><c>PUT points/{channel} {users: [{username, current}], mode: "add"|"set"}</c>: many viewers at once.</summary>
    public Task BulkPointsAsync(string jwt, string channel, IEnumerable<string> users, long amount, bool set, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"points/{Seg(channel)}", jwt, ct, new
        {
            users = users.Select(u => new { username = u, current = amount }).ToArray(),
            mode = set ? "set" : "add",
        });

    /// <summary>
    /// Points leaderboards: <c>top</c> (current points), <c>alltime</c> or <c>watchtime</c>, each
    /// <c>GET points/{channel}/{board}?limit&amp;offset</c>. Rows are read from <c>users</c> (or a bare array); the
    /// value from <c>points</c>, <c>pointsAlltime</c>, <c>minutes</c> or <c>watchtime</c>, whichever is there.
    /// </summary>
    public async Task<IReadOnlyList<StreamElementsRank>> GetPointsBoardAsync(string jwt, string channel, string board, int limit, CancellationToken ct)
    {
        board = board.Trim().ToLowerInvariant() switch { "alltime" or "all time" => "alltime", "watchtime" or "watch time" => "watchtime", _ => "top" };
        var json = await SendAsync(HttpMethod.Get, $"points/{Seg(channel)}/{board}?limit={Clamp(limit)}&offset=0", jwt, ct).ConfigureAwait(false);
        var rows = json.ValueKind == JsonValueKind.Array ? json.EnumerateArray() : json.Items("users");
        return rows.Where(u => u.ValueKind == JsonValueKind.Object)
            .Select(u => new StreamElementsRank(u.Str("username"), FirstNumber(u, board switch
            {
                "alltime" => ["pointsAlltime", "points"],
                "watchtime" => ["minutes", "watchtime", "watchTime", "points"],
                _ => ["points"],
            }))).ToArray();
    }

    /// <summary><c>DELETE points/{channel}/reset/{context}</c>: <c>current</c> or <c>alltime</c> points for everyone.</summary>
    public Task ResetPointsAsync(string jwt, string channel, string context, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"points/{Seg(channel)}/reset/{Seg(context)}", jwt, ct);

    // ---- loyalty store ----

    /// <summary><c>GET store/{channel}/items?limit&amp;offset</c> → LoyaltyItem array.</summary>
    public async Task<IReadOnlyList<JsonElement>> GetStoreItemsAsync(string jwt, string channel, CancellationToken ct) =>
        Rows(await SendAsync(HttpMethod.Get, $"store/{Seg(channel)}/items?limit=100&offset=0", jwt, ct).ConfigureAwait(false));

    internal static StreamElementsStoreItem StoreItem(JsonElement item)
    {
        var quantity = item.Child("quantity") ?? default;
        return new StreamElementsStoreItem(item.Str("_id"), item.Str("name"), Flag(item, "enabled"), item.Number("cost"),
            quantity.ValueKind == JsonValueKind.Object ? (long)quantity.Number("current") : -1,
            quantity.ValueKind == JsonValueKind.Object ? (long)quantity.Number("total") : -1, item.Str("type"));
    }

    /// <summary>
    /// Changes a store item found by name or id: on/off (null keeps it), cost and stock (null keeps them). The item as
    /// listed, with those fields changed, is sent back with <c>PUT store/{channel}/items/{itemId}</c> (the spec requires
    /// <c>name</c> and <c>description</c>, which the listed item carries).
    /// </summary>
    public async Task<StreamElementsStoreItem> UpdateStoreItemAsync(string jwt, string channel, string nameOrId, bool? enabled, bool toggle,
        double? cost, long? stock, CancellationToken ct)
    {
        var item = Find(await GetStoreItemsAsync(jwt, channel, ct).ConfigureAwait(false), nameOrId, "name")
                   ?? throw new StreamElementsApiException($"No StreamElements store item named '{nameOrId}'");
        var body = Writable(item);
        if (toggle) body["enabled"] = !Flag(item, "enabled");
        else if (enabled is { } on) body["enabled"] = on;
        if (cost is { } c) body["cost"] = c;
        if (stock is { } s)
        {
            var quantity = body["quantity"] as JsonObject ?? [];
            quantity["current"] = s;
            if (quantity["total"] is JsonValue total && total.TryGetValue<double>(out var t) && t >= 0 && t < s) quantity["total"] = s;
            body["quantity"] = quantity;
        }
        var json = await SendAsync(HttpMethod.Put, $"store/{Seg(channel)}/items/{Seg(item.Str("_id"))}", jwt, ct, body).ConfigureAwait(false);
        return StoreItem(json.Has("_id") ? json : JsonSerializer.SerializeToElement(body));
    }

    /// <summary>
    /// <c>GET store/{channel}/redemptions?limit&amp;offset&amp;pending</c> (response not documented: <c>docs</c> or a bare
    /// array of redemptions with <c>_id</c>, <c>item</c>, <c>redeemer</c>, <c>input</c>, <c>completed</c> are read leniently).
    /// </summary>
    public async Task<IReadOnlyList<StreamElementsRedemption>> GetRedemptionsAsync(string jwt, string channel, bool pendingOnly, int limit,
        CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get,
            $"store/{Seg(channel)}/redemptions?limit={Clamp(limit)}&offset=0&pending={(pendingOnly ? "true" : "false")}", jwt, ct).ConfigureAwait(false);
        return Rows(json).Select(Redemption).ToArray();
    }

    internal static StreamElementsRedemption Redemption(JsonElement r)
    {
        var item = r.Child("item") is { ValueKind: JsonValueKind.Object } i ? i : default;
        var redeemer = r.Child("redeemer") is { ValueKind: JsonValueKind.Object } u ? u : default;
        var input = r.Child("input") is { ValueKind: JsonValueKind.Array } inputs
            ? string.Join(", ", inputs.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString() : x.GetRawText()))
            : r.First("input", "userInput", "message");
        return new StreamElementsRedemption(r.Str("_id"),
            item.ValueKind == JsonValueKind.Object ? item.Str("name") : r.First("itemName", "item"),
            item.ValueKind == JsonValueKind.Object ? item.Str("_id") : r.Str("itemId"),
            redeemer.ValueKind == JsonValueKind.Object ? redeemer.First("username", "displayName") : r.First("redeemer", "username"),
            input, Flag(r, "completed"), r.Str("createdAt"));
    }

    /// <summary><c>PUT store/{channel}/redemptions/{redemptionId} {completed: true}</c> (body not documented).</summary>
    public Task CompleteRedemptionAsync(string jwt, string channel, string redemptionId, CancellationToken ct) =>
        SendAsync(HttpMethod.Put, $"store/{Seg(channel)}/redemptions/{Seg(redemptionId)}", jwt, ct, new { completed = true });

    /// <summary><c>DELETE store/{channel}/redemptions/{redemptionId}</c>: cancels it and gives the points back.</summary>
    public Task RefundRedemptionAsync(string jwt, string channel, string redemptionId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"store/{Seg(channel)}/redemptions/{Seg(redemptionId)}", jwt, ct);

    // ---- tips and activities ----

    /// <summary>
    /// <c>POST tips/{channel}</c> with NewTip <c>{user: {username, email}, provider, message, amount, currency, imported}</c>:
    /// records a tip (e.g. one paid elsewhere) so goals and leaderboards count it.
    /// </summary>
    public async Task<StreamElementsTip> CreateTipAsync(string jwt, string channel, string user, double amount, string currency, string message,
        bool imported, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, $"tips/{Seg(channel)}", jwt, ct, new
        {
            user = new { username = user, email = "" },
            provider = "cuebot",
            message,
            amount,
            currency = currency.Trim().ToUpperInvariant(),
            imported,
        }).ConfigureAwait(false);
        return Tip(json);
    }

    /// <summary><c>GET tips/{channel}/moderation</c> → <c>{recent: [...], pending: [...]}</c> (tip documents).</summary>
    public async Task<IReadOnlyList<StreamElementsTip>> GetPendingTipsAsync(string jwt, string channel, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"tips/{Seg(channel)}/moderation", jwt, ct).ConfigureAwait(false);
        return json.Items("pending").Select(Tip).ToArray();
    }

    /// <summary>
    /// <c>GET activities/{channel}?after&amp;before&amp;limit&amp;types&amp;origin</c> → activity array (oldest first is not
    /// promised, so callers sort). The spec lists every query parameter as required; the minimums are sent as 0.
    /// </summary>
    public async Task<IReadOnlyList<JsonElement>> GetActivitiesAsync(string jwt, string channel, DateTimeOffset after, IReadOnlyList<string> types,
        int limit, CancellationToken ct)
    {
        var query = $"after={Uri.EscapeDataString(after.UtcDateTime.ToString("o", CultureInfo.InvariantCulture))}"
                    + $"&before={Uri.EscapeDataString(DateTimeOffset.UtcNow.UtcDateTime.ToString("o", CultureInfo.InvariantCulture))}"
                    + $"&limit={Clamp(limit)}&mincheer=0&minhost=0&minsub=0&mintip=0&origin=feed"
                    + string.Concat(types.Select(t => "&types=" + Uri.EscapeDataString(t)));
        return Rows(await SendAsync(HttpMethod.Get, $"activities/{Seg(channel)}?{query}", jwt, ct).ConfigureAwait(false));
    }

    /// <summary><c>POST activities/{channel}/{activityId}/replay</c> → <c>{success}</c>: StreamElements sends it again.</summary>
    public async Task<bool> ReplayActivityAsync(string jwt, string channel, string activityId, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, $"activities/{Seg(channel)}/{Seg(activityId)}/replay", jwt, ct).ConfigureAwait(false);
        return json.Child("success") is not { ValueKind: JsonValueKind.False };
    }

    /// <summary>
    /// <c>GET activities/{channel}/top?type=tip|cheer&amp;period=alltime|months|weeks|session&amp;limit&amp;offset</c> →
    /// <c>[{username, total}]</c>.
    /// </summary>
    public async Task<IReadOnlyList<StreamElementsRank>> GetActivityLeaderboardAsync(string jwt, string channel, string type, string period, int limit,
        CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get,
            $"activities/{Seg(channel)}/top?type={Seg(type)}&period={Seg(Period(period))}&limit={Clamp(limit)}&offset=0", jwt, ct).ConfigureAwait(false);
        return Rows(json).Select(r => new StreamElementsRank(r.First("username", "_id", "name"), Math.Round(r.Number("total"), 2))).ToArray();
    }

    internal static string Period(string period) => period.Trim().ToLowerInvariant() switch
    {
        "session" or "stream" => "session",
        "week" or "weeks" or "weekly" => "weeks",
        "month" or "months" or "monthly" => "months",
        _ => "alltime",
    };

    // ---- contests ----

    /// <summary><c>GET contests/{channel}</c> → <c>{active, contests: [...]}</c>; the active one (or the newest).</summary>
    public async Task<StreamElementsContest?> GetContestAsync(string jwt, string channel, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"contests/{Seg(channel)}", jwt, ct).ConfigureAwait(false);
        if (json.Child("active") is { ValueKind: JsonValueKind.Object } active) return Contest(active);
        return json.Items("contests").Select(Contest).FirstOrDefault();
    }

    /// <summary>
    /// <c>POST contests/{channel}</c>. The spec gives no body schema; the Contest document's own fields are sent:
    /// <c>{title, minBet, maxBet, duration, options: [{title, command}]}</c>.
    /// </summary>
    public async Task<StreamElementsContest> CreateContestAsync(string jwt, string channel, string title, IReadOnlyList<string> options, long minBet,
        long maxBet, int durationMinutes, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Post, $"contests/{Seg(channel)}", jwt, ct, new
        {
            title,
            minBet,
            maxBet,
            duration = durationMinutes,
            options = options.Select((o, i) => new { title = o, command = (i + 1).ToString(CultureInfo.InvariantCulture) }).ToArray(),
        }).ConfigureAwait(false);
        return Contest(json);
    }

    /// <summary>start (PUT), close (DELETE), refund (DELETE) or winner (PUT with <c>{winnerId}</c>, an option id).</summary>
    public Task ContestActionAsync(string jwt, string channel, string contestId, string action, string winnerOptionId, CancellationToken ct) =>
        action switch
        {
            "start" => SendAsync(HttpMethod.Put, $"contests/{Seg(channel)}/{Seg(contestId)}/start", jwt, ct),
            "close" => SendAsync(HttpMethod.Delete, $"contests/{Seg(channel)}/{Seg(contestId)}/close", jwt, ct),
            "refund" => SendAsync(HttpMethod.Delete, $"contests/{Seg(channel)}/{Seg(contestId)}/refund", jwt, ct),
            "winner" => SendAsync(HttpMethod.Put, $"contests/{Seg(channel)}/{Seg(contestId)}/winner", jwt, ct, new { winnerId = winnerOptionId }),
            _ => throw new ArgumentException($"Unknown contest action '{action}' (start, close, winner or refund)"),
        };

    internal static StreamElementsContest Contest(JsonElement c) => new(c.Str("_id"), c.Str("title"), c.Str("state"), c.Number("totalAmount"),
        (int)c.Number("totalUsers"),
        c.Items("options").Where(o => o.ValueKind == JsonValueKind.Object)
            .Select(o => new StreamElementsContestOption(o.Str("_id"), o.Str("title"), o.Str("command"), o.Number("totalAmount"), (int)o.Number("totalUsers")))
            .ToArray());

    // ---- giveaways (kappa/v3) ----

    /// <summary><c>GET giveaways/{channel}</c> (v3) → <c>{active, giveaways: [...]}</c>; the active one (or the newest).</summary>
    public async Task<StreamElementsGiveaway?> GetGiveawayAsync(string jwt, string channel, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"giveaways/{Seg(channel)}", jwt, ct, version: 3).ConfigureAwait(false);
        if (json.Child("active") is { ValueKind: JsonValueKind.Object } active) return Giveaway(active);
        return json.Items("giveaways").Select(Giveaway).FirstOrDefault();
    }

    /// <summary>start/winner/complete/reopen (PUT) or close/refund (DELETE) on <c>giveaways/{channel}/{id}/…</c> (v3).</summary>
    public Task GiveawayActionAsync(string jwt, string channel, string giveawayId, string action, CancellationToken ct)
    {
        var path = $"giveaways/{Seg(channel)}/{Seg(giveawayId)}/{action}";
        return action switch
        {
            "start" or "winner" or "complete" or "reopen" => SendAsync(HttpMethod.Put, path, jwt, ct, version: 3),
            "close" or "refund" => SendAsync(HttpMethod.Delete, path, jwt, ct, version: 3),
            _ => throw new ArgumentException($"Unknown giveaway action '{action}' (start, close, winner, complete, reopen or refund)"),
        };
    }

    internal static StreamElementsGiveaway Giveaway(JsonElement g) =>
        new(g.Str("_id"), g.First("title", "name"), g.Str("state"), (int)g.Number("totalUsers"), g.Number("totalAmount"));

    // ---- song requests ----

    /// <summary><c>POST songrequest/{channel}/player/{state}</c>, state <c>play</c> or <c>pause</c>.</summary>
    public Task SetSongPlayerAsync(string jwt, string channel, bool play, CancellationToken ct) =>
        SendAsync(HttpMethod.Post, $"songrequest/{Seg(channel)}/player/{(play ? "play" : "pause")}", jwt, ct);

    /// <summary><c>GET songrequest/{channel}/playing</c> → Song (null when nothing plays).</summary>
    public async Task<StreamElementsSong?> GetPlayingSongAsync(string jwt, string channel, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"songrequest/{Seg(channel)}/playing", jwt, ct).ConfigureAwait(false);
        return json.ValueKind == JsonValueKind.Object && json.Has("_id") ? Song(json) : null;
    }

    /// <summary><c>GET songrequest/{channel}/next</c> → <c>{song}</c> (null when the queue is empty).</summary>
    public async Task<StreamElementsSong?> GetNextSongAsync(string jwt, string channel, CancellationToken ct)
    {
        var json = await SendAsync(HttpMethod.Get, $"songrequest/{Seg(channel)}/next", jwt, ct).ConfigureAwait(false);
        return json.Child("song") is { ValueKind: JsonValueKind.Object } song ? Song(song) : null;
    }

    /// <summary><c>GET songrequest/{channel}/queue</c> → Song array.</summary>
    public async Task<IReadOnlyList<StreamElementsSong>> GetSongQueueAsync(string jwt, string channel, CancellationToken ct) =>
        Rows(await SendAsync(HttpMethod.Get, $"songrequest/{Seg(channel)}/queue", jwt, ct).ConfigureAwait(false)).Select(Song).ToArray();

    /// <summary><c>POST songrequest/{channel}/queue {video}</c> (a YouTube link, id or search) → the queued Song.</summary>
    public async Task<StreamElementsSong> AddSongAsync(string jwt, string channel, string video, CancellationToken ct) =>
        Song(await SendAsync(HttpMethod.Post, $"songrequest/{Seg(channel)}/queue", jwt, ct, new { video }).ConfigureAwait(false));

    /// <summary><c>DELETE songrequest/{channel}/queue/{song}</c>.</summary>
    public Task RemoveSongAsync(string jwt, string channel, string songId, CancellationToken ct) =>
        SendAsync(HttpMethod.Delete, $"songrequest/{Seg(channel)}/queue/{Seg(songId)}", jwt, ct);

    internal static StreamElementsSong Song(JsonElement s)
    {
        var user = s.Child("user") is { ValueKind: JsonValueKind.Object } u ? u.First("displayName", "username") : "";
        return new StreamElementsSong(s.Str("_id"), s.Str("title"), s.Str("videoId"), user, s.Number("duration"), s.Str("source"));
    }

    // ---- helpers ----

    private static IReadOnlyList<JsonElement> Rows(JsonElement json) =>
        (json.ValueKind == JsonValueKind.Array ? json.EnumerateArray()
            : json.Child("docs") is { ValueKind: JsonValueKind.Array } ? json.Items("docs")
            : json.Items("data"))
        .Where(r => r.ValueKind == JsonValueKind.Object).ToArray();

    /// <summary>The row whose id equals <paramref name="nameOrId"/>, else the one whose name matches (ignoring case).</summary>
    private static JsonElement? Find(IReadOnlyList<JsonElement> rows, string nameOrId, string nameField)
    {
        var wanted = nameOrId.Trim();
        if (wanted.Length == 0) throw new ArgumentException("A name is required");
        foreach (var row in rows)
            if (row.First("_id", "commandId") == wanted) return row;
        foreach (var row in rows)
            if (string.Equals(row.Str(nameField).TrimStart('!'), wanted, StringComparison.OrdinalIgnoreCase)) return row;
        return null;
    }

    /// <summary>A listed document as a body to send back: server-owned fields removed.</summary>
    private static JsonObject Writable(JsonElement row)
    {
        var body = JsonNode.Parse(row.GetRawText()) as JsonObject ?? [];
        foreach (var key in new[] { "_id", "__v", "createdAt", "updatedAt" }) body.Remove(key);
        return body;
    }

    private static bool Flag(JsonElement element, string name) => element.Child(name) is { ValueKind: JsonValueKind.True };

    private static double FirstNumber(JsonElement element, IEnumerable<string> names)
    {
        foreach (var name in names)
            if (element.Has(name)) return element.Number(name);
        return 0;
    }
}
