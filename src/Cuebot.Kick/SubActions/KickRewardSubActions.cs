using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Kick.Api;

namespace Cuebot.Kick.SubActions;

/// <summary>
/// Channel reward management through the official API (docs.kick.com "Channel Rewards"). Kick lets an app change,
/// delete and handle redemptions only of rewards that the same app created, so rewards made on kick.com itself can
/// be read but not changed.
/// </summary>
internal static class KickRewardParameters
{
    public static ParameterDescriptor Reward() =>
        new("reward", "Reward") { Help = "Reward title or id. Leave empty for the redeemed reward (%rewardId%)." };

    /// <summary>The reward named in the parameters, or the one that triggered the action.</summary>
    public static string RewardKey(ActionRunContext context, ParameterValues p)
    {
        var key = p["reward"].Trim();
        if (key.Length == 0 && context.Args.TryGetValue("rewardId", out var id))
            key = Convert.ToString(id, CultureInfo.InvariantCulture) ?? "";
        return key.Length > 0 ? key : throw new ArgumentException("No reward given.");
    }

    public static void Describe(ActionRunContext context, KickReward r, string prefix = "reward")
    {
        context.Args[prefix + "Id"] = r.Id;
        context.Args[prefix + "Title"] = r.Title;
        context.Args[prefix + "Description"] = r.Description;
        context.Args[prefix + "Cost"] = r.Cost;
        context.Args[prefix + "BackgroundColor"] = r.BackgroundColor;
        context.Args[prefix + "Enabled"] = r.IsEnabled;
        context.Args[prefix + "Paused"] = r.IsPaused;
        context.Args[prefix + "UserInputRequired"] = r.IsUserInputRequired;
        context.Args[prefix + "SkipsQueue"] = r.SkipsRequestQueue;
    }
}

internal sealed class CreateRewardSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.createReward";
    public override string DisplayName => "Reward: create";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("title", "Title") { Help = "Up to 50 characters" },
        new("cost", "Cost", ParameterKind.Number) { DefaultValue = "100" },
        new("description", "Description") { Help = "Up to 200 characters" },
        new("color", "Background colour (#RRGGBB)") { DefaultValue = "#00E701" },
        new("enabled", "Enabled", ParameterKind.Boolean) { DefaultValue = "true" },
        new("userInput", "Viewer must enter text", ParameterKind.Boolean) { DefaultValue = "false" },
        new("skipQueue", "Skip the request queue (accept automatically)", ParameterKind.Boolean) { DefaultValue = "false" },
    ];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var created = await Kick.CreateRewardAsync(new KickRewardUpdate
        {
            Title = p["title"],
            Cost = p.GetInt("cost", 100),
            Description = p["description"].Trim().Length > 0 ? p["description"] : null,
            BackgroundColor = p["color"].Trim().Length > 0 ? p["color"] : null,
            IsEnabled = p.GetBool("enabled", true),
            IsUserInputRequired = p.GetBool("userInput"),
            SkipsRequestQueue = p.GetBool("skipQueue"),
        }, ct).ConfigureAwait(false);
        context.Args["createdRewardId"] = created.Id;
        context.Args["createdRewardTitle"] = created.Title;
        return SubActionResult.Next;
    }
}

/// <summary>One field of a reward (enabled, paused, title, cost...).</summary>
internal sealed class UpdateRewardSubAction(KickService kick, string typeId, string displayName, ParameterDescriptor value,
    Func<ParameterValues, KickRewardUpdate> update) : KickSubAction(kick)
{
    public override string TypeId => typeId;
    public override string DisplayName => displayName;
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [KickRewardParameters.Reward(), value];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var updated = await Kick.UpdateRewardAsync(KickRewardParameters.RewardKey(context, p), update(p), ct).ConfigureAwait(false);
        KickRewardParameters.Describe(context, updated);
        return SubActionResult.Next;
    }

    /// <summary>Every single-field reward sub-action.</summary>
    public static IEnumerable<UpdateRewardSubAction> All(KickService kick) =>
    [
        new(kick, "kick.setRewardEnabled", "Reward: set enabled",
            new("enabled", "Enabled", ParameterKind.Boolean) { DefaultValue = "true" }, p => new KickRewardUpdate { IsEnabled = p.GetBool("enabled", true) }),
        new(kick, "kick.setRewardPaused", "Reward: set paused",
            new("paused", "Paused", ParameterKind.Boolean) { DefaultValue = "true" }, p => new KickRewardUpdate { IsPaused = p.GetBool("paused", true) }),
        new(kick, "kick.setRewardTitle", "Reward: set title", new("title", "Title"), p => new KickRewardUpdate { Title = p["title"] }),
        new(kick, "kick.setRewardCost", "Reward: set cost",
            new("cost", "Cost", ParameterKind.Number) { DefaultValue = "100" }, p => new KickRewardUpdate { Cost = p.GetInt("cost", 100) }),
        new(kick, "kick.setRewardDescription", "Reward: set description", new("description", "Description"),
            p => new KickRewardUpdate { Description = p["description"] }),
        new(kick, "kick.setRewardColor", "Reward: set background colour",
            new("color", "Colour (#RRGGBB)") { DefaultValue = "#00E701" }, p => new KickRewardUpdate { BackgroundColor = p["color"] }),
        new(kick, "kick.setRewardUserInput", "Reward: set viewer input required",
            new("required", "Viewer must enter text", ParameterKind.Boolean) { DefaultValue = "true" },
            p => new KickRewardUpdate { IsUserInputRequired = p.GetBool("required", true) }),
        new(kick, "kick.setRewardSkipQueue", "Reward: set skip request queue",
            new("skip", "Accept redemptions automatically", ParameterKind.Boolean) { DefaultValue = "true" },
            p => new KickRewardUpdate { SkipsRequestQueue = p.GetBool("skip", true) }),
    ];
}

internal sealed class DeleteRewardSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.deleteReward";
    public override string DisplayName => "Reward: delete";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [KickRewardParameters.Reward()];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        await Kick.DeleteRewardAsync(KickRewardParameters.RewardKey(context, p), ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

internal sealed class GetRewardInfoSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.getRewardInfo";
    public override string DisplayName => "Reward: get info";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [KickRewardParameters.Reward()];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        KickRewardParameters.Describe(context, await Kick.FindRewardAsync(KickRewardParameters.RewardKey(context, p), ct).ConfigureAwait(false));
        return SubActionResult.Next;
    }
}

/// <summary>Accepts (fulfils) or rejects (refunds) one redemption, by default the one that triggered the action.</summary>
internal sealed class UpdateRedemptionSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.updateRedemption";
    public override string DisplayName => "Reward: accept or reject redemption";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("redemptionId", "Redemption id") { DefaultValue = "%redemptionId%", Help = "Several ids may be separated by commas" },
        new("status", "Action", ParameterKind.Choice) { DefaultValue = "Accept", Choices = ["Accept", "Reject"] },
    ];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var ids = p["redemptionId"].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var failures = await Kick.UpdateRedemptionsAsync(ids, p["status"] != "Reject", ct).ConfigureAwait(false);
        context.Args["redemptionUpdated"] = failures.Count == 0;
        context.Args["redemptionFailures"] = string.Join(", ", failures.Select(f => $"{f.Id}: {f.Reason}"));
        return SubActionResult.Next;
    }
}

/// <summary>Accepts or rejects every pending redemption (of one reward, or of all rewards this app manages).</summary>
internal sealed class UpdatePendingRedemptionsSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.updatePendingRedemptions";
    public override string DisplayName => "Reward: accept or reject all pending redemptions";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("reward", "Reward") { Help = "Reward title or id. Leave empty for every reward." },
        new("status", "Action", ParameterKind.Choice) { DefaultValue = "Accept", Choices = ["Accept", "Reject"] },
    ];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var pending = await Kick.GetPendingRedemptionsAsync(p["reward"], ct).ConfigureAwait(false);
        var failures = pending.Count == 0 ? []
            : await Kick.UpdateRedemptionsAsync(pending.Select(r => r.Id), p["status"] != "Reject", ct).ConfigureAwait(false);
        context.Args["redemptionsUpdated"] = pending.Count - failures.Count;
        context.Args["redemptionFailures"] = string.Join(", ", failures.Select(f => $"{f.Id}: {f.Reason}"));
        return SubActionResult.Next;
    }
}
