using Cuebot.Core.Rewards;
using Cuebot.Kick.Api;

namespace Cuebot.Kick;

/// <summary>Kick channel rewards for the mod portal (see <see cref="IChannelRewards"/>).</summary>
public sealed class KickChannelRewards(KickService kick) : IChannelRewards
{
    /// <summary>Kick: "Channels may have up to 50 rewards, including both enabled and disabled rewards."</summary>
    public const int MaxRewards = 50;

    public const int MaxTitleLength = KickApiClient.MaxRewardTitleLength;
    public const int MaxDescriptionLength = KickApiClient.MaxRewardDescriptionLength;

    public string Platform => "kick";
    public string Name => "Kick";
    public ChannelRewardRules Rules { get; } = new(MaxRewards, MaxTitleLength, MaxDescriptionLength,
        HasStreamLimits: false, HasCooldown: false);
    public bool Ready => kick.IsBroadcasterSignedIn;
    public IReadOnlyList<ChannelReward> Cached => [.. kick.Rewards.Select(From)];

    public async Task<IReadOnlyList<ChannelReward>> ListAsync(CancellationToken ct = default) =>
        [.. (await kick.GetRewardsAsync(ct).ConfigureAwait(false)).Select(From)];

    public async Task<ChannelReward> CreateAsync(ChannelReward reward, CancellationToken ct = default) =>
        From(await kick.CreateRewardAsync(Update(reward) with { IsPaused = null }, ct).ConfigureAwait(false));

    public async Task<ChannelReward> UpdateAsync(ChannelReward reward, CancellationToken ct = default) =>
        From(await kick.UpdateRewardAsync(reward.Id, Update(reward), ct).ConfigureAwait(false));

    public async Task DeleteAsync(string id, CancellationToken ct = default) => await kick.DeleteRewardAsync(id, ct).ConfigureAwait(false);

    private ChannelReward From(KickReward r) => new(r.Id, r.Title, r.Description, r.Cost, r.BackgroundColor, r.IsEnabled, r.IsPaused,
        r.IsUserInputRequired, r.SkipsRequestQueue, 0, 0, 0, kick.MadeByCuebot(r.Id) ? true : null);

    private static KickRewardUpdate Update(ChannelReward r) => new()
    {
        Title = r.Title,
        Description = r.Description,
        Cost = r.Cost,
        BackgroundColor = r.Color.Length > 0 ? r.Color : null,
        IsEnabled = r.Enabled,
        IsPaused = r.Paused,
        IsUserInputRequired = r.UserInput,
        SkipsRequestQueue = r.SkipQueue,
    };
}
