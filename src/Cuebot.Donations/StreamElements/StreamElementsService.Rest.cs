using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Overlays.Widgets;

namespace Cuebot.Donations.StreamElements;

/// <summary>The REST API as used by sub-actions, script functions and the app (all need the JWT).</summary>
public sealed partial class StreamElementsService
{
    /// <summary>The token's channel id, looked up once and cached in donations.json.</summary>
    public async Task<string> GetChannelIdAsync(CancellationToken ct = default)
    {
        if (Settings.ChannelId.Length > 0) return Settings.ChannelId;
        if (Settings.JwtToken.Trim().Length == 0)
            throw new StreamElementsApiException(UsesOverlayToken
                ? "This needs the StreamElements JWT token; the Overlay Token only receives events"
                : "Enter your StreamElements JWT token first");
        var channel = await _api.GetMyChannelAsync(Settings.JwtToken, ct).ConfigureAwait(false);
        Settings.ChannelId = channel.Id;
        Settings.ChannelName = channel.DisplayName;
        Settings.ChannelProvider = channel.Provider;
        SaveSettings();
        return channel.Id;
    }

    /// <summary>Forgets the cached channel (after the token changes).</summary>
    public void ResetChannel()
    {
        Settings.ChannelId = "";
        Settings.ChannelName = "";
        Settings.ChannelProvider = "";
        SaveSettings();
    }

    private async Task<string> Channel(CancellationToken ct) => await GetChannelIdAsync(ct).ConfigureAwait(false);

    private string Jwt => Settings.JwtToken;

    // ---- points ----

    public async Task<StreamElementsPoints> GetUserPointsAsync(string user, CancellationToken ct = default) =>
        await _api.GetUserPointsAsync(Jwt, await Channel(ct).ConfigureAwait(false), User(user), ct).ConfigureAwait(false);

    public async Task<long> AddUserPointsAsync(string user, long amount, CancellationToken ct = default) =>
        await _api.AddUserPointsAsync(Jwt, await Channel(ct).ConfigureAwait(false), User(user), amount, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<StreamElementsRank>> GetPointsLeaderboardAsync(int limit, CancellationToken ct = default) =>
        await _api.GetPointsLeaderboardAsync(Jwt, await Channel(ct).ConfigureAwait(false), limit, ct).ConfigureAwait(false);

    /// <summary>Points leaderboard by <c>top</c> (current), <c>alltime</c> or <c>watchtime</c>.</summary>
    public async Task<IReadOnlyList<StreamElementsRank>> GetPointsBoardAsync(string board, int limit, CancellationToken ct = default) =>
        board.Trim().Length == 0 || board.Trim().Equals("top", StringComparison.OrdinalIgnoreCase) || board.Trim().Equals("current", StringComparison.OrdinalIgnoreCase)
            ? await GetPointsLeaderboardAsync(limit, ct).ConfigureAwait(false)
            : await _api.GetPointsBoardAsync(Jwt, await Channel(ct).ConfigureAwait(false), board, limit, ct).ConfigureAwait(false);

    public async Task<int> GetUserRankAsync(string user, CancellationToken ct = default) =>
        await _api.GetUserRankAsync(Jwt, await Channel(ct).ConfigureAwait(false), User(user), ct).ConfigureAwait(false);

    public async Task ResetUserPointsAsync(string user, CancellationToken ct = default) =>
        await _api.ResetUserPointsAsync(Jwt, await Channel(ct).ConfigureAwait(false), User(user), ct).ConfigureAwait(false);

    /// <summary>Adds (or sets) the same amount for many viewers in one request.</summary>
    public async Task<int> BulkPointsAsync(IEnumerable<string> users, long amount, bool set, CancellationToken ct = default)
    {
        var names = users.Select(u => u.Trim().TrimStart('@')).Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (names.Length == 0) throw new ArgumentException("At least one user name is required");
        await _api.BulkPointsAsync(Jwt, await Channel(ct).ConfigureAwait(false), names, amount, set, ct).ConfigureAwait(false);
        return names.Length;
    }

    /// <summary>Resets everyone's <c>current</c> or <c>alltime</c> points.</summary>
    public async Task ResetPointsAsync(string context, CancellationToken ct = default)
    {
        context = context.Trim().ToLowerInvariant() switch { "alltime" or "all time" => "alltime", "current" or "" => "current", var other => other };
        await _api.ResetPointsAsync(Jwt, await Channel(ct).ConfigureAwait(false), context, ct).ConfigureAwait(false);
    }

    // ---- tips and activities ----

    public async Task<IReadOnlyList<StreamElementsTip>> GetTipsAsync(int limit, CancellationToken ct = default) =>
        await _api.GetTipsAsync(Jwt, await Channel(ct).ConfigureAwait(false), limit, ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<StreamElementsTip>> GetPendingTipsAsync(CancellationToken ct = default) =>
        await _api.GetPendingTipsAsync(Jwt, await Channel(ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    /// <summary>Top tippers; with a period (session, weeks, months) from the activity leaderboard, else all time.</summary>
    public async Task<IReadOnlyList<StreamElementsRank>> GetTipLeaderboardAsync(int limit, CancellationToken ct = default) =>
        await GetTipLeaderboardAsync(limit, "", ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<StreamElementsRank>> GetTipLeaderboardAsync(int limit, string period, CancellationToken ct = default) =>
        StreamElementsApi.Period(period) == "alltime"
            ? await _api.GetTipLeaderboardAsync(Jwt, await Channel(ct).ConfigureAwait(false), limit, ct).ConfigureAwait(false)
            : await _api.GetActivityLeaderboardAsync(Jwt, await Channel(ct).ConfigureAwait(false), "tip", period, limit, ct).ConfigureAwait(false);

    /// <summary>Top cheerers for a period (alltime, months, weeks, session).</summary>
    public async Task<IReadOnlyList<StreamElementsRank>> GetCheerLeaderboardAsync(int limit, string period, CancellationToken ct = default) =>
        await _api.GetActivityLeaderboardAsync(Jwt, await Channel(ct).ConfigureAwait(false), "cheer", period, limit, ct).ConfigureAwait(false);

    /// <summary>Records a tip in StreamElements (e.g. paid elsewhere) so goals and leaderboards count it.</summary>
    public async Task<StreamElementsTip> CreateTipAsync(string user, double amount, string currency, string message, bool imported = true,
        CancellationToken ct = default)
    {
        if (amount <= 0) throw new ArgumentException("The amount must be more than 0");
        if (currency.Trim().Length != 3) throw new ArgumentException("The currency must be a 3-letter code, like USD");
        return await _api.CreateTipAsync(Jwt, await Channel(ct).ConfigureAwait(false), User(user), amount, currency, message, imported, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Asks StreamElements to send the newest activity of a type again (dashboard replay), so the whole real pipe can be
    /// tested. Returns the replayed activity id, or "" when there is none.
    /// </summary>
    public async Task<string> ReplayLatestAsync(string type = "tip", CancellationToken ct = default)
    {
        var channel = await Channel(ct).ConfigureAwait(false);
        var recent = await _api.GetActivitiesAsync(Jwt, channel, DateTimeOffset.UtcNow.AddDays(-30), [type], 1, ct).ConfigureAwait(false);
        var latest = recent.OrderByDescending(a => a.Str("createdAt"), StringComparer.Ordinal).FirstOrDefault();
        var id = latest.ValueKind == JsonValueKind.Object ? latest.First("_id", "activityId") : "";
        if (id.Length == 0) return "";
        return await _api.ReplayActivityAsync(Jwt, channel, id, ct).ConfigureAwait(false) ? id : "";
    }

    // ---- chatbot ----

    public async Task<StreamElementsBot> GetBotAsync(CancellationToken ct = default) =>
        await _api.GetBotAsync(Jwt, await Channel(ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    public async Task SayAsync(string message, CancellationToken ct = default)
    {
        message = message.Trim();
        if (message.Length == 0) throw new ArgumentException("A message is required");
        await _api.SayAsync(Jwt, await Channel(ct).ConfigureAwait(false), message, ct).ConfigureAwait(false);
    }

    public async Task SetBotMutedAsync(bool mute, CancellationToken ct = default) =>
        await _api.SetBotMutedAsync(Jwt, await Channel(ct).ConfigureAwait(false), mute, ct).ConfigureAwait(false);

    public async Task<long> GetBotCounterAsync(string counter, CancellationToken ct = default) =>
        await _api.GetBotCounterAsync(Jwt, await Channel(ct).ConfigureAwait(false), Required(counter, "A counter name"), ct).ConfigureAwait(false);

    /// <summary>SE_API.counters.get for widgets: the chatbot counter when a JWT is set, else null.</summary>
    async Task<double?> IStreamElementsOverlaySource.GetBotCounterAsync(string name, CancellationToken ct)
    {
        if (Settings.JwtToken.Trim().Length == 0 || name.Trim().Length == 0) return null;
        try
        {
            return await GetBotCounterAsync(name, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or StreamElementsApiException)
        {
            return null;
        }
    }

    public async Task<StreamElementsToggle> SetTimerEnabledAsync(string nameOrId, bool? enabled, CancellationToken ct = default) =>
        await _api.SetTimerEnabledAsync(Jwt, await Channel(ct).ConfigureAwait(false), nameOrId, enabled, ct).ConfigureAwait(false);

    public async Task<StreamElementsToggle> SetCommandEnabledAsync(string nameOrId, bool? enabled, CancellationToken ct = default) =>
        await _api.SetCommandEnabledAsync(Jwt, await Channel(ct).ConfigureAwait(false), nameOrId, enabled, ct).ConfigureAwait(false);

    // ---- overlays ----

    public bool HasToken => Settings.JwtToken.Trim().Length > 0;

    public async Task<IReadOnlyList<StreamElementsOverlaySummary>> ListOverlaysAsync(CancellationToken ct) =>
        StreamElementsOverlayImport.ParseList(await _api.GetOverlaysAsync(Jwt, await Channel(ct).ConfigureAwait(false), ct).ConfigureAwait(false));

    public async Task<JsonObject> GetOverlayAsync(string id, CancellationToken ct) =>
        await _api.GetOverlayAsync(Jwt, await Channel(ct).ConfigureAwait(false), id, ct).ConfigureAwait(false);

    /// <summary>The overlay actions the REST API accepts.</summary>
    public static readonly IReadOnlyList<string> OverlayActions = ["pause", "play", "mute", "unmute", "reload"];

    /// <summary>Pauses/plays the alert queue, mutes/unmutes or reloads every StreamElements overlay.</summary>
    public async Task OverlayActionAsync(string action, CancellationToken ct = default)
    {
        action = action.Trim().ToLowerInvariant() switch { "unpause" or "resume" => "play", var a => a };
        if (!OverlayActions.Contains(action)) throw new ArgumentException($"Unknown overlay action '{action}' (pause, play, mute, unmute or reload)");
        await _api.OverlayActionAsync(Jwt, await Channel(ct).ConfigureAwait(false), action, ct).ConfigureAwait(false);
    }

    // ---- session data ----

    /// <summary>Changes one session value (e.g. <c>tip-goal</c> / <c>amount</c>) and updates the matching global.</summary>
    public async Task SetSessionValueAsync(string key, string field, string value, CancellationToken ct = default)
    {
        key = Required(key, "A session key, like tip-goal");
        field = Required(field, "A field, like amount");
        JsonNode? node = double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)
            ? JsonValue.Create(n)
            : bool.TryParse(value, out var b) ? JsonValue.Create(b) : JsonValue.Create(value);
        var updated = await _api.SetSessionValueAsync(Jwt, await Channel(ct).ConfigureAwait(false), key, field, node, ct).ConfigureAwait(false);
        ApplySession(updated);
        _globals?.SetValue(SessionVariable(key, field), node is JsonValue v && v.TryGetValue<double>(out var d) ? d : value, false);
    }

    public async Task ResetSessionAsync(CancellationToken ct = default) =>
        await _api.ResetSessionAsync(Jwt, await Channel(ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    // ---- loyalty store ----

    public async Task<IReadOnlyList<StreamElementsStoreItem>> GetStoreItemsAsync(CancellationToken ct = default) =>
        (await _api.GetStoreItemsAsync(Jwt, await Channel(ct).ConfigureAwait(false), ct).ConfigureAwait(false)).Select(StreamElementsApi.StoreItem).ToArray();

    public async Task<StreamElementsStoreItem> UpdateStoreItemAsync(string nameOrId, bool? enabled, bool toggle, double? cost, long? stock,
        CancellationToken ct = default) =>
        await _api.UpdateStoreItemAsync(Jwt, await Channel(ct).ConfigureAwait(false), Required(nameOrId, "An item name"), enabled, toggle, cost, stock, ct)
            .ConfigureAwait(false);

    public async Task<IReadOnlyList<StreamElementsRedemption>> GetRedemptionsAsync(bool pendingOnly, int limit, CancellationToken ct = default) =>
        await _api.GetRedemptionsAsync(Jwt, await Channel(ct).ConfigureAwait(false), pendingOnly, limit, ct).ConfigureAwait(false);

    public async Task CompleteRedemptionAsync(string redemptionId, CancellationToken ct = default) =>
        await _api.CompleteRedemptionAsync(Jwt, await Channel(ct).ConfigureAwait(false), Required(redemptionId, "A redemption id"), ct).ConfigureAwait(false);

    public async Task RefundRedemptionAsync(string redemptionId, CancellationToken ct = default) =>
        await _api.RefundRedemptionAsync(Jwt, await Channel(ct).ConfigureAwait(false), Required(redemptionId, "A redemption id"), ct).ConfigureAwait(false);

    // ---- contests and giveaways ----

    public async Task<StreamElementsContest?> GetContestAsync(CancellationToken ct = default) =>
        await _api.GetContestAsync(Jwt, await Channel(ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    public async Task<StreamElementsContest> CreateContestAsync(string title, IReadOnlyList<string> options, long minBet, long maxBet, int minutes,
        CancellationToken ct = default)
    {
        var choices = options.Select(o => o.Trim()).Where(o => o.Length > 0).ToArray();
        if (choices.Length < 2) throw new ArgumentException("A contest needs at least two options");
        return await _api.CreateContestAsync(Jwt, await Channel(ct).ConfigureAwait(false), Required(title, "A title"), choices, minBet, maxBet,
            Math.Max(1, minutes), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// start, close, refund or winner on the given contest (empty: the active one). The winner is an option's title,
    /// number (1, 2, ...) or id.
    /// </summary>
    public async Task<StreamElementsContest> ContestActionAsync(string action, string contestId, string winner, CancellationToken ct = default)
    {
        var channel = await Channel(ct).ConfigureAwait(false);
        var contest = await _api.GetContestAsync(Jwt, channel, ct).ConfigureAwait(false);
        var id = contestId.Trim().Length > 0 ? contestId.Trim() : contest?.Id ?? throw new StreamElementsApiException("There is no StreamElements contest");
        var optionId = "";
        if (action == "winner")
        {
            var w = Required(winner, "The winning option");
            var options = contest?.Options ?? [];
            optionId = options.FirstOrDefault(o => o.Id == w || o.Command == w || o.Title.Equals(w, StringComparison.OrdinalIgnoreCase))?.Id
                       ?? (int.TryParse(w, out var number) && number >= 1 && number <= options.Count ? options[number - 1].Id : w);
        }
        await _api.ContestActionAsync(Jwt, channel, id, action, optionId, ct).ConfigureAwait(false);
        return contest ?? new StreamElementsContest(id, "", "", 0, 0, []);
    }

    public async Task<StreamElementsGiveaway?> GetGiveawayAsync(CancellationToken ct = default) =>
        await _api.GetGiveawayAsync(Jwt, await Channel(ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    /// <summary>start, close, winner, complete, reopen or refund on the given giveaway (empty: the active one).</summary>
    public async Task<string> GiveawayActionAsync(string action, string giveawayId, CancellationToken ct = default)
    {
        var channel = await Channel(ct).ConfigureAwait(false);
        var id = giveawayId.Trim();
        if (id.Length == 0)
            id = (await _api.GetGiveawayAsync(Jwt, channel, ct).ConfigureAwait(false))?.Id ?? throw new StreamElementsApiException("There is no StreamElements giveaway");
        await _api.GiveawayActionAsync(Jwt, channel, id, action, ct).ConfigureAwait(false);
        return id;
    }

    // ---- song requests ----

    public async Task SetSongPlayerAsync(bool play, CancellationToken ct = default) =>
        await _api.SetSongPlayerAsync(Jwt, await Channel(ct).ConfigureAwait(false), play, ct).ConfigureAwait(false);

    public async Task<StreamElementsSong?> GetPlayingSongAsync(CancellationToken ct = default) =>
        await _api.GetPlayingSongAsync(Jwt, await Channel(ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    public async Task<StreamElementsSong?> GetNextSongAsync(CancellationToken ct = default) =>
        await _api.GetNextSongAsync(Jwt, await Channel(ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    public async Task<IReadOnlyList<StreamElementsSong>> GetSongQueueAsync(CancellationToken ct = default) =>
        await _api.GetSongQueueAsync(Jwt, await Channel(ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    public async Task<StreamElementsSong> AddSongAsync(string video, CancellationToken ct = default) =>
        await _api.AddSongAsync(Jwt, await Channel(ct).ConfigureAwait(false), Required(video, "A YouTube link or search"), ct).ConfigureAwait(false);

    public async Task RemoveSongAsync(string songId, CancellationToken ct = default) =>
        await _api.RemoveSongAsync(Jwt, await Channel(ct).ConfigureAwait(false), Required(songId, "A song id"), ct).ConfigureAwait(false);

    private static string User(string user)
    {
        user = user.Trim().TrimStart('@');
        if (user.Length == 0) throw new ArgumentException("A user name is required");
        return user;
    }

    private static string Required(string value, string what) =>
        value.Trim() is { Length: > 0 } trimmed ? trimmed : throw new ArgumentException($"{what} is required");
}
