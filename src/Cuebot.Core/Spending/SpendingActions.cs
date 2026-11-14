using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays.Session;
using Cuebot.Core.Scripting;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Spending;

public static class SpendingFeatures
{
    public const string Category = "Spending";

    public static IServiceCollection AddSpendingFeatures(this IServiceCollection services)
    {
        services.AddSingleton<IExchangeRateSource>(sp => new WebExchangeRateSource(sp.GetRequiredService<HttpClient>()));
        services.AddSingleton(sp => new SpendingLedger(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<ChatHub>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<AppLog>(), sp.GetRequiredService<IExchangeRateSource>())
        {
            Links = sp.GetService<Users.AccountLinks>(),
        });
        // Ticked by the timer service (rate retries); resolving TimerService at start-up creates the ledger.
        services.AddSingleton<IPeriodicService>(sp => sp.GetRequiredService<SpendingLedger>());
        services.AddSingleton<ISessionDataSource>(sp => new SpendingSessionData(sp.GetRequiredService<SpendingLedger>()));
        services.AddSingleton<ISubActionHandler, GetTopSpendersSubAction>();
        services.AddSingleton<ISubActionHandler, NewSpendingStreamSubAction>();
        services.AddSingleton<ISubActionHandler, AddSpendingSubAction>();
        services.AddSingleton<ISubActionHandler, RemoveSpendingSubAction>();
        services.AddSingleton<ITriggerProvider, SpendingTriggers>();
        services.AddSingleton<IScriptFunctionProvider, SpendingScriptFunctions>();
        return services;
    }
}

/// <summary>The period names people and scripts use ("stream", "today", "This week", "month", "all"...).</summary>
public static class SpendingPeriods
{
    public static readonly IReadOnlyList<string> Choices = ["This stream", "Today", "This week", "This month", "This year", "All time"];

    public static string Label(SpendingPeriod period) => Choices[(int)period];

    /// <summary>A period from its name; anything unknown is <see cref="SpendingPeriod.Stream"/>.</summary>
    public static SpendingPeriod Parse(string? text)
    {
        var t = new string((text ?? "").Where(char.IsLetter).ToArray()).ToLowerInvariant();
        return t switch
        {
            "day" or "today" or "daily" => SpendingPeriod.Day,
            "week" or "thisweek" or "weekly" => SpendingPeriod.Week,
            "month" or "thismonth" or "monthly" => SpendingPeriod.Month,
            "year" or "thisyear" or "yearly" => SpendingPeriod.Year,
            "all" or "alltime" or "ever" or "total" => SpendingPeriod.AllTime,
            _ => SpendingPeriod.Stream,
        };
    }

    /// <summary>"" for every platform; "All", "All platforms" and "Any" mean the same.</summary>
    public static string Platform(string? text) =>
        (text ?? "").Trim() is var p && (p.Length == 0 || p.Equals("all", StringComparison.OrdinalIgnoreCase)
                                        || p.Equals("all platforms", StringComparison.OrdinalIgnoreCase) || p.Equals("any", StringComparison.OrdinalIgnoreCase))
            ? "" : p;
}

internal static class SpendingArgs
{
    /// <summary>
    /// topSpender, topSpenderPlatform, topSpenderAmount, topSpenderFormatted (the leader; empty when nobody), topSpenders
    /// ("Alice ($25.00), Bob ($10.00)"), topSpendersCount, topSpendersTotal, topSpendersFormattedTotal, topSpendersJson,
    /// topSpender&lt;N&gt; / topSpender&lt;N&gt;Platform / topSpender&lt;N&gt;Amount / topSpender&lt;N&gt;Formatted for each
    /// place, spendingCurrency and spendingPeriod.
    /// </summary>
    public static void Store(IDictionary<string, object?> args, SpendingList list)
    {
        var first = list.Spenders.FirstOrDefault();
        args["topSpender"] = first?.User ?? "";
        args["topSpenderPlatform"] = first?.Platform ?? "";
        args["topSpenderAmount"] = first?.Total ?? 0;
        args["topSpenderFormatted"] = first is null ? "" : SpendingText.Money(first.Total, list.Currency);
        args["topSpenders"] = string.Join(", ", list.Spenders.Select(s => $"{s.User} ({SpendingText.Money(s.Total, list.Currency)})"));
        args["topSpendersCount"] = list.Spenders.Count;
        args["topSpendersTotal"] = list.Total;
        args["topSpendersFormattedTotal"] = SpendingText.Money(list.Total, list.Currency);
        args["topSpendersJson"] = JsonSerializer.Serialize(list, SpendingJson.Options);
        foreach (var s in list.Spenders)
        {
            args[$"topSpender{s.Rank}"] = s.User;
            args[$"topSpender{s.Rank}Platform"] = s.Platform;
            args[$"topSpender{s.Rank}Amount"] = s.Total;
            args[$"topSpender{s.Rank}Formatted"] = SpendingText.Money(s.Total, list.Currency);
        }
        args["spendingCurrency"] = list.Currency;
        args["spendingPeriod"] = SpendingPeriods.Label(list.Period);
    }

    public static ParameterDescriptor Period(string defaultValue = "This stream") =>
        new("period", "Period", ParameterKind.Choice) { DefaultValue = defaultValue, Choices = [.. SpendingPeriods.Choices] };

    public static ParameterDescriptor Platform(SpendingLedger ledger, string defaultValue = "") =>
        new("platform", "Platform")
        {
            DefaultValue = defaultValue,
            Help = "Empty for every platform together",
            Suggestions = () => ["Twitch", "YouTube", "Kick", "Rumble", .. ledger.Platforms()],
        };
}

internal static class SpendingJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };
}

/// <summary>Puts a top-spenders list into arguments (%topSpender%, %topSpenders%, %topSpender1Amount%...).</summary>
public sealed class GetTopSpendersSubAction(SpendingLedger ledger) : ISubActionHandler
{
    public string TypeId => "spending.getTop";
    public string Category => SpendingFeatures.Category;
    public string DisplayName => "Get top spenders";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        SpendingArgs.Period(),
        SpendingArgs.Platform(ledger),
        new("count", "How many", ParameterKind.Number) { DefaultValue = "5" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var list = ledger.Top(SpendingPeriods.Parse(p["period"]), SpendingPeriods.Platform(p["platform"]), Math.Clamp(p.GetInt("count", 5), 1, 100));
        SpendingArgs.Store(context.Args, list);
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class NewSpendingStreamSubAction(SpendingLedger ledger) : ISubActionHandler
{
    public string TypeId => "spending.newStream";
    public string Category => SpendingFeatures.Category;
    public string DisplayName => "Start a new stream for top spenders";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        ledger.StartSession();
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class AddSpendingSubAction(SpendingLedger ledger) : ISubActionHandler
{
    public string TypeId => "spending.add";
    public string Category => SpendingFeatures.Category;
    public string DisplayName => "Add a purchase";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("user", "User") { DefaultValue = "%user%" },
        new("platform", "Platform") { DefaultValue = "%platform%", Suggestions = () => ["Twitch", "YouTube", "Kick", "Rumble", "Manual"] },
        new("amount", "Amount", ParameterKind.Number) { DefaultValue = "0" },
        new("currency", "Currency") { Help = "ISO code such as USD or EUR; empty for your chosen currency" },
        new("label", "What it was") { DefaultValue = "Manual" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var platform = Text.Clean(p["platform"]);
        ledger.AddManual(platform, Text.Clean(p["user"]), p.GetDouble("amount"), Text.Clean(p["currency"]), Text.Clean(p["label"]));
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class RemoveSpendingSubAction(SpendingLedger ledger) : ISubActionHandler
{
    public string TypeId => "spending.remove";
    public string Category => SpendingFeatures.Category;
    public string DisplayName => "Remove a person's purchases";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("user", "User") { DefaultValue = "%user%" },
        SpendingArgs.Platform(ledger, "%platform%"),
        SpendingArgs.Period("All time"),
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var user = Text.Clean(p["user"]);
        if (user.Length == 0) throw new InvalidOperationException("No user given");
        context.Args["removed"] = ledger.RemoveUser(SpendingPeriods.Platform(Text.Clean(p["platform"])), user, SpendingPeriods.Parse(p["period"]));
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>"Money spent" and "New top spender this stream".</summary>
internal sealed class SpendingTriggers(SpendingLedger ledger) : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType(SpendingEvents.Recorded, SpendingFeatures.Category, "Money spent", SpendingEvents.Recorded,
            [
                SpendingArgs.Platform(ledger),
                new ParameterDescriptor("minAmount", "Minimum amount (your currency)", ParameterKind.Number) { DefaultValue = "0" },
            ],
            (e, s, _) => PlatformMatches(e, s) && Amount(e) >= s.GetDouble("minAmount")),
        new EventTriggerType(SpendingEvents.TopSpenderChanged, SpendingFeatures.Category, "New top spender this stream", SpendingEvents.TopSpenderChanged,
            [
                new ParameterDescriptor("scope", "List")
                {
                    DefaultValue = "All platforms",
                    Help = "All platforms: the overall leader changed. A platform name: that platform's leader changed. Any: either",
                    Suggestions = () => ["All platforms", "Any", "Twitch", "YouTube", "Kick", "Rumble", .. ledger.Platforms()],
                },
            ],
            (e, s, _) => s["scope"].Trim() is var scope && (scope.Equals("any", StringComparison.OrdinalIgnoreCase)
                || string.Equals(e.Args.GetString("scope"), scope.Length == 0 ? "All platforms" : scope, StringComparison.OrdinalIgnoreCase))),
    ];

    private static double Amount(BotEvent e) => e.Args.GetValueOrDefault("amount") is double d ? d : 0;

    private static bool PlatformMatches(BotEvent e, ParameterValues s) =>
        SpendingPeriods.Platform(s["platform"]) is var p && (p.Length == 0 || string.Equals(e.Args.GetString("platform"), p, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Script functions: spending.top, spending.add, spending.remove, spending.newStream, spending.stream, spending.platforms.</summary>
public sealed class SpendingScriptFunctions(SpendingLedger ledger) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("spending.top", "Top spenders ({period, platform, currency, total, pending, spenders:[{rank,user,platform,total,count,breakdown}]}); "
                            + "period stream/day/week/month/year/all, platform empty for all", ["period", "platform", "count"], (_, a, _) =>
            {
                var count = int.TryParse(a.GetString("count", "10"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 10;
                var list = ledger.Top(SpendingPeriods.Parse(a.GetString("period")), SpendingPeriods.Platform(a.GetString("platform")), Math.Clamp(count, 1, 1000));
                return Task.FromResult<object?>(JsonSerializer.SerializeToElement(list, SpendingJson.Options));
            }),
        new("spending.add", "Add a purchase by hand; user/platform default to the triggering user", ["user", "platform", "amount", "currency", "label"],
            (ctx, a, _) =>
            {
                string Value(string key) => a.GetElement(key) is not null ? a.GetString(key) : ctx.Args.GetString(key);
                var amount = double.TryParse(a.GetString("amount"), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
                try
                {
                    ledger.AddManual(Value("platform"), Value("user"), amount, a.GetString("currency"), a.GetString("label", "Manual"));
                }
                catch (ArgumentException ex)
                {
                    throw new ScriptException(ex.Message);
                }
                return Task.FromResult<object?>(true);
            }),
        new("spending.remove", "Remove a person's purchases in a period (default all time); returns how many were removed",
            ["user", "platform", "period"], (_, a, _) =>
                Task.FromResult<object?>(ledger.RemoveUser(SpendingPeriods.Platform(a.GetString("platform")), a.GetString("user"),
                    a.GetString("period") is { Length: > 0 } period ? SpendingPeriods.Parse(period) : SpendingPeriod.AllTime))),
        new("spending.newStream", "Start a new stream for top spenders (fresh list, freshly fetched exchange rates)", [], (_, _, _) =>
        {
            ledger.StartSession();
            return Task.FromResult<object?>(true);
        }),
        new("spending.stream", "The current stream ({id, startedAt, endedAt, currency, ratesSource, ratesDate}), or null", [], (_, _, _) =>
            Task.FromResult<object?>(ledger.CurrentSession is { } s
                ? new { s.Id, s.StartedAt, s.EndedAt, s.Currency, s.RatesSource, s.RatesDate }
                : null)),
        new("spending.platforms", "The platforms anything was bought on", [], (_, _, _) => Task.FromResult<object?>(ledger.Platforms())),
    ];
}

/// <summary>
/// The top spenders in overlay session data, as <c>cuebot-top-spenders</c>: <c>{currency, periods: {stream|day|week|
/// month|year|alltime: {all|&lt;platform&gt;: {total, formattedTotal, spenders: [{rank, name, platform, amount,
/// formatted, count}]}}}}</c>, the top <see cref="Size"/> of each, platform names in lower case.
/// </summary>
internal sealed class SpendingSessionData : ISessionDataSource
{
    public const string Key = "cuebot-top-spenders";
    public const int Size = 10;
    private readonly SpendingLedger _ledger;

    public SpendingSessionData(SpendingLedger ledger)
    {
        _ledger = ledger;
        ledger.Changed += () => Changed?.Invoke();
    }

    public event Action? Changed;

    public void AddTo(JsonObject data)
    {
        var platforms = _ledger.Platforms();
        var periods = new JsonObject();
        var currency = _ledger.Settings.Currency;
        foreach (var period in Enum.GetValues<SpendingPeriod>())
        {
            var lists = new JsonObject { ["all"] = ToJson(_ledger.Top(period, "", Size)) };
            foreach (var platform in platforms) lists[platform.ToLowerInvariant()] = ToJson(_ledger.Top(period, platform, Size));
            periods[period.ToString().ToLowerInvariant()] = lists;
        }
        data[Key] = new JsonObject { ["currency"] = currency, ["periods"] = periods };
    }

    private static JsonObject ToJson(SpendingList list) => new()
    {
        ["total"] = list.Total,
        ["formattedTotal"] = SpendingText.Money(list.Total, list.Currency),
        ["spenders"] = new JsonArray([.. list.Spenders.Select(s => (JsonNode)new JsonObject
        {
            ["rank"] = s.Rank,
            ["name"] = s.User,
            ["platform"] = s.Platform,
            ["amount"] = s.Total,
            ["formatted"] = SpendingText.Money(s.Total, list.Currency),
            ["count"] = s.Count,
        })]),
    };
}

internal static class Text
{
    /// <summary>Trims; an unresolved "%arg%" placeholder counts as empty.</summary>
    public static string Clean(string? text) => ExtrasText.Clean(text);
}
