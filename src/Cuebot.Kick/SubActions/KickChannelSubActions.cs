using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Kick.Api;
using Cuebot.Kick.Events;

namespace Cuebot.Kick.SubActions;

internal sealed class SetTagsSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.setTags";
    public override string DisplayName => "Set channel tags (tags)";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [new("tags", "Tags") { Help = "Comma-separated, like \"chill, english, cozy\". Empty removes every tag" }];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var tags = await Kick.SetTagsAsync(p["tags"], ct).ConfigureAwait(false);
        context.Args["tags"] = string.Join(", ", tags);
        context.Args["tagsSuccess"] = true;
        return SubActionResult.Next;
    }
}

internal sealed class GetChannelInfoSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.getChannelInfo";
    public override string DisplayName => "Get channel info (channelTitle, channelIsLive, channelSubscriberCount...)";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("channel", "Channel") { Help = "Kick user name, channel name or numeric user id. Empty: your own channel" },
        new("prefix", "Variable name start") { DefaultValue = "channel" },
    ];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var prefix = p["prefix"].Trim().Length > 0 ? p["prefix"].Trim() : "channel";
        foreach (var (key, value) in await Kick.GetChannelInfoAsync(Clean(p["channel"]), prefix, ct).ConfigureAwait(false)) context.Args[key] = value;
        return SubActionResult.Next;
    }

    /// <summary>An unresolved "%arg%" counts as empty (the own channel).</summary>
    internal static string Clean(string value)
    {
        value = value.Trim();
        return value.StartsWith('%') && value.EndsWith('%') ? "" : value;
    }
}

internal sealed class GetSubscriberCountSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.getSubscriberCount";
    public override string DisplayName => "Get subscriber count (subscriberCount, giftedSubscriberCount, paidSubscriberCount)";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var info = await Kick.GetChannelInfoAsync("", "channel", ct).ConfigureAwait(false);
        context.Args["subscriberCount"] = info["channelSubscriberCount"];
        context.Args["giftedSubscriberCount"] = info["channelGiftedSubscriberCount"];
        context.Args["paidSubscriberCount"] = info["channelPaidSubscriberCount"];
        context.Args["canceledSubscriberCount"] = info["channelCanceledSubscriberCount"];
        return SubActionResult.Next;
    }
}

internal sealed class GetLiveStatusSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.getLiveStatus";
    public override string DisplayName => "Is a user live? (liveIsLive, liveTitle, liveViewerCount, liveCategory...)";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [User("User to check"), new("prefix", "Variable name start") { DefaultValue = "live" }];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var prefix = p["prefix"].Trim().Length > 0 ? p["prefix"].Trim() : "live";
        foreach (var (key, value) in await Kick.GetLiveStatusAsync(p["user"], prefix, ct).ConfigureAwait(false)) context.Args[key] = value;
        return SubActionResult.Next;
    }
}

internal sealed class GetLivestreamsSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.getLivestreams";
    public override string DisplayName => "Find live streams (livestreamCount, livestream1UserName, livestreamNames...)";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("category", "Category") { Help = "Category name or numeric id. Empty: every category" },
        new("language", "Language code") { Help = "Like en or es. Empty: every language" },
        new("count", "How many", ParameterKind.Number) { DefaultValue = "5", Help = "1 to 100, the most watched first" },
    ];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var streams = await Kick.GetLivestreamsAsync(p["category"], p["language"], p.GetInt("count", 5), ct).ConfigureAwait(false);
        context.Args["livestreamCount"] = streams.Count;
        context.Args["livestreamNames"] = string.Join(", ", streams.Select(s => s.UserName));
        for (var i = 0; i < streams.Count; i++)
            foreach (var (key, value) in KickService.LivestreamArgs(streams[i], "livestream" + (i + 1).ToString(CultureInfo.InvariantCulture)))
                context.Args[key] = value;
        return SubActionResult.Next;
    }
}

internal sealed class GetKicksLeaderboardSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.getKicksLeaderboard";
    public override string DisplayName => "Get KICKs leaderboard (leaderboardCount, leaderboard1User, leaderboard1Amount...)";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("period", "Period", ParameterKind.Choice) { DefaultValue = "week", Choices = ["week", "month", "lifetime"], ChoiceLabels = new Dictionary<string, string> { ["week"] = "This week", ["month"] = "This month", ["lifetime"] = "All time" } },
        new("top", "How many", ParameterKind.Number) { DefaultValue = "10", Help = "1 to 100" },
    ];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var board = await Kick.GetKicksLeaderboardAsync(p.GetInt("top", 10), ct).ConfigureAwait(false);
        var entries = p["period"].Trim().ToLowerInvariant() switch
        {
            "month" => board.Month,
            "lifetime" or "all" or "alltime" => board.Lifetime,
            _ => board.Week,
        };
        Write(context.Args, entries);
        return SubActionResult.Next;
    }

    internal static void Write(IDictionary<string, object?> args, IReadOnlyList<KickLeaderboardEntry> entries)
    {
        args["leaderboardCount"] = entries.Count;
        args["leaderboardList"] = string.Join(", ", entries.Select(e => $"{e.UserName} ({e.GiftedAmount.ToString(CultureInfo.InvariantCulture)})"));
        for (var i = 0; i < entries.Count; i++)
        {
            var n = "leaderboard" + (i + 1).ToString(CultureInfo.InvariantCulture);
            args[n + "User"] = entries[i].UserName;
            args[n + "UserId"] = KickArgs.Id(entries[i].UserId);
            args[n + "Amount"] = entries[i].GiftedAmount;
            args[n + "Rank"] = entries[i].Rank;
        }
    }
}

internal sealed class RunAdBreakSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.runAdBreak";
    public override string DisplayName => "Run ad break (adBreakId, remainingAdBreaks)";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("duration", "Length (seconds)", ParameterKind.Number)
        {
            DefaultValue = "30",
            Help = $"{KickApiClient.MinAdBreakSeconds} to {KickApiClient.MaxAdBreakSeconds} seconds",
        },
    ];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var ad = await Kick.RunAdBreakAsync(p.GetInt("duration", 30), ct).ConfigureAwait(false);
        context.Args["adBreakId"] = ad.Id;
        context.Args["adLength"] = Math.Clamp(p.GetInt("duration", 30), KickApiClient.MinAdBreakSeconds, KickApiClient.MaxAdBreakSeconds);
        context.Args["remainingAdBreaks"] = ad.RemainingAdBreaks;
        return SubActionResult.Next;
    }
}

internal sealed class GetAdStatusSubAction(KickService kick) : KickSubAction(kick)
{
    public override string TypeId => "kick.getAdStatus";
    public override string DisplayName => "Get ad status (adsOptedIn, remainingAdBreaks, maxAdBreaks...)";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var s = await Kick.GetAdStatusAsync(ct).ConfigureAwait(false);
        context.Args["adsOptedIn"] = s.OptedIn;
        context.Args["adsBlocked"] = s.AdsBlocked;
        context.Args["autoAdsActive"] = s.AutoAdsActive;
        context.Args["remainingAdBreaks"] = s.RemainingAdBreaks;
        context.Args["maxAdBreaks"] = s.MaxAdBreaks;
        context.Args["adLimitPeriodSeconds"] = s.LimitPeriodSeconds;
        var last = s.AdBreaks.OrderByDescending(b => b.CreatedAt).FirstOrDefault();
        context.Args["lastAdBreakStatus"] = last?.Status ?? "";
        context.Args["lastAdBreakLength"] = last?.DurationSeconds ?? 0;
        context.Args["lastAdBreakAt"] = last?.CreatedAt?.LocalDateTime ?? default(DateTime);
        return SubActionResult.Next;
    }
}
