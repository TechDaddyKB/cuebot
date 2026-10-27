using System.Globalization;
using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Cuebot.Core.Users;
using Cuebot.Core.Variables;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Points;

public static class PointsFeatures
{
    public static IServiceCollection AddPointsFeatures(this IServiceCollection services)
    {
        services.AddSettingsFile<PointsSettings>("points.json");
        services.AddSingleton(sp => new PointsService(sp.GetRequiredService<UserVariables>(),
            sp.GetRequiredService<Storage.JsonFile<PointsSettings>>(), sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>(),
            sp.GetRequiredService<UserRegistry>(), sp.GetServices<IChatEventAdapter>()));
        // Ticked by the timer service (presence payouts); resolving TimerService at start-up creates the service.
        services.AddSingleton<IPeriodicService>(sp => sp.GetRequiredService<PointsService>());
        services.AddSingleton<ITriggerProvider, PointsTriggers>();
        services.AddSingleton<ISubActionHandler, AddPointsSubAction>();
        services.AddSingleton<ISubActionHandler, RemovePointsSubAction>();
        services.AddSingleton<ISubActionHandler, SetPointsSubAction>();
        services.AddSingleton<ISubActionHandler, GetPointsSubAction>();
        services.AddSingleton<ISubActionHandler, SpendPointsSubAction>();
        services.AddSingleton<ISubActionHandler, TransferPointsSubAction>();
        services.AddSingleton<ISubActionHandler, PointsLeaderboardSubAction>();
        services.AddSingleton<ISubActionHandler, ResetAllPointsSubAction>();
        services.AddSingleton<IScriptFunctionProvider, PointsScriptFunctions>();
        return services;
    }
}

public sealed class PointsTriggers(Storage.JsonFile<PointsSettings> settings) : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType("points.thresholdCrossed", ExtrasCategories.Points, "Points threshold reached", PointsEvents.ThresholdCrossed,
            [new ParameterDescriptor("threshold", "Threshold")
            {
                Help = "Empty = any threshold set on the Points page",
                Suggestions = () => settings.Value.Thresholds.ToArray().Select(t => t.ToString(CultureInfo.InvariantCulture)),
            }],
            (e, s, _) =>
            {
                var wanted = s["threshold"].Trim();
                return wanted.Length == 0 || string.Equals(e.Args.GetString("threshold"), wanted, StringComparison.Ordinal);
            }),
    ];
}

/// <summary>A resolved points user.</summary>
internal readonly record struct PointsUser(string Platform, string UserId, string UserName);

internal static class PointsParameters
{
    private static readonly string[] KnownPlatforms = ["twitch", "youtube", "kick", "rumble"];

    public static ParameterDescriptor[] User(string userNameDefault = "") =>
    [
        new("platform", "Platform") { Help = "Empty = the platform of the trigger", Suggestions = () => KnownPlatforms },
        new("userName", "User name") { DefaultValue = userNameDefault, Help = "Empty = the user who triggered the action; a known user is looked up by name", Suggestions = () => ["%user%", "%input0%"] },
        new("userId", "User id") { Help = "Optional; wins over the name" },
    ];

    public static ParameterDescriptor Amount(string defaultValue = "", string? help = null) =>
        new("amount", "Amount") { DefaultValue = defaultValue, Help = help ?? "Whole number", Suggestions = () => ["%input0%", "%input1%", "10", "100"] };

    /// <summary>
    /// Explicit platform/id/name win; empty ones fall back to the triggering user (platform, userId, user). A user
    /// given only by name is looked up among known chatters and points holders.
    /// </summary>
    public static PointsUser? Find(PointsService points, IReadOnlyDictionary<string, object?> args, string platform, string userId,
        string userName, bool fallBackToCaller = true)
    {
        var p = ExtrasText.Clean(platform);
        var id = ExtrasText.Clean(userId);
        var name = ExtrasText.Clean(userName).TrimStart('@');
        if (p.Length == 0) p = UserVariables.PlatformFromArgs(args) ?? "";
        if (p.Length == 0) return null;
        if (id.Length == 0 && name.Length == 0 && fallBackToCaller)
        {
            id = args.GetString("userId");
            name = args.GetString("user");
            if (name.Length == 0) name = args.GetString("userName");
        }
        if (id.Length == 0 && name.Length > 0) id = points.FindUserId(p, name) ?? "";
        return id.Length == 0 ? null : new PointsUser(p, id, name.Length > 0 ? name : id);
    }

    public static PointsUser Require(PointsService points, ActionRunContext context, ParameterValues p, string prefix = "",
        bool fallBackToCaller = true)
    {
        var user = Find(points, context.Args, p[prefix + "platform"], p[prefix + "userId"], p[prefix + "userName"], fallBackToCaller);
        if (user is { } u) return u;
        var name = ExtrasText.Clean(p[prefix + "userName"]);
        throw new InvalidOperationException(name.Length > 0
            ? $"Unknown user '{name}': they need to chat (or have {points.CurrencyName}) first"
            : "No user: set the platform and user name, or run from a trigger that provides platform and userId");
    }

    /// <summary>Parses a whole amount; "all" means <paramref name="all"/> (the current balance).</summary>
    public static long ParseAmount(string text, long? all = null)
    {
        var t = ExtrasText.Clean(text).Replace(",", "").Replace("_", "");
        if (all is { } a && string.Equals(t, "all", StringComparison.OrdinalIgnoreCase)) return a;
        if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n)) return n;
        if (decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == decimal.Truncate(d)
            && d is >= long.MinValue and <= long.MaxValue) return (long)d;
        throw new InvalidOperationException(t.Length == 0 ? "No amount given" : $"'{t}' is not a whole number");
    }

    /// <summary>points, pointsFormatted, pointsName, pointsUser, pointsUserId, pointsPlatform.</summary>
    public static void Store(ActionRunContext context, PointsService points, PointsUser user, long balance, string prefix = "points")
    {
        context.Args[prefix] = balance;
        context.Args[prefix + "Formatted"] = points.Format(balance);
        context.Args[prefix + "User"] = user.UserName;
        context.Args[prefix + "UserId"] = user.UserId;
        context.Args[prefix + "Platform"] = user.Platform;
        context.Args["pointsName"] = points.CurrencyName;
    }
}

public abstract class PointsSubActionBase(PointsService points) : ISubActionHandler
{
    protected PointsService Points => points;
    public abstract string TypeId { get; }
    public string Category => ExtrasCategories.Points;
    public abstract string DisplayName { get; }
    public virtual IReadOnlyList<string> Branches => [];
    public abstract IReadOnlyList<ParameterDescriptor> Parameters { get; }
    public abstract Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct);
}

public sealed class AddPointsSubAction(PointsService points) : PointsSubActionBase(points)
{
    public override string TypeId => "points.add";
    public override string DisplayName => "Add points";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [.. PointsParameters.User(), PointsParameters.Amount("10")];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var user = PointsParameters.Require(Points, context, p);
        var amount = PointsParameters.ParseAmount(p["amount"]);
        var balance = Points.Add(user.Platform, user.UserId, user.UserName, amount);
        PointsParameters.Store(context, Points, user, balance);
        context.Args["pointsAdded"] = amount;
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class RemovePointsSubAction(PointsService points) : PointsSubActionBase(points)
{
    public override string TypeId => "points.remove";
    public override string DisplayName => "Remove points";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [.. PointsParameters.User(), PointsParameters.Amount("10", "Whole number or 'all'; the balance never goes below 0")];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var user = PointsParameters.Require(Points, context, p);
        var before = Points.Get(user.Platform, user.UserId);
        var amount = PointsParameters.ParseAmount(p["amount"], before);
        var balance = Points.Remove(user.Platform, user.UserId, user.UserName, amount);
        PointsParameters.Store(context, Points, user, balance);
        context.Args["pointsRemoved"] = before - balance;
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class SetPointsSubAction(PointsService points) : PointsSubActionBase(points)
{
    public override string TypeId => "points.set";
    public override string DisplayName => "Set points";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [.. PointsParameters.User(), PointsParameters.Amount("0")];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var user = PointsParameters.Require(Points, context, p);
        var balance = Points.Set(user.Platform, user.UserId, user.UserName, PointsParameters.ParseAmount(p["amount"]));
        PointsParameters.Store(context, Points, user, balance);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Gets a balance and rank (for "!points [name]": user name defaults to %input0%, falling back to the caller).</summary>
public sealed class GetPointsSubAction(PointsService points) : PointsSubActionBase(points)
{
    public override string TypeId => "points.get";
    public override string DisplayName => "Get points";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = PointsParameters.User("%input0%");

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var user = PointsParameters.Require(Points, context, p);
        var balance = Points.Get(user.Platform, user.UserId);
        PointsParameters.Store(context, Points, user, balance);
        context.Args["pointsRank"] = Points.Rank(user.Platform, user.UserId);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Takes points only when the user has enough: branch "Paid" or "Not enough" (for redeemable commands).</summary>
public sealed class SpendPointsSubAction(PointsService points) : PointsSubActionBase(points)
{
    public const string Paid = "Paid";
    public const string NotEnough = "Not enough";

    public override string TypeId => "points.spend";
    public override string DisplayName => "Spend points";
    public override IReadOnlyList<string> Branches { get; } = [Paid, NotEnough];
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        .. PointsParameters.User(), PointsParameters.Amount("100", "Whole number or 'all'"),
        new("stopIfNotEnough", "Stop the action when not enough", ParameterKind.Boolean) { DefaultValue = "false" },
    ];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var user = PointsParameters.Require(Points, context, p);
        var current = Points.Get(user.Platform, user.UserId);
        var amount = PointsParameters.ParseAmount(p["amount"], current);
        if (amount < 0) throw new InvalidOperationException("Cannot spend a negative amount");
        var ok = Points.TrySpend(user.Platform, user.UserId, user.UserName, amount, out var balance);
        PointsParameters.Store(context, Points, user, balance);
        context.Args["pointsSpent"] = ok;
        context.Args["pointsCost"] = amount;
        context.Args["pointsMissing"] = ok ? 0 : amount - balance;
        if (!ok && p.GetBool("stopIfNotEnough")) return Task.FromResult(SubActionResult.Stop);
        return Task.FromResult(SubActionResult.TakeBranch(ok ? Paid : NotEnough));
    }
}

/// <summary>Gives points from one user to another ("!give name amount"): branch "Transferred" or "Failed" (%pointsError%).</summary>
public sealed class TransferPointsSubAction(PointsService points) : PointsSubActionBase(points)
{
    public const string Transferred = "Transferred";
    public const string Failed = "Failed";

    public override string TypeId => "points.transfer";
    public override string DisplayName => "Transfer points";
    public override IReadOnlyList<string> Branches { get; } = [Transferred, Failed];
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        .. PointsParameters.User(),
        new("toUserName", "To user name") { DefaultValue = "%input0%", Suggestions = () => ["%input0%", "%targetUser%"] },
        new("toUserId", "To user id") { Help = "Optional; wins over the name" },
        new("toPlatform", "To platform") { Help = "Empty = same platform as the giver" },
        PointsParameters.Amount("%input1%", "Whole number or 'all'"),
    ];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var from = PointsParameters.Require(Points, context, p);
        try
        {
            var toPlatform = ExtrasText.Clean(p["toPlatform"]) is { Length: > 0 } tp ? tp : from.Platform;
            var to = PointsParameters.Find(Points, context.Args, toPlatform, p["toUserId"], p["toUserName"], fallBackToCaller: false)
                     ?? throw new InvalidOperationException(ExtrasText.Clean(p["toUserName"]) is { Length: > 0 } n
                         ? $"Unknown user '{n.TrimStart('@')}'"
                         : "No user to give to");
            var amount = PointsParameters.ParseAmount(p["amount"], Points.Get(from.Platform, from.UserId));
            var (fromBalance, toBalance) = Points.Transfer(from.Platform, from.UserId, from.UserName, to.Platform, to.UserId, to.UserName, amount);
            PointsParameters.Store(context, Points, from, fromBalance);
            PointsParameters.Store(context, Points, to, toBalance, "targetPoints");
            context.Args["pointsTransferred"] = amount;
            context.Args["pointsError"] = "";
            return Task.FromResult(SubActionResult.TakeBranch(Transferred));
        }
        catch (InvalidOperationException ex)
        {
            PointsParameters.Store(context, Points, from, Points.Get(from.Platform, from.UserId));
            context.Args["pointsTransferred"] = 0;
            context.Args["pointsError"] = ex.Message;
            return Task.FromResult(SubActionResult.TakeBranch(Failed));
        }
    }
}

/// <summary>
/// Top balances into arguments: %leaderboard% ("1. Alice (500 points), 2. ..."), %leaderboardCount%,
/// %leaderboardJson%, and %leaderboard1User%, %leaderboard1Points%, %leaderboard1Formatted% ... per place.
/// </summary>
public sealed class PointsLeaderboardSubAction(PointsService points) : PointsSubActionBase(points)
{
    public override string TypeId => "points.leaderboard";
    public override string DisplayName => "Get points leaderboard";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("count", "How many", ParameterKind.Number) { DefaultValue = "5" },
        new("platform", "Platform") { Help = "Empty = every platform", Suggestions = () => ["twitch", "youtube", "kick", "rumble"] },
    ];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var board = Points.Leaderboard(ExtrasText.Clean(p["platform"]), Math.Max(1, p.GetInt("count", 5)));
        context.Args["leaderboard"] = string.Join(", ", board.Select(e => $"{e.Rank}. {e.UserName} ({Points.Format(e.Points)})"));
        context.Args["leaderboardCount"] = board.Count;
        context.Args["leaderboardJson"] = JsonSerializer.Serialize(board, Storage.Json.Options);
        for (var i = 0; i < board.Count; i++)
        {
            var key = "leaderboard" + (i + 1).ToString(CultureInfo.InvariantCulture);
            context.Args[key + "User"] = board[i].UserName;
            context.Args[key + "UserId"] = board[i].UserId;
            context.Args[key + "Platform"] = board[i].Platform;
            context.Args[key + "Points"] = board[i].Points;
            context.Args[key + "Formatted"] = Points.Format(board[i].Points);
            context.Args[key + "Rank"] = board[i].Rank;
        }
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class ResetAllPointsSubAction(PointsService points) : PointsSubActionBase(points)
{
    public override string TypeId => "points.resetAll";
    public override string DisplayName => "Reset everyone's points";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("platform", "Platform") { Help = "Empty = every platform", Suggestions = () => ["twitch", "youtube", "kick", "rumble"] },
    ];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        context.Args["pointsReset"] = Points.ResetAll(ExtrasText.Clean(p["platform"]));
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>
/// Script functions. User arguments are platform, user (name) and userId; omitted ones default to the triggering user.
/// </summary>
public sealed class PointsScriptFunctions(PointsService points) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("points.get", "A user's balance", ["platform", "user", "userId"],
            (ctx, a, _) => Result(points.Get(User(ctx, a).Platform, User(ctx, a).UserId))),
        new("points.add", "Add points (negative removes); returns the balance", ["platform", "user", "userId", "amount"],
            (ctx, a, _) => { var u = User(ctx, a); return Result(points.Add(u.Platform, u.UserId, u.UserName, Amount(a))); }),
        new("points.remove", "Remove points (not below 0); returns the balance", ["platform", "user", "userId", "amount"],
            (ctx, a, _) => { var u = User(ctx, a); return Result(points.Remove(u.Platform, u.UserId, u.UserName, Amount(a))); }),
        new("points.set", "Set a balance; returns it", ["platform", "user", "userId", "amount"],
            (ctx, a, _) => { var u = User(ctx, a); return Result(points.Set(u.Platform, u.UserId, u.UserName, Amount(a))); }),
        new("points.spend", "Spend points if the user has enough; returns true when paid", ["platform", "user", "userId", "amount"],
            (ctx, a, _) => { var u = User(ctx, a); return Task.FromResult<object?>(points.TrySpend(u.Platform, u.UserId, u.UserName, Amount(a), out long _)); }),
        new("points.transfer", "Give points from the user to 'to' (a name; toUserId/toPlatform optional); returns {from, to}",
            ["platform", "user", "userId", "to", "toUserId", "toPlatform", "amount"], (ctx, a, _) =>
            {
                var from = User(ctx, a);
                var to = PointsParameters.Find(points, ctx.Args, a.GetString("toPlatform") is { Length: > 0 } tp ? tp : from.Platform,
                    a.GetString("toUserId"), a.GetString("to"), fallBackToCaller: false) ?? throw new ScriptException("Unknown target user");
                try
                {
                    var (f, t) = points.Transfer(from.Platform, from.UserId, from.UserName, to.Platform, to.UserId, to.UserName, Amount(a));
                    return Task.FromResult<object?>(new Dictionary<string, object?> { ["from"] = f, ["to"] = t });
                }
                catch (InvalidOperationException ex)
                {
                    throw new ScriptException(ex.Message);
                }
            }),
        new("points.leaderboard", "Top balances as [{rank, platform, userId, userName, points}]", ["platform", "count"],
            (_, a, _) => Task.FromResult<object?>(points.Leaderboard(a.GetString("platform"),
                int.TryParse(a.GetString("count", "10"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 10))),
        new("points.format", "Formats an amount with the currency name or symbol", ["amount"], (_, a, _) => Task.FromResult<object?>(points.Format(Amount(a)))),
    ];

    private static Task<object?> Result(long value) => Task.FromResult<object?>(value);

    private PointsUser User(ActionRunContext ctx, JsonElement a)
    {
        var name = a.GetString("user");
        return PointsParameters.Find(points, ctx.Args, a.GetString("platform"), a.GetString("userId"), name)
               ?? throw new ScriptException(name.Length > 0 ? $"Unknown user '{name}'" : "No user: pass platform and user or userId");
    }

    private static long Amount(JsonElement a)
    {
        try
        {
            return PointsParameters.ParseAmount(a.GetString("amount"));
        }
        catch (InvalidOperationException ex)
        {
            throw new ScriptException(ex.Message);
        }
    }
}
