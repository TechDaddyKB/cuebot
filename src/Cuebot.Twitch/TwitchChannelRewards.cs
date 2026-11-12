using Cuebot.Core.Rewards;

namespace Cuebot.Twitch;

/// <summary>Twitch channel point rewards for the mod portal (see <see cref="IChannelRewards"/>).</summary>
public sealed class TwitchChannelRewards(TwitchService twitch) : IChannelRewards
{
    /// <summary>Twitch: "The maximum number of custom rewards per channel is 50", enabled and disabled.</summary>
    public const int MaxRewards = 50;

    /// <summary>Twitch: "A channel may offer a maximum of 50 custom Power-ups".</summary>
    public const int MaxPowerUps = 50;

    public const int MaxTitleLength = 45;
    public const int MaxPromptLength = 200;

    private IReadOnlyList<ChannelReward> _cached = [];

    public string Platform => "twitch";
    public string Name => "Twitch";
    public ChannelRewardRules Rules { get; } = new(MaxRewards, MaxTitleLength, MaxPromptLength, HasStreamLimits: true, HasCooldown: true);
    public bool Ready => twitch.IsAuthorized;
    public IReadOnlyList<ChannelReward> Cached => _cached;

    public async Task<IReadOnlyList<ChannelReward>> ListAsync(CancellationToken ct = default) =>
        _cached = [.. (await twitch.Api.GetRewardsAsync(ct).ConfigureAwait(false)).Select(From)];

    public async Task<ChannelReward> CreateAsync(ChannelReward reward, CancellationToken ct = default)
    {
        var created = From(await twitch.Api.CreateRewardAsync(Update(reward) with { IsPaused = null }, ct).ConfigureAwait(false));
        _cached = [.. _cached, created];
        return created;
    }

    public async Task<ChannelReward> UpdateAsync(ChannelReward reward, CancellationToken ct = default)
    {
        var updated = From(await twitch.Api.UpdateRewardAsync(reward.Id, Update(reward), ct).ConfigureAwait(false));
        _cached = [.. _cached.Select(r => r.Id == updated.Id ? updated : r)];
        return updated;
    }

    public async Task DeleteAsync(string id, CancellationToken ct = default)
    {
        await twitch.Api.DeleteRewardAsync(id, ct).ConfigureAwait(false);
        _cached = [.. _cached.Where(r => r.Id != id)];
    }

    private static ChannelReward From(TwitchReward r) => new(r.Id, r.Title, r.Prompt, r.Cost, r.BackgroundColor, r.IsEnabled, r.IsPaused,
        r.IsUserInputRequired, r.SkipQueue, r.MaxPerStream, r.MaxPerUserPerStream, r.GlobalCooldownSeconds, r.IsManageable);

    private static RewardUpdate Update(ChannelReward r) => new()
    {
        Title = r.Title,
        Prompt = r.Description,
        Cost = r.Cost,
        BackgroundColor = r.Color.Length > 0 ? r.Color : null,
        IsEnabled = r.Enabled,
        IsPaused = r.Paused,
        IsUserInputRequired = r.UserInput,
        SkipQueue = r.SkipQueue,
        MaxPerStream = r.MaxPerStream,
        MaxPerUserPerStream = r.MaxPerUserPerStream,
        GlobalCooldownSeconds = r.CooldownSeconds,
    };
}
