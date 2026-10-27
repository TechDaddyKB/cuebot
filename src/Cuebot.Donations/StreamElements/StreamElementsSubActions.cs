using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Donations.Triggers;

namespace Cuebot.Donations.StreamElements;

/// <summary>Shared shape of the StreamElements REST sub-actions.</summary>
internal abstract class StreamElementsSubAction(StreamElementsService service) : ISubActionHandler
{
    protected StreamElementsService Service => service;
    public abstract string TypeId { get; }
    public string Category => StreamElementsTriggers.Category;
    public abstract string DisplayName { get; }
    public abstract IReadOnlyList<ParameterDescriptor> Parameters { get; }
    public abstract Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct);

    protected static ParameterDescriptor User { get; } =
        new("user", "User") { DefaultValue = "%userName%", Help = "StreamElements loyalty user (the platform login name)" };

    protected static ParameterDescriptor Count(string label) =>
        new("count", label, ParameterKind.Number) { DefaultValue = "5" };
}

internal sealed class GetUserPointsSubAction(StreamElementsService service) : StreamElementsSubAction(service)
{
    public override string TypeId => "streamelements.getUserPoints";
    public override string DisplayName => "Get user points";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [User];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var points = await Service.GetUserPointsAsync(p["user"], ct).ConfigureAwait(false);
        context.Args["sePointsUser"] = points.UserName;
        context.Args["sePoints"] = points.Points;
        context.Args["sePointsAllTime"] = points.PointsAllTime;
        context.Args["sePointsRank"] = points.Rank;
        return SubActionResult.Next;
    }
}

internal sealed class AddUserPointsSubAction(StreamElementsService service) : StreamElementsSubAction(service)
{
    public override string TypeId => "streamelements.addUserPoints";
    public override string DisplayName => "Add user points";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [User, new("amount", "Points", ParameterKind.Number) { DefaultValue = "100", Help = "Negative values remove points" }];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var amount = (long)Math.Round(p.GetDouble("amount"));
        context.Args["sePoints"] = await Service.AddUserPointsAsync(p["user"], amount, ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

internal sealed class GetPointsLeaderboardSubAction(StreamElementsService service) : StreamElementsSubAction(service)
{
    public override string TypeId => "streamelements.getPointsLeaderboard";
    public override string DisplayName => "Get points leaderboard";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [Count("Users")];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var rows = await Service.GetPointsLeaderboardAsync(p.GetInt("count", 5), ct).ConfigureAwait(false);
        WriteRanks(context, "sePointsLeader", rows);
        return SubActionResult.Next;
    }

    internal static void WriteRanks(ActionRunContext context, string prefix, IReadOnlyList<StreamElementsRank> rows)
    {
        context.Args[$"{prefix}Count"] = rows.Count;
        for (var i = 0; i < rows.Count; i++)
        {
            context.Args[$"{prefix}{i}User"] = rows[i].UserName;
            context.Args[$"{prefix}{i}Total"] = rows[i].Total;
        }
    }
}

internal sealed class GetTipsSubAction(StreamElementsService service) : StreamElementsSubAction(service)
{
    public override string TypeId => "streamelements.getTips";
    public override string DisplayName => "Get latest tips";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [Count("Tips")];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var tips = await Service.GetTipsAsync(p.GetInt("count", 5), ct).ConfigureAwait(false);
        context.Args["seTipCount"] = tips.Count;
        for (var i = 0; i < tips.Count; i++)
        {
            var tip = tips[i];
            var n = i.ToString(CultureInfo.InvariantCulture);
            context.Args[$"seTip{n}User"] = tip.UserName;
            context.Args[$"seTip{n}Amount"] = tip.Amount;
            context.Args[$"seTip{n}Currency"] = tip.Currency;
            context.Args[$"seTip{n}FormattedAmount"] = tip.FormattedAmount;
            context.Args[$"seTip{n}Message"] = tip.Message;
            context.Args[$"seTip{n}CreatedAt"] = tip.CreatedAt;
        }
        return SubActionResult.Next;
    }
}

internal sealed class GetTipLeaderboardSubAction(StreamElementsService service) : StreamElementsSubAction(service)
{
    public override string TypeId => "streamelements.getTipLeaderboard";
    public override string DisplayName => "Get tip leaderboard";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [Count("Tippers")];

    public override async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var rows = await Service.GetTipLeaderboardAsync(p.GetInt("count", 5), ct).ConfigureAwait(false);
        GetPointsLeaderboardSubAction.WriteRanks(context, "seTipLeader", rows);
        return SubActionResult.Next;
    }
}
