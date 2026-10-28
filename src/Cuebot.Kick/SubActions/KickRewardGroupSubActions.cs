using Cuebot.Core.Actions;
using Cuebot.Kick.Api;

namespace Cuebot.Kick.SubActions;

/// <summary>
/// Streamer.bot's reward counters and reward groups, kept by Cuebot (Kick's API has neither): counters count
/// redemptions (reset on going live unless persisted), groups are names set per reward on the Kick page.
/// </summary>
internal static class KickRewardGroups
{
    public static ParameterDescriptor Group() =>
        new("group", "Reward group") { Help = "Set a reward's group on the Kick page (Channel rewards)" };

    public static ParameterDescriptor State(string on, string off) =>
        new("state", "State", ParameterKind.Choice) { DefaultValue = on, Choices = [on, off, "Toggle"] };

    /// <summary>The reward's id: one Cuebot already keeps counters or a group for, else looked up on Kick by id or title.</summary>
    public static async Task<string> RewardIdAsync(KickService kick, ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var key = KickRewardParameters.RewardKey(context, p);
        return kick.Settings.Rewards.ContainsKey(key) ? key : (await kick.FindRewardAsync(key, ct).ConfigureAwait(false)).Id;
    }

    public static string[] Lines(string text) =>
        text.Split(['\n', '\r', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

internal sealed class GetRewardCounterSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.getRewardCounter";
    public override string DisplayName => "Reward: get counters (counter, userCounter)";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        KickRewardParameters.Reward(),
        new("user", "For user") { DefaultValue = "%userId%", Help = "Kick user name or numeric user id; empty for no user counter" },
    ];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var rewardId = await KickRewardGroups.RewardIdAsync(Kick, context, p, ct).ConfigureAwait(false);
        var user = p["user"].Trim();
        var userId = user.Length == 0 ? "" : Events.KickArgs.Id(await Kick.ResolveUserIdAsync(user, ct).ConfigureAwait(false));
        var (counter, userCounter) = Kick.Counters.GetRewardCounter(rewardId, userId);
        context.Args["counter"] = counter;
        context.Args["userCounter"] = userCounter;
        return SubActionResult.Next;
    }
}

/// <summary>Reset Reward Counter, or Reset Reward User Counters.</summary>
internal sealed class ResetRewardCounterSubAction(KickService kick, bool users) : KickSubAction(kick)
{
    public override string TypeId => users ? "kick.resetRewardUserCounters" : "kick.resetRewardCounter";
    public override string DisplayName => users ? "Reward: reset user counters" : "Reward: reset counter";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [KickRewardParameters.Reward()];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var rewardId = await KickRewardGroups.RewardIdAsync(Kick, context, p, ct).ConfigureAwait(false);
        if (users) Kick.Counters.ResetRewardUserCounters(rewardId);
        else Kick.Counters.ResetRewardCounter(rewardId);
        return SubActionResult.Next;
    }
}

/// <summary>
/// Set Group Enabled State, or Set Group Paused State: one flag on every reward of a group (Toggle: on unless any
/// reward already has it). Rewards already in that state are left alone; ones Kick refuses (made by another app or on
/// kick.com) are counted in rewardsSkipped.
/// </summary>
internal sealed class SetRewardGroupStateSubAction(KickService kick, bool paused) : KickSubAction(kick)
{
    public override string TypeId => paused ? "kick.setRewardGroupPaused" : "kick.setRewardGroupEnabled";
    public override string DisplayName => paused ? "Reward group: set paused state (rewardsChanged)" : "Reward group: set enabled state (rewardsChanged)";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [KickRewardGroups.Group(), paused ? KickRewardGroups.State("Paused", "Unpaused") : KickRewardGroups.State("Enabled", "Disabled")];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var group = p["group"].Trim();
        if (group.Length == 0) throw new ArgumentException("No reward group given.");
        var ids = Kick.Counters.RewardsInGroup(group).ToHashSet(StringComparer.Ordinal);
        var rewards = (await Kick.GetRewardsAsync(ct).ConfigureAwait(false)).Where(r => ids.Contains(r.Id)).ToArray();
        if (rewards.Length == 0) throw new KickApiException($"The reward group '{group}' has no rewards.");
        Func<KickReward, bool> flag = paused ? r => r.IsPaused : r => r.IsEnabled;
        var state = p["state"];
        var on = state == "Toggle"
            ? !rewards.Any(flag)
            : !state.StartsWith("Dis", StringComparison.Ordinal) && !state.StartsWith("Un", StringComparison.Ordinal);
        var changes = rewards.Where(r => flag(r) != on)
            .Select(r => (r, paused ? new KickRewardUpdate { IsPaused = on } : new KickRewardUpdate { IsEnabled = on })).ToArray();
        var (changed, skipped) = await Kick.UpdateRewardsAsync(changes, ct).ConfigureAwait(false);
        context.Args["rewardsChanged"] = changed;
        context.Args["rewardsSkipped"] = skipped;
        return SubActionResult.Next;
    }
}

/// <summary>Enables, disables, pauses and unpauses many rewards at once (by title, id or group), with one update each.</summary>
internal sealed class ConfigureRewardsSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.configureRewards";
    public override string DisplayName => "Configure rewards (rewardsChanged)";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("enable", "Enable", ParameterKind.Multiline) { Help = "Reward titles, ids or group names, one per line" },
        new("disable", "Disable", ParameterKind.Multiline) { Help = "Reward titles, ids or group names, one per line" },
        new("pause", "Pause", ParameterKind.Multiline) { Help = "Reward titles, ids or group names, one per line" },
        new("unpause", "Unpause", ParameterKind.Multiline) { Help = "Reward titles, ids or group names, one per line" },
        new("disableNotConfigured", "Disable rewards that are in no group", ParameterKind.Boolean) { DefaultValue = "false" },
    ];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var rewards = await Kick.GetRewardsAsync(ct).ConfigureAwait(false);
        var updates = new Dictionary<string, KickRewardUpdate>(StringComparer.Ordinal);
        var mentioned = new HashSet<string>(StringComparer.Ordinal);

        IEnumerable<KickReward> Resolve(string list) => KickRewardGroups.Lines(list).SelectMany(key =>
        {
            var inGroup = Kick.Counters.RewardsInGroup(key).ToHashSet(StringComparer.Ordinal);
            var matches = rewards.Where(r => string.Equals(r.Id, key, StringComparison.OrdinalIgnoreCase)
                                             || r.Title.Equals(key, StringComparison.OrdinalIgnoreCase) || inGroup.Contains(r.Id)).ToArray();
            return matches.Length > 0 ? matches : throw new KickApiException($"No reward or reward group '{key}' was found.");
        });

        void Apply(string list, Func<KickRewardUpdate, KickRewardUpdate> change)
        {
            foreach (var reward in Resolve(list))
            {
                mentioned.Add(reward.Id);
                updates[reward.Id] = change(updates.GetValueOrDefault(reward.Id) ?? new KickRewardUpdate());
            }
        }

        Apply(p["enable"], u => u with { IsEnabled = true });
        Apply(p["disable"], u => u with { IsEnabled = false });
        Apply(p["pause"], u => u with { IsPaused = true });
        Apply(p["unpause"], u => u with { IsPaused = false });
        if (p.GetBool("disableNotConfigured"))
            foreach (var reward in rewards.Where(r => !mentioned.Contains(r.Id) && r.IsEnabled && Kick.Counters.GetGroup(r.Id).Length == 0))
                updates[reward.Id] = new KickRewardUpdate { IsEnabled = false };

        var changes = rewards.Where(r => updates.ContainsKey(r.Id)).Select(r => (r, updates[r.Id])).ToArray();
        var (changed, skipped) = await Kick.UpdateRewardsAsync(changes, ct).ConfigureAwait(false);
        context.Args["rewardsChanged"] = changed;
        context.Args["rewardsSkipped"] = skipped;
        return SubActionResult.Next;
    }
}
