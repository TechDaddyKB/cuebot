namespace Cuebot.Core.Rewards;

/// <summary>A channel point reward as the mod portal sees it, on any platform.</summary>
/// <param name="Manageable">True when Cuebot made it and may change it; false when it may not; null when the platform does not say.</param>
public sealed record ChannelReward(
    string Id,
    string Title,
    string Description,
    int Cost,
    string Color,
    bool Enabled,
    bool Paused,
    bool UserInput,
    bool SkipQueue,
    int MaxPerStream,
    int MaxPerUserPerStream,
    int CooldownSeconds,
    bool? Manageable);

/// <summary>What a platform's rewards allow: how many, how long the texts, which limits it has.</summary>
public sealed record ChannelRewardRules(int MaxRewards, int MaxTitleLength, int MaxDescriptionLength, bool HasStreamLimits, bool HasCooldown);

/// <summary>
/// One platform's channel point rewards (Twitch, Kick), for the mod portal. Each platform only lets the app that made a
/// reward change or delete it; the implementation passes the platform's refusal on.
/// </summary>
public interface IChannelRewards
{
    /// <summary>"twitch" or "kick".</summary>
    string Platform { get; }

    /// <summary>"Twitch" or "Kick".</summary>
    string Name { get; }

    ChannelRewardRules Rules { get; }

    /// <summary>The streamer is signed in, so the rewards can be read.</summary>
    bool Ready { get; }

    /// <summary>The rewards as last read (no platform call).</summary>
    IReadOnlyList<ChannelReward> Cached { get; }

    Task<IReadOnlyList<ChannelReward>> ListAsync(CancellationToken ct = default);

    Task<ChannelReward> CreateAsync(ChannelReward reward, CancellationToken ct = default);

    Task<ChannelReward> UpdateAsync(ChannelReward reward, CancellationToken ct = default);

    Task DeleteAsync(string id, CancellationToken ct = default);
}
