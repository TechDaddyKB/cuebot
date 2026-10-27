using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Credits;

public static class CreditsFeatures
{
    public static IServiceCollection AddCreditsFeatures(this IServiceCollection services)
    {
        services.AddSingleton(sp => new CreditsCollector(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>()));
        services.AddSingleton<ICreditsSource>(sp => sp.GetRequiredService<CreditsCollector>());
        // Ticked by the timer service (auto reset); resolving TimerService at start-up creates the collector.
        services.AddSingleton<IPeriodicService>(sp => sp.GetRequiredService<CreditsCollector>());
        services.AddSingleton<ISubActionHandler, ResetCreditsSubAction>();
        services.AddSingleton<ISubActionHandler, AddCustomCreditSubAction>();
        services.AddSingleton<ISubActionHandler, GetCreditsSubAction>();
        services.AddSingleton<IScriptFunctionProvider, CreditsScriptFunctions>();
        return services;
    }
}

internal static class CreditsArgs
{
    /// <summary>
    /// credits (JSON, see <see cref="CreditsSnapshot"/>), creditsTotal, and per section credits&lt;Id&gt; (names joined
    /// by ", ") and credits&lt;Id&gt;Count, for every built-in section (empty when none) and custom ones.
    /// </summary>
    public static void Store(IDictionary<string, object?> args, CreditsSnapshot snapshot)
    {
        args["credits"] = CreditsJson.Serialize(snapshot);
        args["creditsTotal"] = snapshot.TotalEntries;
        foreach (var id in CreditsSections.Order.Concat(snapshot.Sections.Select(s => s.Id)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var names = snapshot.Section(id)?.Entries.Select(e => e.User).ToArray() ?? [];
            var key = "credits" + ExtrasText.Pascal(ArgSafe(id));
            args[key] = string.Join(", ", names);
            args[key + "Count"] = names.Length;
        }
    }

    public static string ArgSafe(string id) => new(id.Where(char.IsLetterOrDigit).ToArray());

    public static IEnumerable<string> SectionSuggestions(CreditsCollector credits) =>
        CreditsSections.Order.Concat(credits.Snapshot().Sections.Select(s => s.Id)).Append("Special thanks")
            .Distinct(StringComparer.OrdinalIgnoreCase);
}

public sealed class ResetCreditsSubAction(CreditsCollector credits) : ISubActionHandler
{
    public string TypeId => "credits.reset";
    public string Category => ExtrasCategories.Credits;
    public string DisplayName => "Reset credits";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        credits.Reset();
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class AddCustomCreditSubAction(CreditsCollector credits) : ISubActionHandler
{
    public string TypeId => "credits.addCustom";
    public string Category => ExtrasCategories.Credits;
    public string DisplayName => "Add custom credit";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("section", "Section")
        {
            DefaultValue = "Special thanks",
            Help = "A built-in section id (follows, tips, ...) or any custom title",
            Suggestions = () => CreditsArgs.SectionSuggestions(credits),
        },
        new("user", "User") { DefaultValue = "%user%" },
        new("platform", "Platform") { DefaultValue = "%platform%", Suggestions = () => ["twitch", "youtube", "kick", "rumble"] },
        new("amount", "Amount", ParameterKind.Number) { DefaultValue = "0", Help = "Added to the user's total in this section" },
        new("detail", "Detail") { Help = "Optional text shown with the name" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var user = ExtrasText.Clean(p["user"]);
        if (user.Length == 0) throw new InvalidOperationException("No user given for the credit");
        var section = ExtrasText.Clean(p["section"]);
        if (section.Length == 0) throw new InvalidOperationException("No credits section given");
        credits.AddCustom(section, user, ExtrasText.Clean(p["platform"]), p.GetDouble("amount"), ExtrasText.Clean(p["detail"]));
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Puts the credits into arguments: %credits% (JSON), %creditsFollows%, %creditsFollowsCount%, ...</summary>
public sealed class GetCreditsSubAction(CreditsCollector credits) : ISubActionHandler
{
    public string TypeId => "credits.get";
    public string Category => ExtrasCategories.Credits;
    public string DisplayName => "Get credits";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("test", "Use test data", ParameterKind.Boolean) { DefaultValue = "false", Help = "Sample credits for designing an overlay" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        CreditsArgs.Store(context.Args, p.GetBool("test") ? credits.Test() : credits.Snapshot());
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Script functions: credits.get (object), credits.test, credits.reset, credits.add.</summary>
public sealed class CreditsScriptFunctions(CreditsCollector credits) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("credits.get", "The current credits ({sections:[{id,title,entries:[{user,platform,count,amount,detail}]}]})", [],
            (_, _, _) => Task.FromResult<object?>(credits.Snapshot())),
        new("credits.test", "Sample credits for overlay design", [], (_, _, _) => Task.FromResult<object?>(credits.Test())),
        new("credits.reset", "Clear the current credits", [], (_, _, _) =>
        {
            credits.Reset();
            return Task.FromResult<object?>(true);
        }),
        new("credits.add", "Add a credit to a section; user/platform default to the triggering user",
            ["section", "user", "platform", "amount", "detail"], (ctx, a, _) =>
            {
                string Value(string key) => a.GetElement(key) is not null ? a.GetString(key) : ctx.Args.GetString(key);
                var amount = a.GetElement("amount") is { ValueKind: System.Text.Json.JsonValueKind.Number } el && el.TryGetDouble(out var d) ? d
                    : double.TryParse(a.GetString("amount"), System.Globalization.NumberStyles.Float, ExtrasText.Inv, out var parsed) ? parsed : 0;
                try
                {
                    credits.AddCustom(a.GetString("section"), Value("user"), Value("platform"), amount, a.GetString("detail"));
                }
                catch (ArgumentException ex)
                {
                    throw new ScriptException(ex.Message);
                }
                return Task.FromResult<object?>(true);
            }),
    ];
}
