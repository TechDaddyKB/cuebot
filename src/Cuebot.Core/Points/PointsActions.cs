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
            sp.GetRequiredService<UserRegistry>(), sp.GetServices<IChatEventAdapter>(), groups: sp.GetService<GroupStore>()));
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
        services.AddSingleton<ISubActionHandler, AwardPointsToGroupSubAction>();
        services.AddSingleton<ISubActionHandler, AwardPointsToPresentSubAction>();
        services.AddSingleton<ISubActionHandler, GetPointsMultiplierSubAction>();
        services.AddSingleton<ISubActionHandler, SetGroupMultiplierSubAction>();
        services.AddSingleton<ISubActionHandler, SetUserMultiplierSubAction>();
        services.AddSingleton<ISubActionHandler, CheckPointsSubAction>();
        services.AddSingleton<ISubActionHandler, DeletePointsSubAction>();
        services.AddSingleton<ISubActionHandler, ResetUserPointsSubAction>();
        services.AddSingleton<ISubActionHandler, GetPointsDefinitionSubAction>();
        services.AddSingleton<ISubActionHandler, AwardPointsToAllSubAction>();
        services.AddSingleton<IScriptFunctionProvider, PointsScriptFunctions>();
        return services;
    }
}

public sealed class PointsTriggers(Storage.JsonFile<PointsSettings> settings) : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType("points.thresholdCrossed", ExtrasCategories.Points, "Points threshold reached", PointsEvents.ThresholdCrossed,
            [
                new ParameterDescriptor("threshold", "Threshold")
                {
                    Help = "Empty = any threshold set on the Points page",
                    Suggestions = () => All().SelectMany(c => c.Thresholds).Distinct().Order().Select(t => t.ToString(CultureInfo.InvariantCulture)),
                },
                new ParameterDescriptor("currency", "Currency")
                {
                    Help = "Empty = any currency",
                    Suggestions = () => All().Select(c => c.Name),
                },
            ],
            (e, s, _) =>
            {
                var wanted = s["threshold"].Trim();
                var currency = s["currency"].Trim();
                return (wanted.Length == 0 || string.Equals(e.Args.GetString("threshold"), wanted, StringComparison.Ordinal))
                       && (currency.Length == 0 || IsCurrency(e.Args.GetString("currency"), currency));
            }),
    ];

    private PointsCurrency[] All() => settings.Value.Currencies.Prepend(settings.Value).ToArray();

    /// <summary>The event names the currency; the filter may use its name, variable or singular name.</summary>
    private bool IsCurrency(string eventCurrency, string filter)
    {
        if (string.Equals(eventCurrency, filter, StringComparison.OrdinalIgnoreCase)) return true;
        var c = All().FirstOrDefault(x => string.Equals(x.Name, eventCurrency, StringComparison.OrdinalIgnoreCase));
        return c is not null && (string.Equals(c.VariableName, filter, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(c.SingularName, filter, StringComparison.OrdinalIgnoreCase));
    }
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

    public static ParameterDescriptor Platform(string help = "Empty = every platform") =>
        new("platform", "Platform") { Help = help, Suggestions = () => KnownPlatforms };

    public static ParameterDescriptor Amount(string defaultValue = "", string? help = null) =>
        new("amount", "Amount") { DefaultValue = defaultValue, Help = help ?? "Whole number", Suggestions = () => ["%input0%", "%input1%", "10", "100"] };

    /// <summary>The currency to use; empty (as in every action saved before currencies existed) = the default one.</summary>
    public static ParameterDescriptor Currency(PointsService points) =>
        new("currency", "Currency") { Help = "Empty = the default currency", Suggestions = () => points.CurrencyNames };

    /// <summary>The currency parameter, checked (null = the default).</summary>
    /// <exception cref="InvalidOperationException">Unknown currency (the message lists the known ones).</exception>
    public static string? CurrencyOf(PointsService points, ParameterValues p)
    {
        var c = ExtrasText.Clean(p["currency"]);
        return c.Length == 0 ? null : points.NameOf(c);
    }

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
        bool fallBackToCaller = true, string? currency = null)
    {
        var user = Find(points, context.Args, p[prefix + "platform"], p[prefix + "userId"], p[prefix + "userName"], fallBackToCaller);
        if (user is { } u) return u;
        var name = ExtrasText.Clean(p[prefix + "userName"]);
        throw new InvalidOperationException(name.Length > 0
            ? $"Unknown user '{name}': they need to chat (or have {points.NameOf(currency)}) first"
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

    /// <summary>A multiplier such as "1.5" or "2x"; empty or "none" = null (remove).</summary>
    public static decimal? ParseMultiplier(string text)
    {
        var t = ExtrasText.Clean(text).TrimEnd('x', 'X').Trim();
        if (t.Length == 0 || string.Equals(t, "none", StringComparison.OrdinalIgnoreCase)) return null;
        if (decimal.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m >= 0) return m;
        throw new InvalidOperationException($"'{t}' is not a multiplier (use a number like 1.5, or empty to remove)");
    }

    /// <summary>Streamer.bot's "Destination Variable": an extra argument that receives the balance (blank = none).</summary>
    public static ParameterDescriptor Destination(string defaultValue = "") =>
        new("destination", "Also save the balance in variable") { DefaultValue = defaultValue, Help = "e.g. userPoints; blank = only %points%" };

    public static void StoreDestination(ActionRunContext context, ParameterValues p, long balance)
    {
        var name = ExtrasText.Clean(p["destination"]).Trim('%');
        if (name.Length > 0) context.Args[name] = balance;
    }

    /// <summary>points, pointsFormatted, pointsName, pointsCurrency, pointsUser, pointsUserId, pointsPlatform.</summary>
    public static void Store(ActionRunContext context, PointsService points, PointsUser user, long balance, string prefix = "points",
        string? currency = null)
    {
        context.Args[prefix] = balance;
        context.Args[prefix + "Formatted"] = points.Format(balance, currency);
        context.Args[prefix + "User"] = user.UserName;
        context.Args[prefix + "UserId"] = user.UserId;
        context.Args[prefix + "Platform"] = user.Platform;
        context.Args["pointsName"] = points.NameOf(currency);
        context.Args["pointsCurrency"] = points.NameOf(currency);
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
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        .. PointsParameters.User(), PointsParameters.Amount("10"), PointsParameters.Currency(points),
        new("applyMultipliers", "Apply user/group multipliers", ParameterKind.Boolean) { DefaultValue = "false" },
        PointsParameters.Destination(),
    ];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var currency = PointsParameters.CurrencyOf(Points, p);
        var user = PointsParameters.Require(Points, context, p, currency: currency);
        var amount = PointsParameters.ParseAmount(p["amount"]);
        if (p.GetBool("applyMultipliers")) amount = Points.Multiplied(amount, user.Platform, user.UserId, user.UserName, currency);
        var balance = Points.Add(user.Platform, user.UserId, user.UserName, amount, currency: currency);
        PointsParameters.Store(context, Points, user, balance, currency: currency);
        PointsParameters.StoreDestination(context, p, balance);
        context.Args["pointsAdded"] = amount;
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class RemovePointsSubAction(PointsService points) : PointsSubActionBase(points)
{
    public override string TypeId => "points.remove";
    public override string DisplayName => "Remove points";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [.. PointsParameters.User(), PointsParameters.Amount("10", "Whole number or 'all'; the balance never goes below 0"), PointsParameters.Currency(points)];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var currency = PointsParameters.CurrencyOf(Points, p);
        var user = PointsParameters.Require(Points, context, p, currency: currency);
        var before = Points.Get(user.Platform, user.UserId, currency);
        var amount = PointsParameters.ParseAmount(p["amount"], before);
        var balance = Points.Remove(user.Platform, user.UserId, user.UserName, amount, currency);
        PointsParameters.Store(context, Points, user, balance, currency: currency);
        context.Args["pointsRemoved"] = before - balance;
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class SetPointsSubAction(PointsService points) : PointsSubActionBase(points)
{
    public override string TypeId => "points.set";
    public override string DisplayName => "Set points";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [.. PointsParameters.User(), PointsParameters.Amount("0"), PointsParameters.Currency(points)];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var currency = PointsParameters.CurrencyOf(Points, p);
        var user = PointsParameters.Require(Points, context, p, currency: currency);
        var balance = Points.Set(user.Platform, user.UserId, user.UserName, PointsParameters.ParseAmount(p["amount"]), currency);
        PointsParameters.Store(context, Points, user, balance, currency: currency);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Gets a balance and rank (for "!points [name]": user name defaults to %input0%, falling back to the caller).</summary>
public sealed class GetPointsSubAction(PointsService points) : PointsSubActionBase(points)
{
    public override string TypeId => "points.get";
    public override string DisplayName => "Get points";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [.. PointsParameters.User("%input0%"), PointsParameters.Currency(points)];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var currency = PointsParameters.CurrencyOf(Points, p);
        var user = PointsParameters.Require(Points, context, p, currency: currency);
        var balance = Points.Get(user.Platform, user.UserId, currency);
        PointsParameters.Store(context, Points, user, balance, currency: currency);
        context.Args["pointsRank"] = Points.Rank(user.Platform, user.UserId, currency);
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
        PointsParameters.Currency(points),
    ];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var currency = PointsParameters.CurrencyOf(Points, p);
        var user = PointsParameters.Require(Points, context, p, currency: currency);
        var current = Points.Get(user.Platform, user.UserId, currency);
        var amount = PointsParameters.ParseAmount(p["amount"], current);
        if (amount < 0) throw new InvalidOperationException("Cannot spend a negative amount");
        var ok = Points.TrySpend(user.Platform, user.UserId, user.UserName, amount, out var balance, currency);
        PointsParameters.Store(context, Points, user, balance, currency: currency);
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
        PointsParameters.Currency(points),
    ];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var currency = PointsParameters.CurrencyOf(Points, p);
        var from = PointsParameters.Require(Points, context, p, currency: currency);
        try
        {
            var toPlatform = ExtrasText.Clean(p["toPlatform"]) is { Length: > 0 } tp ? tp : from.Platform;
            var to = PointsParameters.Find(Points, context.Args, toPlatform, p["toUserId"], p["toUserName"], fallBackToCaller: false)
                     ?? throw new InvalidOperationException(ExtrasText.Clean(p["toUserName"]) is { Length: > 0 } n
                         ? $"Unknown user '{n.TrimStart('@')}'"
                         : "No user to give to");
            var amount = PointsParameters.ParseAmount(p["amount"], Points.Get(from.Platform, from.UserId, currency));
            var (fromBalance, toBalance) = Points.Transfer(from.Platform, from.UserId, from.UserName, to.Platform, to.UserId, to.UserName, amount, currency);
            PointsParameters.Store(context, Points, from, fromBalance, currency: currency);
            PointsParameters.Store(context, Points, to, toBalance, "targetPoints", currency);
            context.Args["pointsTransferred"] = amount;
            context.Args["pointsError"] = "";
            return Task.FromResult(SubActionResult.TakeBranch(Transferred));
        }
        catch (InvalidOperationException ex)
        {
            PointsParameters.Store(context, Points, from, Points.Get(from.Platform, from.UserId, currency), currency: currency);
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
        new("offset", "Skip the first (offset)", ParameterKind.Number) { DefaultValue = "0", Help = "0 starts at first place; 5 starts at sixth" },
        PointsParameters.Platform(),
        PointsParameters.Currency(points),
    ];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var currency = PointsParameters.CurrencyOf(Points, p);
        var offset = Math.Max(0, p.GetInt("offset"));
        var board = Points.Leaderboard(ExtrasText.Clean(p["platform"]), 0, currency).Skip(offset).Take(Math.Max(1, p.GetInt("count", 5))).ToList();
        context.Args["leaderboard"] = string.Join(", ", board.Select(e => $"{e.Rank}. {e.UserName} ({Points.Format(e.Points, currency)})"));
        context.Args["leaderboardCount"] = board.Count;
        context.Args["leaderboardJson"] = JsonSerializer.Serialize(board, Storage.Json.Options);
        context.Args["pointsCurrency"] = Points.NameOf(currency);
        for (var i = 0; i < board.Count; i++)
        {
            var key = "leaderboard" + (i + 1).ToString(CultureInfo.InvariantCulture);
            context.Args[key + "User"] = board[i].UserName;
            context.Args[key + "UserId"] = board[i].UserId;
            context.Args[key + "Platform"] = board[i].Platform;
            context.Args[key + "Points"] = board[i].Points;
            context.Args[key + "Formatted"] = Points.Format(board[i].Points, currency);
            context.Args[key + "Rank"] = board[i].Rank;
        }
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class ResetAllPointsSubAction(PointsService points) : PointsSubActionBase(points)
{
    public override string TypeId => "points.resetAll";
    public override string DisplayName => "Reset everyone's points";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [PointsParameters.Platform(), PointsParameters.Currency(points)];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        context.Args["pointsReset"] = Points.ResetAll(ExtrasText.Clean(p["platform"]), PointsParameters.CurrencyOf(Points, p));
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Pays every member of a user group (%pointsAwarded% = how many users).</summary>
public sealed class AwardPointsToGroupSubAction(PointsService points, GroupStore groups) : PointsSubActionBase(points)
{
    public override string TypeId => "points.awardGroup";
    public override string DisplayName => "Award points to group";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("group", "Group") { Suggestions = () => groups.Names },
        PointsParameters.Amount("10"),
        new("applyMultipliers", "Apply multipliers", ParameterKind.Boolean) { DefaultValue = "false" },
        PointsParameters.Currency(points),
    ];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var currency = PointsParameters.CurrencyOf(Points, p);
        var group = ExtrasText.Clean(p["group"]);
        if (group.Length == 0) throw new InvalidOperationException("No group given");
        context.Args["pointsAwarded"] = Points.AwardGroup(group, PointsParameters.ParseAmount(p["amount"]), p.GetBool("applyMultipliers"), currency);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Pays every present viewer (%pointsAwarded% = how many users).</summary>
public sealed class AwardPointsToPresentSubAction(PointsService points) : PointsSubActionBase(points)
{
    public override string TypeId => "points.awardPresent";
    public override string DisplayName => "Award points to present viewers";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        PointsParameters.Platform(),
        PointsParameters.Amount("10"),
        new("applyMultipliers", "Apply multipliers", ParameterKind.Boolean) { DefaultValue = "true" },
        PointsParameters.Currency(points),
    ];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var currency = PointsParameters.CurrencyOf(Points, p);
        context.Args["pointsAwarded"] = Points.AwardPresent(ExtrasText.Clean(p["platform"]), PointsParameters.ParseAmount(p["amount"]),
            p.GetBool("applyMultipliers", true), currency);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>The user's effective multiplier (%pointsMultiplier%) and their own one (%pointsUserMultiplier%, 1 when unset).</summary>
public sealed class GetPointsMultiplierSubAction(PointsService points) : PointsSubActionBase(points)
{
    public override string TypeId => "points.getMultiplier";
    public override string DisplayName => "Get points multiplier";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } = [.. PointsParameters.User(), PointsParameters.Currency(points)];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var currency = PointsParameters.CurrencyOf(Points, p);
        var user = PointsParameters.Require(Points, context, p, currency: currency);
        context.Args["pointsMultiplier"] = Points.Multiplier(user.Platform, user.UserId, user.UserName, currency);
        context.Args["pointsUserMultiplier"] = Points.GetUserMultiplier(user.Platform, user.UserId, currency) ?? 1m;
        context.Args["pointsUser"] = user.UserName;
        context.Args["pointsCurrency"] = Points.NameOf(currency);
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class SetGroupMultiplierSubAction(PointsService points, GroupStore groups) : PointsSubActionBase(points)
{
    public override string TypeId => "points.setGroupMultiplier";
    public override string DisplayName => "Set group points multiplier";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("group", "Group") { Suggestions = () => groups.Names },
        new("multiplier", "Multiplier") { DefaultValue = "2", Help = "e.g. 1.5; empty removes the group's multiplier" },
        PointsParameters.Currency(points),
    ];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        Points.SetGroupMultiplier(ExtrasText.Clean(p["group"]), PointsParameters.ParseMultiplier(p["multiplier"]), PointsParameters.CurrencyOf(Points, p));
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class SetUserMultiplierSubAction(PointsService points) : PointsSubActionBase(points)
{
    public override string TypeId => "points.setUserMultiplier";
    public override string DisplayName => "Set user points multiplier";
    public override IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        .. PointsParameters.User(),
        new("multiplier", "Multiplier") { DefaultValue = "2", Help = "e.g. 1.5; empty removes the user's multiplier" },
        PointsParameters.Currency(points),
    ];

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var currency = PointsParameters.CurrencyOf(Points, p);
        var user = PointsParameters.Require(Points, context, p, currency: currency);
        Points.SetUserMultiplier(user.Platform, user.UserId, PointsParameters.ParseMultiplier(p["multiplier"]), currency);
        context.Args["pointsMultiplier"] = Points.Multiplier(user.Platform, user.UserId, user.UserName, currency);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>
/// Script functions. User arguments are platform, user (name) and userId; omitted ones default to the triggering user.
/// Every function takes an optional "currency" (empty = the default currency).
/// </summary>
public sealed class PointsScriptFunctions(PointsService points) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("points.get", "A user's balance", ["platform", "user", "userId", "currency"],
            (ctx, a, _) => Guard(() => { var u = User(ctx, a); return points.Get(u.Platform, u.UserId, Currency(a)); })),
        new("points.add", "Add points (negative removes); returns the balance", ["platform", "user", "userId", "amount", "currency"],
            (ctx, a, _) => Guard(() => { var u = User(ctx, a); return points.Add(u.Platform, u.UserId, u.UserName, Amount(a), currency: Currency(a)); })),
        new("points.remove", "Remove points (not below 0); returns the balance", ["platform", "user", "userId", "amount", "currency"],
            (ctx, a, _) => Guard(() => { var u = User(ctx, a); return points.Remove(u.Platform, u.UserId, u.UserName, Amount(a), Currency(a)); })),
        new("points.set", "Set a balance; returns it", ["platform", "user", "userId", "amount", "currency"],
            (ctx, a, _) => Guard(() => { var u = User(ctx, a); return points.Set(u.Platform, u.UserId, u.UserName, Amount(a), Currency(a)); })),
        new("points.spend", "Spend points if the user has enough; returns true when paid", ["platform", "user", "userId", "amount", "currency"],
            (ctx, a, _) => Guard(() => { var u = User(ctx, a); return points.TrySpend(u.Platform, u.UserId, u.UserName, Amount(a), out long _, Currency(a)); })),
        new("points.transfer", "Give points from the user to 'to' (a name; toUserId/toPlatform optional); returns {from, to}",
            ["platform", "user", "userId", "to", "toUserId", "toPlatform", "amount", "currency"], (ctx, a, _) => Guard(() =>
            {
                var from = User(ctx, a);
                var to = PointsParameters.Find(points, ctx.Args, a.GetString("toPlatform") is { Length: > 0 } tp ? tp : from.Platform,
                    a.GetString("toUserId"), a.GetString("to"), fallBackToCaller: false) ?? throw new ScriptException("Unknown target user");
                var (f, t) = points.Transfer(from.Platform, from.UserId, from.UserName, to.Platform, to.UserId, to.UserName, Amount(a), Currency(a));
                return new Dictionary<string, object?> { ["from"] = f, ["to"] = t };
            })),
        new("points.leaderboard", "Top balances as [{rank, platform, userId, userName, points}]", ["platform", "count", "currency"],
            (_, a, _) => Guard(() => points.Leaderboard(a.GetString("platform"),
                int.TryParse(a.GetString("count", "10"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var c) ? c : 10, Currency(a)))),
        new("points.format", "Formats an amount with the currency name or symbol", ["amount", "currency"],
            (_, a, _) => Guard(() => points.Format(Amount(a), Currency(a)))),
        new("points.currencies", "Every currency as [{name, singularName, symbol, variableName, enabled}], the default first", [],
            (_, _, _) => Task.FromResult<object?>(points.Currencies.Select(c => new Dictionary<string, object?>
            {
                ["name"] = c.Name, ["singularName"] = c.SingularName, ["symbol"] = c.Symbol, ["variableName"] = c.VariableName, ["enabled"] = c.Enabled,
            }).ToArray())),
        new("points.multiplier", "A user's effective earning multiplier", ["platform", "user", "userId", "currency"],
            (ctx, a, _) => Guard(() => { var u = User(ctx, a); return points.Multiplier(u.Platform, u.UserId, u.UserName, Currency(a)); })),
        new("points.setUserMultiplier", "Set a user's own multiplier (empty removes it)", ["platform", "user", "userId", "multiplier", "currency"],
            (ctx, a, _) => Guard(() =>
            {
                var u = User(ctx, a);
                points.SetUserMultiplier(u.Platform, u.UserId, PointsParameters.ParseMultiplier(a.GetString("multiplier")), Currency(a));
                return true;
            })),
        new("points.setGroupMultiplier", "Set a user group's multiplier (empty removes it)", ["group", "multiplier", "currency"],
            (_, a, _) => Guard(() =>
            {
                points.SetGroupMultiplier(a.GetString("group"), PointsParameters.ParseMultiplier(a.GetString("multiplier")), Currency(a));
                return true;
            })),
        new("points.awardGroup", "Pay every member of a group; returns how many were paid", ["group", "amount", "applyMultipliers", "currency"],
            (_, a, _) => Guard(() => points.AwardGroup(a.GetString("group"), Amount(a),
                string.Equals(a.GetString("applyMultipliers"), "true", StringComparison.OrdinalIgnoreCase), Currency(a)))),
    ];

    /// <summary>Turns the service's InvalidOperationException (unknown currency, not enough points...) into a script error.</summary>
    private static Task<object?> Guard<T>(Func<T> body)
    {
        try
        {
            return Task.FromResult<object?>(body());
        }
        catch (InvalidOperationException ex)
        {
            throw new ScriptException(ex.Message);
        }
    }

    private static string? Currency(JsonElement a) => a.GetString("currency") is { Length: > 0 } c ? c : null;

    private PointsUser User(ActionRunContext ctx, JsonElement a)
    {
        var name = a.GetString("user");
        return PointsParameters.Find(points, ctx.Args, a.GetString("platform"), a.GetString("userId"), name)
               ?? throw new ScriptException(name.Length > 0 ? $"Unknown user '{name}'" : "No user: pass platform and user or userId");
    }

    private static long Amount(JsonElement a) => PointsParameters.ParseAmount(a.GetString("amount"));
}
