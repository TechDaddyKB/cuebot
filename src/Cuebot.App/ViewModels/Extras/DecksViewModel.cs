using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Decks;

namespace Cuebot.App.ViewModels.Extras;

/// <summary>
/// The Decks page: create decks (grids of buttons served by the HTTP server at /deck/&lt;id&gt;), their pages and
/// buttons. Every edit is saved at once and pushed to open deck pages.
/// </summary>
public sealed partial class DecksViewModel : PageViewModel
{
    private readonly DeckService _decks;
    private readonly Func<string> _baseUrl;
    private readonly Func<bool> _httpEnabled;
    private readonly Func<string>? _deckToken;

    /// <param name="baseUrl">The HTTP server's base address, e.g. http://127.0.0.1:7474.</param>
    /// <param name="httpEnabled">Whether the HTTP server is on (decks need it).</param>
    /// <param name="deckToken">The deck token other devices need (empty = none); deck links include it.</param>
    /// <param name="actionName">The name of the action a button refers to by name or id; null when there is no such action.</param>
    public DecksViewModel(DeckService decks, Func<string> baseUrl, Func<bool> httpEnabled, Func<IEnumerable<string>> actionNames,
        Func<string>? deckToken = null, Func<string, string?>? actionName = null)
        : base("Decks")
    {
        ActionName = actionName ?? (a => actionNames().FirstOrDefault(n => string.Equals(n, a, StringComparison.OrdinalIgnoreCase)));
        _decks = decks;
        _baseUrl = baseUrl;
        _deckToken = deckToken;
        _httpEnabled = httpEnabled;
        ActionNames = actionNames;
        foreach (var d in decks.Decks) Decks.Add(new DeckEditorViewModel(this, d));
        SelectedDeck = Decks.FirstOrDefault();
        // Decks are also made and changed outside this page (the Assistant, the REST API, MCP): show those too.
        decks.Changed += () => { if (!_saving) OnUi(Sync); };
    }

    /// <summary>Set while this page itself saves, so its own edits do not rebuild the editor being typed in.</summary>
    private bool _saving;

    /// <summary>Brings the list in line with the saved decks: new ones added, removed ones dropped, changed ones reloaded.</summary>
    internal void Sync()
    {
        var saved = _decks.Decks;
        var selected = SelectedDeck?.Id;
        foreach (var gone in Decks.Where(d => saved.All(s => s.Id != d.Id)).ToArray()) Decks.Remove(gone);
        foreach (var deck in saved)
        {
            var index = Decks.ToList().FindIndex(d => d.Id == deck.Id);
            if (index < 0) Decks.Add(new DeckEditorViewModel(this, deck));
            else if (!SameDesign(Decks[index].Definition, deck)) Decks[index] = new DeckEditorViewModel(this, deck);
        }
        SelectedDeck = Decks.FirstOrDefault(d => d.Id == selected) ?? Decks.FirstOrDefault();
    }

    private static bool SameDesign(DeckDefinition a, DeckDefinition b) =>
        System.Text.Json.JsonSerializer.Serialize(a, Cuebot.Core.Storage.Json.Options) == System.Text.Json.JsonSerializer.Serialize(b, Cuebot.Core.Storage.Json.Options);

    private void SaveOwn(Action save)
    {
        _saving = true;
        try { save(); }
        finally { _saving = false; }
    }

    public ObservableCollection<DeckEditorViewModel> Decks { get; } = [];

    /// <summary>Action names for the button editors' action dropdowns.</summary>
    public Func<IEnumerable<string>> ActionNames { get; }

    /// <summary>The name of the action a button refers to (by name or id), or null.</summary>
    internal Func<string, string?> ActionName { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private DeckEditorViewModel? _selectedDeck;

    public bool HasSelection => SelectedDeck is not null;

    public bool HttpEnabled => _httpEnabled();

    public string HttpHint => HttpEnabled
        ? "Open a deck's address in any browser on this computer (or on your network when LAN access is on in Servers)."
        : "Decks are served by the HTTP server, which is off: turn it on in Servers.";

    [RelayCommand]
    private void AddDeck()
    {
        var deck = new DeckDefinition { Name = $"Deck {Decks.Count + 1}" };
        deck.Pages[0].Items.Add(new DeckItem { Name = "Button 1", States = [new DeckItemState { Title = "Button 1", Background = "#3a7bd5" }] });
        SaveOwn(() => _decks.Save(deck));
        var vm = new DeckEditorViewModel(this, deck);
        Decks.Add(vm);
        SelectedDeck = vm;
    }

    [RelayCommand]
    private void RemoveDeck()
    {
        if (SelectedDeck is not { } deck) return;
        SaveOwn(() => _decks.Remove(deck.Id));
        var index = Decks.IndexOf(deck);
        Decks.Remove(deck);
        SelectedDeck = Decks.Count == 0 ? null : Decks[Math.Clamp(index, 0, Decks.Count - 1)];
    }

    internal void Save(DeckDefinition deck) => SaveOwn(() => _decks.Save(deck));

    internal void ResetRuntime(Guid id) => _decks.ResetRuntime(id);

    internal string UrlOf(Guid id)
    {
        var url = $"{_baseUrl().TrimEnd('/')}/deck/{id}";
        return _deckToken?.Invoke() is { Length: > 0 } token ? $"{url}?token={Uri.EscapeDataString(token)}" : url;
    }
}

/// <summary>One deck's settings, pages and buttons.</summary>
public sealed partial class DeckEditorViewModel : ObservableObject
{
    private readonly DecksViewModel _owner;

    internal DeckEditorViewModel(DecksViewModel owner, DeckDefinition definition)
    {
        _owner = owner;
        Definition = definition;
        foreach (var p in definition.Pages) Pages.Add(p.Name);
        SelectedPageIndex = 0;
    }

    public DeckDefinition Definition { get; }
    public Guid Id => Definition.Id;
    public string Url => _owner.UrlOf(Id);
    public ObservableCollection<string> Pages { get; } = [];
    public ObservableCollection<DeckItemEditorViewModel> Items { get; } = [];

    public string Name
    {
        get => Definition.Name;
        set => Edit(() => Definition.Name = value);
    }

    public bool Enabled
    {
        get => Definition.Enabled;
        set => Edit(() => Definition.Enabled = value);
    }

    public decimal? Rows
    {
        get => Definition.Rows;
        set => Edit(() => Definition.Rows = Math.Clamp((int)(value ?? 1), 1, 20));
    }

    public decimal? Columns
    {
        get => Definition.Columns;
        set => Edit(() => Definition.Columns = Math.Clamp((int)(value ?? 1), 1, 20));
    }

    public decimal? Gap
    {
        get => Definition.Gap;
        set => Edit(() => Definition.Gap = Math.Clamp((int)(value ?? 0), 0, 64));
    }

    public string Background
    {
        get => Definition.Background;
        set => Edit(() => Definition.Background = value);
    }

    public string Arguments
    {
        get => Definition.Arguments;
        set => Edit(() => Definition.Arguments = value);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageName))]
    private int _selectedPageIndex = -1;

    private DeckPage? Page => SelectedPageIndex >= 0 && SelectedPageIndex < Definition.Pages.Count ? Definition.Pages[SelectedPageIndex] : null;

    public string PageName
    {
        get => Page?.Name ?? "";
        set
        {
            if (Page is not { } page || page.Name == value) return;
            page.Name = value;
            var index = SelectedPageIndex;
            Pages[index] = value;
            SelectedPageIndex = index;
            Save();
        }
    }

    partial void OnSelectedPageIndexChanged(int value)
    {
        Items.Clear();
        if (Page is { } page)
            foreach (var item in page.Items) Items.Add(new DeckItemEditorViewModel(this, item, _owner.ActionNames, _owner.ActionName));
    }

    [RelayCommand]
    private void AddPage()
    {
        var page = new DeckPage { Name = $"Page {Definition.Pages.Count + 1}" };
        Definition.Pages.Add(page);
        Pages.Add(page.Name);
        SelectedPageIndex = Pages.Count - 1;
        Save();
    }

    [RelayCommand]
    private void RemovePage()
    {
        if (Definition.Pages.Count <= 1 || Page is null) return;
        var index = SelectedPageIndex;
        Definition.Pages.RemoveAt(index);
        Pages.RemoveAt(index);
        SelectedPageIndex = Math.Clamp(index, 0, Pages.Count - 1);
        Save();
    }

    /// <summary>Adds a button in the first free cell of the page (or below the grid when it is full).</summary>
    [RelayCommand]
    private void AddItem()
    {
        if (Page is not { } page) return;
        var taken = page.Items.Select(i => (i.Row, i.Column)).ToHashSet();
        var cell = Enumerable.Range(0, Definition.Rows * Definition.Columns)
            .Select(n => (Row: n / Definition.Columns, Column: n % Definition.Columns))
            .FirstOrDefault(c => !taken.Contains(c), (Row: Definition.Rows, Column: 0));
        var number = Definition.Pages.Sum(p => p.Items.Count) + 1;
        var name = $"Button {number}";
        var item = new DeckItem { Name = name, Row = cell.Row, Column = cell.Column, States = [new DeckItemState { Title = name }] };
        page.Items.Add(item);
        Items.Add(new DeckItemEditorViewModel(this, item, _owner.ActionNames, _owner.ActionName));
        Save();
    }

    internal void RemoveItem(DeckItemEditorViewModel item)
    {
        Page?.Items.Remove(item.Definition);
        Items.Remove(item);
        Save();
    }

    [RelayCommand]
    private void ResetRuntime() => _owner.ResetRuntime(Id);

    internal void Save() => _owner.Save(Definition);

    private void Edit(Action change, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        change();
        OnPropertyChanged(property);
        Save();
    }
}

/// <summary>One button: where it sits, what it runs, and how it looks in its default and active states.</summary>
public sealed partial class DeckItemEditorViewModel : ObservableObject
{
    private readonly DeckEditorViewModel _deck;

    /// <summary>The action dropdown's first entry: the button runs no action (it only fires the Deck item pressed trigger).</summary>
    public const string NoAction = "(none)";

    /// <summary>Common icons for the icon dropdown; any other Iconify name can still be typed.</summary>
    public static readonly IReadOnlyList<string> CommonIcons =
    [
        "mdi:play", "mdi:pause", "mdi:stop", "mdi:record-rec", "mdi:broadcast", "mdi:broadcast-off",
        "mdi:microphone", "mdi:microphone-off", "mdi:volume-high", "mdi:volume-off", "mdi:music", "mdi:headphones",
        "mdi:video", "mdi:video-off", "mdi:camera", "mdi:monitor", "mdi:monitor-off", "mdi:image",
        "mdi:chat", "mdi:chat-off", "mdi:message-text", "mdi:bullhorn", "mdi:bell", "mdi:bell-off",
        "mdi:heart", "mdi:star", "mdi:trophy", "mdi:gift", "mdi:cash", "mdi:diamond-stone",
        "mdi:account", "mdi:account-group", "mdi:hand-wave", "mdi:emoticon-happy", "mdi:party-popper", "mdi:fire",
        "mdi:timer", "mdi:clock", "mdi:dice-6", "mdi:gamepad-variant", "mdi:robot", "mdi:lightning-bolt",
        "mdi:home", "mdi:arrow-left", "mdi:arrow-right", "mdi:refresh", "mdi:check", "mdi:close",
        "mdi:twitch", "mdi:youtube", "mdi:discord", "mdi:twitter", "mdi:instagram", "mdi:spotify",
    ];

    internal DeckItemEditorViewModel(DeckEditorViewModel deck, DeckItem definition, Func<IEnumerable<string>> actions,
        Func<string, string?> actionName)
    {
        _deck = deck;
        Definition = definition;
        _actionName = actionName;
        if (Definition.States.Count == 0) Definition.States.Add(new DeckItemState());
        var choices = new List<string> { NoAction };
        choices.AddRange(actions().Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase));
        // A button whose action was deleted (or renamed) keeps it, marked, until another one is chosen.
        if (MissingAction is { } missing) choices.Add(missing);
        ActionChoices = choices;
    }

    private readonly Func<string, string?> _actionName;

    public DeckItem Definition { get; }

    public IReadOnlyList<string> IconChoices => CommonIcons;

    /// <summary>"(none)", then every action by name, then the button's own action if it no longer exists.</summary>
    public IReadOnlyList<string> ActionChoices { get; }

    private string? MissingAction =>
        string.IsNullOrWhiteSpace(Definition.Action) || _actionName(Definition.Action.Trim()) is not null ? null : $"Missing: {Definition.Action.Trim()}";

    /// <summary>
    /// The action dropdown's choice. Buttons saved with an action id (the Assistant writes ids) show the action's name;
    /// choosing a name stores the name, as the editor always has.
    /// </summary>
    public string SelectedAction
    {
        get => string.IsNullOrWhiteSpace(Definition.Action) ? NoAction : _actionName(Definition.Action.Trim()) ?? MissingAction!;
        set
        {
            if (value is null || value == SelectedAction || value.StartsWith("Missing: ", StringComparison.Ordinal)) return;
            Edit(() => Definition.Action = value == NoAction ? "" : value);
        }
    }
    private DeckItemState Default => Definition.States[0];

    private DeckItemState Active
    {
        get
        {
            while (Definition.States.Count < 2) Definition.States.Add(new DeckItemState());
            return Definition.States[1];
        }
    }

    public string Name { get => Definition.Name; set => Edit(() => Definition.Name = value); }
    public decimal? Row { get => Definition.Row + 1; set => Edit(() => Definition.Row = Math.Max(0, (int)(value ?? 1) - 1)); }
    public decimal? Column { get => Definition.Column + 1; set => Edit(() => Definition.Column = Math.Max(0, (int)(value ?? 1) - 1)); }
    public string Action
    {
        get => Definition.Action;
        set
        {
            Edit(() => Definition.Action = value ?? "");
            OnPropertyChanged(nameof(SelectedAction));
        }
    }
    public string Arguments { get => Definition.Arguments; set => Edit(() => Definition.Arguments = value); }
    public bool ToggleOnPress { get => Definition.ToggleOnPress; set => Edit(() => Definition.ToggleOnPress = value); }
    public string Title { get => Default.Title; set => Edit(() => Default.Title = value); }
    public string TitleColor { get => Default.TitleColor; set => Edit(() => Default.TitleColor = value); }
    public string Background { get => Default.Background; set => Edit(() => Default.Background = value); }
    public string BackgroundImage { get => Default.BackgroundImage; set => Edit(() => Default.BackgroundImage = value); }
    public string Icon { get => Default.Icon; set => Edit(() => Default.Icon = value); }
    public string IconColor { get => Default.IconColor; set => Edit(() => Default.IconColor = value); }

    /// <summary>State 1 ("active"): what the button shows after Item Toggle State / Item Set State 1.</summary>
    public string ActiveTitle
    {
        get => Definition.States.Count > 1 ? Definition.States[1].Title : "";
        set => Edit(() => Active.Title = value);
    }

    public string ActiveBackground
    {
        get => Definition.States.Count > 1 ? Definition.States[1].Background : "";
        set => Edit(() => Active.Background = value);
    }

    public string Position => string.Create(CultureInfo.InvariantCulture, $"Row {Definition.Row + 1}, column {Definition.Column + 1}");

    [RelayCommand]
    private void Remove() => _deck.RemoveItem(this);

    private void Edit(Action change, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        change();
        OnPropertyChanged(property);
        OnPropertyChanged(nameof(Position));
        _deck.Save();
    }
}
