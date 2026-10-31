using System.Text.Json;
using Cuebot.Core.Actions;

namespace Cuebot.Core.Points;

/// <summary>
/// Streamer.bot "Check Points": whether the user has at least the required amount, without spending. Branches
/// "Enough" / "Not enough"; with "Break on failure" (default on, as in Streamer.bot) a failed check stops the action.
/// Sets %pointsEnough%, the balance arguments and the destination argument (default %userPoints%).
/// </summary>
public sealed class CheckPointsSubAction(PointsService points) : PointsSubActionBase(points)
{
    public const string Enough = "Enough";
    public const string NotEnough = "Not enough";

    public override string TypeId => "points.check";
    public override string DisplayName => "Check points";
    public override IReadOnlyList<string> Branches { get; } = [Enough, NotEnough];
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        .. PointsParameters.User(), PointsParameters.Amount("0", "Required amount (whole number)"),
        new("breakOnFailure", "Stop the action when not enough", ParameterKind.Boolean) { DefaultValue = "true" },
        PointsParameters.Destination("userPoints"),
        PointsParameters.Currency(points),
    ];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var currency = PointsParameters.CurrencyOf(Points, p);
        var user = PointsParameters.Require(Points, context, p, currency: currency);
        var balance = Points.Get(user.Platform, user.UserId, currency);
        var required = PointsParameters.ParseAmount(p["amount"]);
        var ok = balance >= required;
        PointsParameters.Store(context, Points, user, balance, currency: currency);
        PointsParameters.StoreDestination(context, p, balance);
        context.Args["pointsEnough"] = ok;
        context.Args["pointsMissing"] = ok ? 0 : required - balance;
        if (!ok && p.GetBool("breakOnFailure", true)) return Task.FromResult(SubActionResult.Stop);
        return Task.FromResult(SubActionResult.TakeBranch(ok ? Enough : NotEnough));
    }
}

/// <summary>Streamer.bot "Delete Points": removes the user's balance record entirely. Sets %pointsDeleted%.</summary>
public sealed class DeletePointsSubAction(PointsService points) : PointsSubActionBase(points)
{
    public override string TypeId => "points.delete";
    public override string DisplayName => "Delete user points";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [.. PointsParameters.User(), PointsParameters.Currency(points)];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var currency = PointsParameters.CurrencyOf(Points, p);
        var user = PointsParameters.Require(Points, context, p, currency: currency);
        context.Args["pointsDeleted"] = Points.Delete(user.Platform, user.UserId, currency);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Streamer.bot "Reset User Points": sets one user's balance to 0 (the record stays).</summary>
public sealed class ResetUserPointsSubAction(PointsService points) : PointsSubActionBase(points)
{
    public override string TypeId => "points.resetUser";
    public override string DisplayName => "Reset user points";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [.. PointsParameters.User(), PointsParameters.Destination("userPoints"), PointsParameters.Currency(points)];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var currency = PointsParameters.CurrencyOf(Points, p);
        var user = PointsParameters.Require(Points, context, p, currency: currency);
        var balance = Points.Set(user.Platform, user.UserId, user.UserName, 0, currency);
        PointsParameters.Store(context, Points, user, balance, currency: currency);
        PointsParameters.StoreDestination(context, p, balance);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>
/// Streamer.bot "Get Points Definition": a currency's settings in %&lt;variable&gt;% (JSON, default %pointsDefinition%)
/// and flattened as %&lt;variable&gt;.name%, .singularName, .symbol, .variableName, .enabled, .maxBalance, .onlyWhileLive.
/// </summary>
public sealed class GetPointsDefinitionSubAction(PointsService points) : PointsSubActionBase(points)
{
    public override string TypeId => "points.getDefinition";
    public override string DisplayName => "Get points definition";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        PointsParameters.Currency(points),
        new("variable", "Save in variable") { DefaultValue = "pointsDefinition" },
    ];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var currency = PointsParameters.CurrencyOf(Points, p);
        var c = Points.GetCurrency(currency) ?? throw new InvalidOperationException($"Unknown currency '{currency}'");
        var name = ExtrasText.Clean(p["variable"]).Trim('%');
        if (name.Length == 0) name = "pointsDefinition";
        context.Args[name] = JsonSerializer.Serialize(c, Storage.Json.Options);
        context.Args[name + ".name"] = c.Name;
        context.Args[name + ".singularName"] = c.SingularName;
        context.Args[name + ".symbol"] = c.Symbol;
        context.Args[name + ".variableName"] = c.VariableName;
        context.Args[name + ".enabled"] = c.Enabled;
        context.Args[name + ".maxBalance"] = c.MaxBalance;
        context.Args[name + ".onlyWhileLive"] = c.OnlyWhileLive;
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>
/// Streamer.bot "Award Points to All": pays every known user (seen in chat or holding a balance), or with "Only active
/// users" just the present viewers. %pointsAwarded% = how many users.
/// </summary>
public sealed class AwardPointsToAllSubAction(PointsService points) : PointsSubActionBase(points)
{
    public override string TypeId => "points.awardAll";
    public override string DisplayName => "Award points to all";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        PointsParameters.Platform(),
        PointsParameters.Amount("10"),
        new("onlyActive", "Only active (present) users", ParameterKind.Boolean) { DefaultValue = "false" },
        new("applyMultipliers", "Apply user/group multipliers", ParameterKind.Boolean) { DefaultValue = "false" },
        PointsParameters.Currency(points),
    ];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var currency = PointsParameters.CurrencyOf(Points, p);
        context.Args["pointsAwarded"] = Points.AwardAll(ExtrasText.Clean(p["platform"]), PointsParameters.ParseAmount(p["amount"]),
            p.GetBool("onlyActive"), p.GetBool("applyMultipliers"), currency);
        return Task.FromResult(SubActionResult.Next);
    }
}
