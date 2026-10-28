using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Decks;

/// <summary>Something a deck's open pages must learn: a new view ("state") or a notification ("notify").</summary>
public sealed record DeckMessage(Guid DeckId, string Kind, JsonObject Data);

public static class DeckEvents
{
    /// <summary>A deck button was pressed (args: deckId, deckName, deckItemId, deckItemName, deckItemState, deckPage, deckClient).</summary>
    public const string ItemPressed = "decks.itemPressed";
}

/// <summary>
/// The decks: their saved design (decks.json) plus runtime state that sub-actions change while Cuebot runs (each
/// item's active state and per-state title/colour/icon/value overrides, and each deck's current page). Runtime state is
/// not saved; a restart shows the decks as designed. The HTTP server renders <see cref="View"/> and forwards
/// <see cref="Message"/> to open pages. Thread-safe.
/// </summary>
public sealed class DeckService : IDisposable
{
    private const string LogSource = "Decks";

    private readonly JsonFile<DeckSettings>? _file;
    private readonly DeckSettings _memory = new();
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly Func<ActionEngine>? _engine;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid Deck, Guid Item), ItemRuntime> _items = [];
    private readonly Dictionary<Guid, int> _pages = [];

    public DeckService(JsonFile<DeckSettings>? file, EventBus bus, AppLog log, Func<ActionEngine>? engine = null)
    {
        _file = file;
        _bus = bus;
        _log = log;
        _engine = engine;
        lock (_gate) Normalize(Settings);
    }

    /// <summary>Raised (on the caller's thread) when a deck's view changes or a notification is sent.</summary>
    public event Action<DeckMessage>? Message;

    /// <summary>Raised when the saved design changes (decks added, edited or removed).</summary>
    public event Action? Changed;

    private DeckSettings Settings => _file?.Value ?? _memory;

    /// <summary>Copies of every deck's design.</summary>
    public IReadOnlyList<DeckDefinition> Decks
    {
        get { lock (_gate) return Json.Clone(Settings.Decks); }
    }

    public IEnumerable<string> DeckNames
    {
        get { lock (_gate) return Settings.Decks.Select(d => d.Name).ToArray(); }
    }

    public IEnumerable<string> ItemNames
    {
        get { lock (_gate) return Settings.Decks.SelectMany(d => d.Pages).SelectMany(p => p.Items).Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(); }
    }

    /// <summary>A copy of the deck with this id or name; null when unknown.</summary>
    public DeckDefinition? Find(string idOrName)
    {
        lock (_gate) return FindLocked(idOrName) is { } d ? Json.Clone(d) : null;
    }

    /// <summary>Adds or replaces a deck's design (matched by id) and refreshes its open pages.</summary>
    public void Save(DeckDefinition deck)
    {
        lock (_gate)
        {
            var copy = Json.Clone(deck);
            Normalize(copy);
            var i = Settings.Decks.FindIndex(d => d.Id == copy.Id);
            if (i >= 0) Settings.Decks[i] = copy;
            else Settings.Decks.Add(copy);
            _file?.ScheduleSave();
        }
        Changed?.Invoke();
        PublishState(deck.Id);
    }

    public bool Remove(Guid id)
    {
        bool removed;
        lock (_gate)
        {
            removed = Settings.Decks.RemoveAll(d => d.Id == id) > 0;
            foreach (var key in _items.Keys.Where(k => k.Deck == id).ToArray()) _items.Remove(key);
            _pages.Remove(id);
            if (removed) _file?.ScheduleSave();
        }
        if (removed) Changed?.Invoke();
        return removed;
    }

    /// <summary>Clears every runtime change (states, overrides, pages) of a deck.</summary>
    public void ResetRuntime(Guid id)
    {
        lock (_gate)
        {
            foreach (var key in _items.Keys.Where(k => k.Deck == id).ToArray()) _items.Remove(key);
            _pages.Remove(id);
        }
        PublishState(id);
    }

    // ---- views ----

    /// <summary>What a browser shows: the deck's layout and the current page's items in their current state. Null when the deck is unknown or disabled.</summary>
    public JsonObject? View(Guid id)
    {
        lock (_gate)
        {
            var deck = Settings.Decks.FirstOrDefault(d => d.Id == id);
            if (deck is null || !deck.Enabled) return null;
            var pageIndex = PageLocked(deck);
            var page = deck.Pages[pageIndex];
            var items = new JsonArray();
            foreach (var item in page.Items)
            {
                var runtime = _items.GetValueOrDefault((deck.Id, item.Id));
                var state = runtime?.State ?? 0;
                var look = Effective(item, runtime, state);
                items.Add(new JsonObject
                {
                    ["id"] = item.Id.ToString(),
                    ["name"] = item.Name,
                    ["row"] = item.Row,
                    ["column"] = item.Column,
                    ["state"] = state,
                    ["title"] = look.Title,
                    ["titleColor"] = look.TitleColor,
                    ["background"] = look.Background,
                    ["backgroundImage"] = look.BackgroundImage,
                    ["icon"] = look.Icon,
                    ["iconColor"] = look.IconColor,
                    ["iconUrl"] = look.IconUrl,
                    ["value"] = look.Value,
                });
            }
            return new JsonObject
            {
                ["id"] = deck.Id.ToString(),
                ["name"] = deck.Name,
                ["rows"] = deck.Rows,
                ["columns"] = deck.Columns,
                ["background"] = deck.Background,
                ["gap"] = deck.Gap,
                ["page"] = pageIndex,
                ["pageName"] = page.Name,
                ["pageCount"] = deck.Pages.Count,
                ["items"] = items,
            };
        }
    }

    /// <summary>Every enabled deck (id and name), for the deck index page.</summary>
    public IReadOnlyList<(Guid Id, string Name)> EnabledDecks()
    {
        lock (_gate) return Settings.Decks.Where(d => d.Enabled).Select(d => (d.Id, d.Name)).ToArray();
    }

    // ---- pressing ----

    /// <summary>
    /// A browser pressed a button: optionally toggles it, fires "Deck item pressed" and runs the item's action with
    /// the deck's and the item's arguments plus deckId, deckName, deckItemId, deckItemName, deckItemState, deckPage and
    /// deckClient. Returns false when the deck or item is unknown or the deck is disabled.
    /// </summary>
    public bool Press(Guid deckId, Guid itemId, string client = "")
    {
        Dictionary<string, object?> args;
        string action;
        var toggled = false;
        lock (_gate)
        {
            var deck = Settings.Decks.FirstOrDefault(d => d.Id == deckId);
            if (deck is null || !deck.Enabled) return false;
            var found = deck.Pages.Select((p, i) => (Page: i, Item: p.Items.FirstOrDefault(x => x.Id == itemId))).FirstOrDefault(x => x.Item is not null);
            if (found.Item is not { } item) return false;
            if (item.ToggleOnPress)
            {
                ToggleLocked(deck.Id, item.Id);
                toggled = true;
            }
            args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in SendSignalSubAction.ParseArguments(deck.Arguments)) args[k] = v;
            foreach (var (k, v) in SendSignalSubAction.ParseArguments(item.Arguments)) args[k] = v;
            args["deckId"] = deck.Id.ToString();
            args["deckName"] = deck.Name;
            args["deckItemId"] = item.Id.ToString();
            args["deckItemName"] = item.Name;
            args["deckItemState"] = _items.GetValueOrDefault((deck.Id, item.Id))?.State ?? 0;
            args["deckPage"] = found.Page;
            args["deckClient"] = client;
            action = item.Action.Trim();
        }
        if (toggled) PublishState(deckId);
        _bus.Publish(new BotEvent(DeckEvents.ItemPressed, args));
        if (action.Length > 0 && _engine?.Invoke() is { } engine)
        {
            var target = engine.FindAction(action);
            if (target is null) _log.Warn(LogSource, $"Deck button '{args["deckItemName"]}' runs '{action}', which does not exist");
            else _ = engine.RunAsync(target, args, source: $"Deck: {args["deckName"]}");
        }
        return true;
    }

    // ---- sub-action operations (all refresh the deck's open pages) ----

    /// <summary>Shows a notification on every open page of the deck.</summary>
    public void Notify(string deck, string title, string description)
    {
        var d = Require(deck);
        Message?.Invoke(new DeckMessage(d.Id, "notify", new JsonObject { ["title"] = title, ["description"] = description }));
    }

    public void SetBackground(string deck, string item, string? color, string? imageUrl, int state) =>
        Override(deck, item, state, o =>
        {
            if (color is not null) o.Background = color;
            if (imageUrl is not null) o.BackgroundImage = imageUrl;
        });

    public void SetIcon(string deck, string item, string? color, string? name, string? url, int state) =>
        Override(deck, item, state, o =>
        {
            if (color is not null) o.IconColor = color;
            if (name is not null) o.Icon = name;
            if (url is not null) o.IconUrl = url;
        });

    public void SetTitle(string deck, string item, string? title, string? color, int state) =>
        Override(deck, item, state, o =>
        {
            if (title is not null) o.Title = title;
            if (color is not null) o.TitleColor = color;
        });

    public void SetValue(string deck, string item, string? value, int state) =>
        Override(deck, item, state, o => o.Value = value ?? "");

    /// <summary>Makes <paramref name="state"/> the item's active state (0 = default).</summary>
    public void SetState(string deck, string item, int state)
    {
        var (d, i) = RequireItem(deck, item);
        lock (_gate) Runtime(d, i).State = Math.Max(0, state);
        PublishState(d);
    }

    /// <summary>Flips the item between state 0 and state 1. Returns the new state.</summary>
    public int ToggleState(string deck, string item)
    {
        var (d, i) = RequireItem(deck, item);
        int state;
        lock (_gate) state = ToggleLocked(d, i);
        PublishState(d);
        return state;
    }

    /// <summary>Moves to the next/previous page (wrapping) or a page by number (from 1). Returns the new page number.</summary>
    public int ChangePage(string deck, int? delta, int? page = null)
    {
        var d = Require(deck);
        int now;
        lock (_gate)
        {
            var live = Settings.Decks.First(x => x.Id == d.Id);
            var count = live.Pages.Count;
            var current = PageLocked(live);
            var next = page is { } p ? Math.Clamp(p - 1, 0, count - 1) : ((current + (delta ?? 0)) % count + count) % count;
            _pages[live.Id] = next;
            now = next + 1;
        }
        PublishState(d.Id);
        return now;
    }

    // ---- internals ----

    private void Override(string deck, string item, int state, Action<DeckItemOverride> change)
    {
        var (d, i) = RequireItem(deck, item);
        lock (_gate)
        {
            var rt = Runtime(d, i);
            if (!rt.Overrides.TryGetValue(Math.Max(0, state), out var o)) rt.Overrides[Math.Max(0, state)] = o = new DeckItemOverride();
            change(o);
        }
        PublishState(d);
    }

    private ItemRuntime Runtime(Guid deck, Guid item)
    {
        if (!_items.TryGetValue((deck, item), out var rt)) _items[(deck, item)] = rt = new ItemRuntime();
        return rt;
    }

    private int ToggleLocked(Guid deck, Guid item)
    {
        var rt = Runtime(deck, item);
        rt.State = rt.State == 0 ? 1 : 0;
        return rt.State;
    }

    private int PageLocked(DeckDefinition deck)
    {
        var page = _pages.GetValueOrDefault(deck.Id);
        return Math.Clamp(page, 0, deck.Pages.Count - 1);
    }

    private DeckDefinition? FindLocked(string idOrName)
    {
        var text = idOrName.Trim();
        return Guid.TryParse(text, out var id)
            ? Settings.Decks.FirstOrDefault(d => d.Id == id)
            : Settings.Decks.FirstOrDefault(d => string.Equals(d.Name, text, StringComparison.OrdinalIgnoreCase));
    }

    private DeckDefinition Require(string deck)
    {
        lock (_gate) return FindLocked(deck) ?? throw new InvalidOperationException($"No deck named '{deck}'");
    }

    private (Guid Deck, Guid Item) RequireItem(string deck, string item)
    {
        lock (_gate)
        {
            var d = FindLocked(deck) ?? throw new InvalidOperationException($"No deck named '{deck}'");
            var text = item.Trim();
            var all = d.Pages.SelectMany(p => p.Items).ToArray();
            var found = (Guid.TryParse(text, out var id) ? all.FirstOrDefault(i => i.Id == id) : null)
                        ?? all.FirstOrDefault(i => string.Equals(i.Name, text, StringComparison.OrdinalIgnoreCase))
                        ?? throw new InvalidOperationException($"Deck '{d.Name}' has no item '{item}'");
            return (d.Id, found.Id);
        }
    }

    /// <summary>Overrides of the state, then the state's design, then state 0's design.</summary>
    private static DeckItemState Effective(DeckItem item, ItemRuntime? runtime, int state)
    {
        var baseState = item.States.Count > 0 ? item.States[0] : new DeckItemState();
        var designed = state < item.States.Count ? item.States[state] : baseState;
        var o = runtime?.Overrides.GetValueOrDefault(state);
        string Pick(string? over, string own, string fallback) => over ?? (own.Length > 0 ? own : fallback);
        return new DeckItemState
        {
            Title = Pick(o?.Title, designed.Title, baseState.Title),
            TitleColor = Pick(o?.TitleColor, designed.TitleColor, baseState.TitleColor),
            Background = Pick(o?.Background, designed.Background, baseState.Background),
            BackgroundImage = Pick(o?.BackgroundImage, designed.BackgroundImage, baseState.BackgroundImage),
            Icon = Pick(o?.Icon, designed.Icon, baseState.Icon),
            IconColor = Pick(o?.IconColor, designed.IconColor, baseState.IconColor),
            IconUrl = Pick(o?.IconUrl, designed.IconUrl, baseState.IconUrl),
            Value = Pick(o?.Value, designed.Value, baseState.Value),
        };
    }

    private void PublishState(Guid id)
    {
        if (Message is null) return;
        if (View(id) is { } view) Message.Invoke(new DeckMessage(id, "state", view));
    }

    private static void Normalize(DeckSettings s)
    {
        s.Decks ??= [];
        foreach (var d in s.Decks) Normalize(d);
    }

    private static void Normalize(DeckDefinition d)
    {
        d.Rows = Math.Clamp(d.Rows, 1, 20);
        d.Columns = Math.Clamp(d.Columns, 1, 20);
        d.Gap = Math.Clamp(d.Gap, 0, 64);
        d.Name ??= "";
        d.Background ??= "";
        d.Arguments ??= "";
        d.Pages ??= [];
        if (d.Pages.Count == 0) d.Pages.Add(new DeckPage());
        foreach (var p in d.Pages)
        {
            p.Items ??= [];
            foreach (var i in p.Items)
            {
                i.States ??= [];
                if (i.States.Count == 0) i.States.Add(new DeckItemState());
                i.Row = Math.Max(0, i.Row);
                i.Column = Math.Max(0, i.Column);
            }
        }
    }

    public void Flush() => _file?.Flush();

    public void Dispose() => _file?.Dispose();

    private sealed class ItemRuntime
    {
        public int State { get; set; }
        public Dictionary<int, DeckItemOverride> Overrides { get; } = [];
    }

    /// <summary>A runtime change to one state's look; null fields keep the design.</summary>
    private sealed class DeckItemOverride
    {
        public string? Title { get; set; }
        public string? TitleColor { get; set; }
        public string? Background { get; set; }
        public string? BackgroundImage { get; set; }
        public string? Icon { get; set; }
        public string? IconColor { get; set; }
        public string? IconUrl { get; set; }
        public string? Value { get; set; }
    }
}
