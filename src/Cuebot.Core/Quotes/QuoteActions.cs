using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Scripting;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Quotes;

public static class QuoteFeatures
{
    public static IServiceCollection AddQuoteFeatures(this IServiceCollection services)
    {
        services.AddSingleton(sp => new QuoteStore(sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<AppLog>(),
            sp.GetRequiredService<EventBus>()));
        services.AddSingleton<ITriggerProvider, QuoteTriggers>();
        services.AddSingleton<ISubActionHandler, AddQuoteSubAction>();
        services.AddSingleton<ISubActionHandler, GetQuoteSubAction>();
        services.AddSingleton<ISubActionHandler, EditQuoteSubAction>();
        services.AddSingleton<ISubActionHandler, DeleteQuoteSubAction>();
        services.AddSingleton<ISubActionHandler, GetQuoteCountSubAction>();
        services.AddSingleton<IScriptFunctionProvider, QuoteScriptFunctions>();
        return services;
    }
}

/// <summary>Quote added / shown / removed triggers, optionally filtered by the quote's user.</summary>
public sealed class QuoteTriggers(QuoteStore quotes) : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Trigger("quotes.added", "Quote added", QuoteEvents.Added),
        Trigger("quotes.shown", "Quote shown", QuoteEvents.Shown),
        Trigger("quotes.updated", "Quote edited", QuoteEvents.Updated),
        Trigger("quotes.removed", "Quote removed", QuoteEvents.Removed),
    ];

    private EventTriggerType Trigger(string id, string name, string eventType) =>
        new(id, ExtrasCategories.Quotes, name, eventType,
            [new ParameterDescriptor("user", "Quote user")
            {
                Help = "Leave empty for every quote",
                Suggestions = () => quotes.All().Select(q => q.User).Where(u => u.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase),
            }],
            (e, s, _) => s["user"].Trim().Length == 0
                         || string.Equals(e.Args.GetString("quoteUser"), s["user"].Trim().TrimStart('@'), StringComparison.OrdinalIgnoreCase));
}

internal static class QuoteParameters
{
    public static ParameterDescriptor Id(QuoteStore quotes, string defaultValue = "%input0%", string? help = null) =>
        new("id", "Quote id")
        {
            DefaultValue = defaultValue,
            Help = help ?? "Quote number, e.g. 12 or #12",
            Suggestions = () => quotes.All().Select(q => q.Id.ToString(ExtrasText.Inv)),
        };

    public static ParameterDescriptor Game(QuoteStore quotes) => new("game", "Game / category")
    {
        Help = "Empty = the category last reported by a platform",
        Suggestions = () => new[] { quotes.CurrentGame }.Concat(quotes.All().Select(q => q.Game))
            .Where(g => g.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase),
    };

    public static int RequireId(string text)
    {
        var id = QuoteStore.ParseId(text);
        return id > 0 ? id : throw new InvalidOperationException($"'{ExtrasText.Clean(text)}' is not a quote id");
    }

    public static void Store(ActionRunContext context, Quote quote)
    {
        foreach (var (key, value) in QuoteStore.ToArgs(quote)) context.Args[key] = value;
    }
}

/// <summary>Adds a quote (for "!addquote text"): the text defaults to the command input.</summary>
public sealed class AddQuoteSubAction(QuoteStore quotes) : ISubActionHandler
{
    public string TypeId => "quotes.add";
    public string Category => ExtrasCategories.Quotes;
    public string DisplayName => "Add quote";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("text", "Quote text") { DefaultValue = "%input%", Help = "%input% is the text after a command" },
        new("user", "User") { DefaultValue = "%user%", Suggestions = () => ["%user%", "%input0%"] },
        new("userId", "User id") { DefaultValue = "%userId%" },
        new("platform", "Platform") { DefaultValue = "%platform%", Suggestions = () => ["twitch", "youtube", "kick", "rumble"] },
        QuoteParameters.Game(quotes),
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var text = ExtrasText.Clean(p["text"]);
        if (text.Length == 0) throw new InvalidOperationException("No quote text given");
        var quote = quotes.Add(text, ExtrasText.Clean(p["user"]), ExtrasText.Clean(p["userId"]), ExtrasText.Clean(p["platform"]),
            ExtrasText.Clean(p["game"]));
        QuoteParameters.Store(context, quote);
        context.Args["quoteCount"] = quotes.Count;
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Gets a quote by id, or a random one (optionally matching a search), into arguments; fires "Quote shown".</summary>
public sealed class GetQuoteSubAction(QuoteStore quotes) : ISubActionHandler
{
    public string TypeId => "quotes.get";
    public string Category => ExtrasCategories.Quotes;
    public string DisplayName => "Get quote";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        QuoteParameters.Id(quotes, "%input0%", "Empty (or not a number) = a random quote"),
        new("search", "Random among quotes containing") { Help = "Optional text / user / game filter for random quotes" },
        new("fireShown", "Fire 'Quote shown' trigger", ParameterKind.Boolean) { DefaultValue = "true" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var id = QuoteStore.ParseId(p["id"]);
        var quote = id > 0 ? quotes.Get(id) : quotes.RandomQuote(ExtrasText.Clean(p["search"]));
        context.Args["quoteFound"] = quote is not null;
        context.Args["quoteCount"] = quotes.Count;
        if (quote is null)
        {
            foreach (var key in QuoteStore.ToArgs(new Quote()).Keys) context.Args[key] = "";
            return Task.FromResult(SubActionResult.Next);
        }
        QuoteParameters.Store(context, quote);
        if (p.GetBool("fireShown", true)) quotes.MarkShown(quote);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Edits a quote's text, user or game; empty fields stay unchanged.</summary>
public sealed class EditQuoteSubAction(QuoteStore quotes) : ISubActionHandler
{
    public string TypeId => "quotes.edit";
    public string Category => ExtrasCategories.Quotes;
    public string DisplayName => "Edit quote";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        QuoteParameters.Id(quotes),
        new("text", "New text") { Help = "Empty keeps the text" },
        new("user", "New user") { Help = "Empty keeps the user" },
        new("game", "New game / category") { Help = "Empty keeps the game" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var id = QuoteParameters.RequireId(p["id"]);
        string? Opt(string key) => ExtrasText.Clean(p[key]) is { Length: > 0 } v ? v : null;
        var quote = quotes.Update(id, Opt("text"), Opt("user"), Opt("game"));
        context.Args["quoteUpdated"] = quote is not null;
        if (quote is not null) QuoteParameters.Store(context, quote);
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class DeleteQuoteSubAction(QuoteStore quotes) : ISubActionHandler
{
    public string TypeId => "quotes.delete";
    public string Category => ExtrasCategories.Quotes;
    public string DisplayName => "Delete quote";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [QuoteParameters.Id(quotes)];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        var removed = quotes.Delete(QuoteParameters.RequireId(p["id"]));
        context.Args["quoteDeleted"] = removed is not null;
        if (removed is not null) QuoteParameters.Store(context, removed);
        context.Args["quoteCount"] = quotes.Count;
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class GetQuoteCountSubAction(QuoteStore quotes) : ISubActionHandler
{
    public string TypeId => "quotes.count";
    public string Category => ExtrasCategories.Quotes;
    public string DisplayName => "Get quote count";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        context.Args["quoteCount"] = quotes.Count;
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Script functions: quotes.add / get / random / edit / delete / count / search. Quotes come back as objects.</summary>
public sealed class QuoteScriptFunctions(QuoteStore quotes) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("quotes.add", "Add a quote; user/userId/platform default to the triggering user. Returns the quote",
            ["text", "user", "userId", "platform", "game"], (ctx, a, _) =>
            {
                string Value(string key) => a.GetElement(key) is not null ? a.GetString(key) : ctx.Args.GetString(key);
                var text = a.GetString("text");
                if (text.Trim().Length == 0) throw new ScriptException("Missing 'text'");
                return Result(quotes.Add(text, Value("user"), Value("userId"), Value("platform"), a.GetString("game")));
            }),
        new("quotes.get", "A quote by id, or null", ["id"], (_, a, _) => Result(quotes.Get(Id(a)))),
        new("quotes.random", "A random quote (optionally containing 'search'), or null; fires 'Quote shown' when show is true",
            ["search", "show"], (_, a, _) =>
            {
                var quote = quotes.RandomQuote(a.GetString("search"));
                if (quote is not null && a.GetBool("show")) quotes.MarkShown(quote);
                return Result(quote);
            }),
        new("quotes.show", "Fire 'Quote shown' for a quote id; returns the quote or null", ["id"], (_, a, _) =>
        {
            var quote = quotes.Get(Id(a));
            if (quote is not null) quotes.MarkShown(quote);
            return Result(quote);
        }),
        new("quotes.edit", "Edit a quote (omitted fields stay); returns the quote or null", ["id", "text", "user", "game"],
            (_, a, _) => Result(quotes.Update(Id(a), Opt(a, "text"), Opt(a, "user"), Opt(a, "game")))),
        new("quotes.delete", "Delete a quote; returns true when it existed", ["id"],
            (_, a, _) => Task.FromResult<object?>(quotes.Delete(Id(a)) is not null)),
        new("quotes.count", "Number of quotes", [], (_, _, _) => Task.FromResult<object?>(quotes.Count)),
        new("quotes.search", "Quotes whose text, user or game contains 'query'", ["query"],
            (_, a, _) => Task.FromResult<object?>(quotes.Search(a.GetString("query")).Select(ToObject).ToArray())),
    ];

    private static int Id(JsonElement a)
    {
        var id = QuoteStore.ParseId(a.GetString("id"));
        return id > 0 ? id : throw new ScriptException("Missing or invalid 'id'");
    }

    private static string? Opt(JsonElement a, string key) => a.GetElement(key) is not null ? a.GetString(key) : null;

    private static Task<object?> Result(Quote? quote) => Task.FromResult<object?>(quote is null ? null : ToObject(quote));

    private static Dictionary<string, object?> ToObject(Quote q) => new()
    {
        ["id"] = q.Id,
        ["text"] = q.Text,
        ["user"] = q.User,
        ["userId"] = q.UserId,
        ["platform"] = q.Platform,
        ["game"] = q.Game,
        ["createdAt"] = q.CreatedAt.ToString("o", ExtrasText.Inv),
    };
}
