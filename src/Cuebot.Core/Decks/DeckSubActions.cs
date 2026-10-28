using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Core.Decks;

/// <summary>Registration for decks: decks.json, the service, Streamer.bot's 10 deck sub-actions and the pressed trigger.</summary>
public static class DeckFeatures
{
    public const string Category = "Decks";

    public static IServiceCollection AddDeckFeatures(this IServiceCollection services)
    {
        services.AddSettingsFile<DeckSettings>("decks.json");
        services.AddSingleton(sp => new DeckService(sp.GetRequiredService<Storage.JsonFile<DeckSettings>>(),
            sp.GetRequiredService<EventBus>(), sp.GetRequiredService<Logging.AppLog>(), sp.GetRequiredService<ActionEngine>));
        services.AddSingleton<ITriggerProvider, DeckTriggers>();
        services.AddSingleton<ISubActionHandler, DeckNotifySubAction>();
        services.AddSingleton<ISubActionHandler, DeckItemSetBackgroundSubAction>();
        services.AddSingleton<ISubActionHandler, DeckItemSetIconSubAction>();
        services.AddSingleton<ISubActionHandler, DeckItemSetStateSubAction>();
        services.AddSingleton<ISubActionHandler, DeckItemSetTitleSubAction>();
        services.AddSingleton<ISubActionHandler, DeckItemSetValueSubAction>();
        services.AddSingleton<ISubActionHandler, DeckItemToggleStateSubAction>();
        services.AddSingleton<ISubActionHandler, DeckPageNextSubAction>();
        services.AddSingleton<ISubActionHandler, DeckPagePreviousSubAction>();
        services.AddSingleton<ISubActionHandler, DeckPageSetSubAction>();
        return services;
    }
}

/// <summary>"Deck item pressed", optionally for one deck and/or item (name or id).</summary>
public sealed class DeckTriggers(DeckService decks) : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        new EventTriggerType("decks.itemPressed", DeckFeatures.Category, "Deck item pressed", DeckEvents.ItemPressed,
            [
                new ParameterDescriptor("deck", "Deck") { Suggestions = () => decks.DeckNames, Help = "Leave empty for any deck" },
                new ParameterDescriptor("item", "Item") { Suggestions = () => decks.ItemNames, Help = "Leave empty for any item" },
            ],
            (e, s, _) => Is(e, "deckName", "deckId", s["deck"]) && Is(e, "deckItemName", "deckItemId", s["item"])),
    ];

    private static bool Is(BotEvent e, string nameKey, string idKey, string wanted)
    {
        wanted = wanted.Trim();
        return wanted.Length == 0
               || string.Equals(e.Args.GetValueOrDefault(nameKey) as string, wanted, StringComparison.OrdinalIgnoreCase)
               || string.Equals(e.Args.GetValueOrDefault(idKey) as string, wanted, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Shared parameters of the deck sub-actions.</summary>
public abstract class DeckSubActionBase(DeckService decks) : ISubActionHandler
{
    protected DeckService Decks => decks;
    public abstract string TypeId { get; }
    public string Category => DeckFeatures.Category;
    public abstract string DisplayName { get; }
    public abstract IReadOnlyList<ParameterDescriptor> Parameters { get; }
    public abstract Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct);

    protected ParameterDescriptor DeckParameter() => new("deck", "Deck") { Suggestions = () => decks.DeckNames, Help = "Deck name or id" };

    protected ParameterDescriptor ItemParameter() =>
        new("item", "Item") { DefaultValue = "%deckItemName%", Suggestions = () => decks.ItemNames, Help = "Item name or id" };

    protected static ParameterDescriptor StateParameter() =>
        new("state", "State", ParameterKind.Number) { DefaultValue = "0", Help = "0 = default, 1 = active, ..." };

    /// <summary>An optional text field: empty means "leave unchanged".</summary>
    protected static string? Optional(ParameterValues p, string key) => p[key].Length == 0 ? null : p[key];
}

public sealed class DeckNotifySubAction(DeckService decks) : DeckSubActionBase(decks)
{
    public override string TypeId => "decks.notify";
    public override string DisplayName => "Deck notify";
    public override IReadOnlyList<ParameterDescriptor> Parameters => _p ??= [DeckParameter(), new("title", "Title"), new("description", "Description", ParameterKind.Multiline)];
    private IReadOnlyList<ParameterDescriptor>? _p;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        Decks.Notify(p["deck"], p["title"], p["description"]);
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class DeckItemSetBackgroundSubAction(DeckService decks) : DeckSubActionBase(decks)
{
    public override string TypeId => "decks.itemSetBackground";
    public override string DisplayName => "Deck item set background";
    public override IReadOnlyList<ParameterDescriptor> Parameters => _p ??=
    [
        DeckParameter(), ItemParameter(),
        new("color", "Color") { Help = "Hex code or colour name; empty keeps it" },
        new("url", "Image URL") { Help = "Empty keeps it" },
        StateParameter(),
    ];
    private IReadOnlyList<ParameterDescriptor>? _p;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        Decks.SetBackground(p["deck"], p["item"], Optional(p, "color"), Optional(p, "url"), p.GetInt("state"));
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class DeckItemSetIconSubAction(DeckService decks) : DeckSubActionBase(decks)
{
    public override string TypeId => "decks.itemSetIcon";
    public override string DisplayName => "Deck item set icon";
    public override IReadOnlyList<ParameterDescriptor> Parameters => _p ??=
    [
        DeckParameter(), ItemParameter(),
        new("color", "Color") { Help = "Icon colour; empty keeps it" },
        new("name", "Icon name") { Help = "Iconify name such as mdi:home; empty keeps it" },
        new("url", "Icon image URL") { Help = "Wins over the icon name; empty keeps it" },
        StateParameter(),
    ];
    private IReadOnlyList<ParameterDescriptor>? _p;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        Decks.SetIcon(p["deck"], p["item"], Optional(p, "color"), Optional(p, "name"), Optional(p, "url"), p.GetInt("state"));
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class DeckItemSetStateSubAction(DeckService decks) : DeckSubActionBase(decks)
{
    public override string TypeId => "decks.itemSetState";
    public override string DisplayName => "Deck item set state";
    public override IReadOnlyList<ParameterDescriptor> Parameters => _p ??= [DeckParameter(), ItemParameter(), StateParameter()];
    private IReadOnlyList<ParameterDescriptor>? _p;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        Decks.SetState(p["deck"], p["item"], p.GetInt("state"));
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class DeckItemSetTitleSubAction(DeckService decks) : DeckSubActionBase(decks)
{
    public override string TypeId => "decks.itemSetTitle";
    public override string DisplayName => "Deck item set title";
    public override IReadOnlyList<ParameterDescriptor> Parameters => _p ??=
    [
        DeckParameter(), ItemParameter(),
        new("title", "Title") { Help = "Empty clears the title" },
        new("color", "Color") { Help = "Title colour; empty keeps it" },
        StateParameter(),
    ];
    private IReadOnlyList<ParameterDescriptor>? _p;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        Decks.SetTitle(p["deck"], p["item"], p["title"], Optional(p, "color"), p.GetInt("state"));
        return Task.FromResult(SubActionResult.Next);
    }
}

public sealed class DeckItemSetValueSubAction(DeckService decks) : DeckSubActionBase(decks)
{
    public override string TypeId => "decks.itemSetValue";
    public override string DisplayName => "Deck item set value";
    public override IReadOnlyList<ParameterDescriptor> Parameters => _p ??=
        [DeckParameter(), ItemParameter(), new("value", "Value") { Help = "Shown in the button's corner; empty hides it" }, StateParameter()];
    private IReadOnlyList<ParameterDescriptor>? _p;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        Decks.SetValue(p["deck"], p["item"], p["value"], p.GetInt("state"));
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Flips an item between state 0 and 1; sets %deckItemState%.</summary>
public sealed class DeckItemToggleStateSubAction(DeckService decks) : DeckSubActionBase(decks)
{
    public override string TypeId => "decks.itemToggleState";
    public override string DisplayName => "Deck item toggle state";
    public override IReadOnlyList<ParameterDescriptor> Parameters => _p ??= [DeckParameter(), ItemParameter()];
    private IReadOnlyList<ParameterDescriptor>? _p;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        context.Args["deckItemState"] = Decks.ToggleState(p["deck"], p["item"]);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Next page (wrapping to the first); sets %deckPageNumber% (from 1).</summary>
public sealed class DeckPageNextSubAction(DeckService decks) : DeckSubActionBase(decks)
{
    public override string TypeId => "decks.pageNext";
    public override string DisplayName => "Deck page next";
    public override IReadOnlyList<ParameterDescriptor> Parameters => _p ??= [DeckParameter()];
    private IReadOnlyList<ParameterDescriptor>? _p;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        context.Args["deckPageNumber"] = Decks.ChangePage(p["deck"], +1);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Previous page (wrapping to the last); sets %deckPageNumber% (from 1).</summary>
public sealed class DeckPagePreviousSubAction(DeckService decks) : DeckSubActionBase(decks)
{
    public override string TypeId => "decks.pagePrevious";
    public override string DisplayName => "Deck page previous";
    public override IReadOnlyList<ParameterDescriptor> Parameters => _p ??= [DeckParameter()];
    private IReadOnlyList<ParameterDescriptor>? _p;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        context.Args["deckPageNumber"] = Decks.ChangePage(p["deck"], -1);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Goes to page N (from 1, clamped); sets %deckPageNumber%.</summary>
public sealed class DeckPageSetSubAction(DeckService decks) : DeckSubActionBase(decks)
{
    public override string TypeId => "decks.pageSet";
    public override string DisplayName => "Deck page set";
    public override IReadOnlyList<ParameterDescriptor> Parameters => _p ??=
        [DeckParameter(), new("page", "Page number", ParameterKind.Number) { DefaultValue = "1" }];
    private IReadOnlyList<ParameterDescriptor>? _p;

    public override Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues p, CancellationToken ct)
    {
        context.Args["deckPageNumber"] = Decks.ChangePage(p["deck"], null, p.GetInt("page", 1));
        return Task.FromResult(SubActionResult.Next);
    }
}
