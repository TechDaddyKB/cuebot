using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Scripting;

namespace Cuebot.Twitch;

/// <summary>
/// Script functions returning lists (clips, moderators, VIPs, blocked terms, cheermotes, reward user counters) and the
/// few actions Streamer.bot's C# methods need beyond the sub-actions. They back the CPH compatibility layer.
/// </summary>
internal static class TwitchListScriptFunctions
{
    public static IEnumerable<ScriptFunction> Create(Func<TwitchApi> api) =>
    [
        new("twitch.getClips",
            "Clips of a channel (user: login or id:123, default the broadcaster) or category (gameId), most viewed first; startedAt, endedAt, count, featured optional",
            ["user", "gameId", "startedAt", "endedAt", "count", "featured"], async (_, a, ct) =>
            {
                var gameId = a.GetString("gameId").Trim();
                var user = a.GetString("user").Trim();
                var broadcasterId = gameId.Length > 0 ? null
                    : user.Length > 0 ? await api().ResolveUserIdAsync(user, ct).ConfigureAwait(false)
                    : api().Broadcaster.UserId;
                return await api().GetClipsAsync(broadcasterId, gameId.Length > 0 ? gameId : null, Time(a, "startedAt"), Time(a, "endedAt"),
                    Int(a, "count"), a.GetElement("featured") is { ValueKind: JsonValueKind.True or JsonValueKind.False } f ? f.GetBoolean() : null, ct)
                    .ConfigureAwait(false);
            }),
        new("twitch.getModerators", "Every moderator of the channel: userId, userLogin, userName", [],
            async (_, _, ct) => await api().GetModeratorsAsync(ct).ConfigureAwait(false)),
        new("twitch.getVips", "Every VIP of the channel: userId, userLogin, userName", [],
            async (_, _, ct) => await api().GetVipsAsync(ct).ConfigureAwait(false)),
        new("twitch.getBlockedTerms", "Every blocked term of the channel: id, text", [],
            async (_, _, ct) => await api().GetBlockedTermsAsync(ct).ConfigureAwait(false)),
        new("twitch.removeBlockedTerm", "Remove a blocked term by its text (or id); returns false when the channel has no such term", ["term"],
            async (_, a, ct) =>
            {
                var term = a.GetString("term").Trim();
                var found = (await api().GetBlockedTermsAsync(ct).ConfigureAwait(false))
                    .FirstOrDefault(t => t.Id == term || string.Equals(t.Text, term, StringComparison.OrdinalIgnoreCase));
                if (found is null) return false;
                await api().RemoveBlockedTermAsync(found.Id, ct).ConfigureAwait(false);
                return true;
            }),
        new("twitch.getCheermotes", "The channel's cheermotes as Twitch lists them", [],
            async (_, _, ct) => (await api().GetCheermotesAsync(ct).ConfigureAwait(false)).Clone()),
        new("twitch.sendAndPinMessage", "Send a chat message as the broadcaster and pin it (duration seconds, 0 = until the stream ends); returns its id",
            ["message", "duration"], async (_, a, ct) =>
                await api().SendAndPinMessageAsync(a.GetString("message"), Int(a, "duration"), ct).ConfigureAwait(false)),
        new("twitch.getRewardUserCounters", "Every user's redemption count of a reward (title or id): userId → count", ["reward"],
            async (_, a, ct) =>
            {
                var reward = await api().FindRewardAsync(a.GetString("reward"), ct).ConfigureAwait(false);
                return api().Counters.GetRewardUserCounters(reward.Id);
            }),
        new("twitch.resetUserRewardCounter", "Reset one user's (login or id:123) counter of a reward (title or id)", ["reward", "user"],
            async (_, a, ct) =>
            {
                var reward = await api().FindRewardAsync(a.GetString("reward"), ct).ConfigureAwait(false);
                api().Counters.ResetUserRewardCounter(reward.Id, await api().ResolveUserIdAsync(a.GetString("user"), ct).ConfigureAwait(false));
                return true;
            }),
        new("twitch.resetUserRewardCounters", "Reset one user's (login or id:123) counters of every reward; returns how many were reset", ["user"],
            async (_, a, ct) => api().Counters.ResetUserRewardCounters(await api().ResolveUserIdAsync(a.GetString("user"), ct).ConfigureAwait(false))),
    ];

    private static int Int(JsonElement a, string name) =>
        int.TryParse(a.GetString(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static DateTimeOffset? Time(JsonElement a, string name) =>
        DateTimeOffset.TryParse(a.GetString(name), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t) ? t : null;
}
