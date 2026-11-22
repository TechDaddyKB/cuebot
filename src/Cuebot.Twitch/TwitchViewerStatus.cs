using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Twitch.Helix;

namespace Cuebot.Twitch;

/// <summary>Follower, sub tier and ban list answers from Helix, for command permissions and the banned-account filter.</summary>
internal sealed class TwitchViewerStatus(Func<TwitchApi> api) : IViewerStatusSource, IBanListSource
{
    public string Platform => "Twitch";

    public bool CanRead
    {
        get
        {
            var twitch = api();
            return twitch.IsSignedIn
                   && (twitch.HasBroadcasterScope(TwitchScopes.ModerationRead) || twitch.HasBroadcasterScope(TwitchScopes.ManageBannedUsers));
        }
    }

    public async Task<bool> IsFollowerAsync(string userId, CancellationToken ct) =>
        await api().GetFollowAsync(userId, ct).ConfigureAwait(false) is not null;

    public async Task<int> SubTierAsync(string userId, CancellationToken ct)
    {
        var sub = await api().GetSubscriptionAsync(userId, ct).ConfigureAwait(false);
        if (sub is null) return 0;
        // Helix says "1000", "2000" or "3000"; Prime counts as tier 1.
        return sub.Tier.Trim() switch
        {
            "3000" or "3" => 3,
            "2000" or "2" => 2,
            _ => 1,
        };
    }

    public async Task<IReadOnlyList<BannedAccount>> GetBannedAsync(CancellationToken ct)
    {
        var banned = await api().GetBannedUsersAsync(null, ct).ConfigureAwait(false);
        // Timeouts have an end time and are not bans.
        return banned.Where(b => b.ExpiresAt is null).Select(b => new BannedAccount(b.UserId, b.UserLogin, b.BannedAt)).ToArray();
    }
}
