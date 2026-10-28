using System.Collections.ObjectModel;
using System.Text.Json;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Commands;
using Cuebot.Core.Storage;
using Cuebot.Core.Users;

namespace Cuebot.App.ViewModels;

/// <summary>A command in the list, grouped under its group header.</summary>
public sealed partial class CommandRowViewModel(CommandsViewModel page, CommandDefinition definition) : ObservableObject
{
    public Guid Id { get; } = definition.Id;

    [ObservableProperty] private CommandDefinition _definition = definition;
    [ObservableProperty] private bool _isSelected;

    public string Name => Definition.Name;
    public string Aliases => Definition.Commands.Count == 0 ? "(no command text)" : string.Join("  ", Definition.Commands);

    partial void OnDefinitionChanged(CommandDefinition value)
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Aliases));
        OnPropertyChanged(nameof(Enabled));
    }

    public bool Enabled
    {
        get => Definition.Enabled;
        set
        {
            if (Definition.Enabled == value) return;
            page.SetEnabled(this, value);
        }
    }

    [RelayCommand]
    private void Select() => page.SelectCommand.Execute(this);
}

/// <summary>A group of commands with a toggle that enables or disables all of them; it can be collapsed.</summary>
public sealed partial class CommandGroupViewModel(CommandsViewModel page, string name, IReadOnlyList<CommandRowViewModel> commands) : ObservableObject
{
    public string Name { get; } = name;
    public string Header => Name.Length == 0 ? "(no group)" : Name;
    public IReadOnlyList<CommandRowViewModel> Commands { get; } = commands;

    /// <summary>True when every command is enabled, false when none is, null when mixed.</summary>
    public bool? Enabled
    {
        get => Commands.All(c => c.Enabled) ? true : Commands.Any(c => c.Enabled) ? null : false;
        set => page.SetGroupEnabled(Name, value ?? true);
    }

    /// <summary>Collapsed groups show only their header; the state survives list rebuilds (filtering, edits).</summary>
    public bool IsExpanded
    {
        get => page.IsGroupExpanded(Name);
        set
        {
            if (value == IsExpanded) return;
            page.SetGroupExpanded(Name, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(Chevron));
        }
    }

    public string Chevron => IsExpanded ? "▾" : "▸";

    [RelayCommand]
    private void ToggleExpanded() => IsExpanded = !IsExpanded;

    public void Refresh() => OnPropertyChanged(nameof(Enabled));
}

/// <summary>A group name with a check box (allowed / denied groups of a command).</summary>
public sealed class GroupChoiceViewModel(string name, bool isChecked, Action<GroupChoiceViewModel> changed) : ObservableObject
{
    private bool _isChecked = isChecked;

    public string Name { get; } = name;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (!SetProperty(ref _isChecked, value)) return;
            changed(this);
        }
    }
}

/// <summary>
/// Editor for one command. Works on an editable copy and writes it back to the <see cref="CommandStore"/> after
/// every change, so the change is live (and saved) immediately.
/// </summary>
public sealed partial class CommandEditorViewModel : ObservableObject
{
    private readonly CommandStore _store;
    private readonly GroupStore _groups;
    private CommandDefinition _c;
    private string _aliasesText;
    private string _sourcesText;
    private string _allowedUsersText;
    private string _deniedUsersText;

    public CommandEditorViewModel(CommandStore store, GroupStore groups, CommandDefinition command)
    {
        _store = store;
        _groups = groups;
        _c = command;
        _aliasesText = string.Join('\n', command.Commands);
        _sourcesText = string.Join('\n', command.Sources);
        _allowedUsersText = string.Join('\n', command.AllowedUsers);
        _deniedUsersText = string.Join('\n', command.DeniedUsers);
        RebuildGroupChoices();
        Validate();
    }

    public Guid Id => _c.Id;
    public string IdText => _c.Id.ToString();

    public static IReadOnlyList<CommandMode> Modes { get; } = Enum.GetValues<CommandMode>();
    public static IReadOnlyList<CommandLocation> Locations { get; } = Enum.GetValues<CommandLocation>();
    public static IReadOnlyList<ChatPermission> Roles { get; } = Enum.GetValues<ChatPermission>();

    public IReadOnlyList<string> GroupSuggestions => _store.Groups;

    public ObservableCollection<GroupChoiceViewModel> AllowedGroups { get; } = [];
    public ObservableCollection<GroupChoiceViewModel> DeniedGroups { get; } = [];
    public bool HasGroups => AllowedGroups.Count > 0;

    [ObservableProperty] private string _regexErrors = "";
    [ObservableProperty] private string _status = "";

    public bool HasRegexErrors => RegexErrors.Length > 0;

    partial void OnRegexErrorsChanged(string value) => OnPropertyChanged(nameof(HasRegexErrors));

    /// <summary>The store's copy changed elsewhere (e.g. "Set command state"): reload unless it matches ours.</summary>
    public void Reload(CommandDefinition stored)
    {
        if (JsonSerializer.Serialize(stored, Json.Options) == JsonSerializer.Serialize(_c, Json.Options)) return;
        _c = stored;
        _aliasesText = string.Join('\n', stored.Commands);
        _sourcesText = string.Join('\n', stored.Sources);
        _allowedUsersText = string.Join('\n', stored.AllowedUsers);
        _deniedUsersText = string.Join('\n', stored.DeniedUsers);
        RebuildGroupChoices();
        Validate();
        OnPropertyChanged(string.Empty);
    }

    public void RefreshCounters()
    {
        OnPropertyChanged(nameof(Counter));
    }

    public void RefreshGroups() => RebuildGroupChoices();

    private void Save(Action change, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        change();
        OnPropertyChanged(property);
        _store.Save(_c);
    }

    public string Name
    {
        get => _c.Name;
        set { if (_c.Name != value) Save(() => _c.Name = value); }
    }

    public string Group
    {
        get => _c.Group;
        set { if (_c.Group != (value ?? "")) Save(() => _c.Group = value ?? ""); }
    }

    public bool Enabled
    {
        get => _c.Enabled;
        set { if (_c.Enabled != value) Save(() => _c.Enabled = value); }
    }

    public bool Include
    {
        get => _c.Include;
        set { if (_c.Include != value) Save(() => _c.Include = value); }
    }

    public CommandMode Mode
    {
        get => _c.Mode;
        set
        {
            if (_c.Mode == value) return;
            Save(() => _c.Mode = value);
            Validate();
        }
    }

    public CommandLocation Location
    {
        get => _c.Location;
        set { if (_c.Location != value) Save(() => _c.Location = value); }
    }

    /// <summary>Command texts (aliases), one per line.</summary>
    public string AliasesText
    {
        get => _aliasesText;
        set
        {
            if (_aliasesText == value) return;
            _aliasesText = value;
            Save(() => _c.Commands = Lines(value));
            Validate();
        }
    }

    /// <summary>Sources, one per line: "Twitch" or "Twitch:Whisper". Empty = every source.</summary>
    public string SourcesText
    {
        get => _sourcesText;
        set
        {
            if (_sourcesText == value) return;
            _sourcesText = value;
            Save(() => _c.Sources = Lines(value));
        }
    }

    public bool CaseSensitive
    {
        get => _c.CaseSensitive;
        set { if (_c.CaseSensitive != value) Save(() => _c.CaseSensitive = value); }
    }

    public bool IgnoreBotAccount
    {
        get => _c.IgnoreBotAccount;
        set { if (_c.IgnoreBotAccount != value) Save(() => _c.IgnoreBotAccount = value); }
    }

    public bool IgnoreInternal
    {
        get => _c.IgnoreInternal;
        set { if (_c.IgnoreInternal != value) Save(() => _c.IgnoreInternal = value); }
    }

    public decimal? GlobalCooldownSeconds
    {
        get => (decimal)_c.GlobalCooldownSeconds;
        set
        {
            var v = (double)Math.Max(0, value ?? 0);
            if (!_c.GlobalCooldownSeconds.Equals(v)) Save(() => _c.GlobalCooldownSeconds = v);
        }
    }

    public decimal? UserCooldownSeconds
    {
        get => (decimal)_c.UserCooldownSeconds;
        set
        {
            var v = (double)Math.Max(0, value ?? 0);
            if (!_c.UserCooldownSeconds.Equals(v)) Save(() => _c.UserCooldownSeconds = v);
        }
    }

    public ChatPermission MinimumRole
    {
        get => _c.MinimumRole;
        set { if (_c.MinimumRole != value) Save(() => _c.MinimumRole = value); }
    }

    public string AllowedUsersText
    {
        get => _allowedUsersText;
        set
        {
            if (_allowedUsersText == value) return;
            _allowedUsersText = value;
            Save(() => _c.AllowedUsers = Lines(value));
        }
    }

    public string DeniedUsersText
    {
        get => _deniedUsersText;
        set
        {
            if (_deniedUsersText == value) return;
            _deniedUsersText = value;
            Save(() => _c.DeniedUsers = Lines(value));
        }
    }

    public bool PersistCounter
    {
        get => _c.PersistCounter;
        set { if (_c.PersistCounter != value) Save(() => _c.PersistCounter = value); }
    }

    public bool PersistUserCounter
    {
        get => _c.PersistUserCounter;
        set { if (_c.PersistUserCounter != value) Save(() => _c.PersistUserCounter = value); }
    }

    public long Counter => _store.GetCounter(_c.Id);

    [RelayCommand]
    private void ResetCounter()
    {
        _store.ResetCounter(_c.Id);
        Status = "Counter reset.";
    }

    [RelayCommand]
    private void ResetUserCounters()
    {
        _store.ResetUserCounters(_c.Id);
        Status = "Every user's counter reset.";
    }

    private void RebuildGroupChoices()
    {
        Fill(AllowedGroups, _c.AllowedGroups, choice => SetGroups(choice, allowed: true));
        Fill(DeniedGroups, _c.DeniedGroups, choice => SetGroups(choice, allowed: false));
        OnPropertyChanged(nameof(HasGroups));

        void Fill(ObservableCollection<GroupChoiceViewModel> target, List<string> selected, Action<GroupChoiceViewModel> changed)
        {
            target.Clear();
            var names = _groups.Names.Concat(selected).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase);
            foreach (var name in names)
                target.Add(new GroupChoiceViewModel(name, selected.Contains(name, StringComparer.OrdinalIgnoreCase), changed));
        }
    }

    private void SetGroups(GroupChoiceViewModel choice, bool allowed)
    {
        var list = allowed ? _c.AllowedGroups : _c.DeniedGroups;
        list.RemoveAll(g => string.Equals(g, choice.Name, StringComparison.OrdinalIgnoreCase));
        if (choice.IsChecked) list.Add(choice.Name);
        _store.Save(_c);
    }

    private void Validate()
    {
        if (_c.Mode != CommandMode.Regex)
        {
            RegexErrors = "";
            return;
        }
        var errors = _c.Commands
            .Select(pattern => (pattern, error: CommandMatcher.ValidateRegex(pattern)))
            .Where(x => x.error is not null)
            .Select(x => $"{x.pattern}: {x.error}");
        RegexErrors = string.Join('\n', errors);
    }

    private static List<string> Lines(string text) =>
        text.ReplaceLineEndings("\n").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
}

/// <summary>Commands page: commands.json grouped by group, with an editor for the selected command.</summary>
public sealed partial class CommandsViewModel : PageViewModel
{
    private readonly CommandStore _store;
    private readonly GroupStore _groups;
    private readonly Dictionary<Guid, CommandRowViewModel> _rows = [];
    private readonly HashSet<string> _collapsed = new(StringComparer.OrdinalIgnoreCase);

    public CommandsViewModel(CommandStore store, GroupStore groups) : base("Commands")
    {
        _store = store;
        _groups = groups;
        store.Changed += () => OnUi(Refresh);
        store.CountersChanged += id => OnUi(() =>
        {
            if (Editor?.Id == id) Editor.RefreshCounters();
        });
        groups.Changed += () => OnUi(() => Editor?.RefreshGroups());
        Refresh();
    }

    public ObservableCollection<CommandGroupViewModel> Groups { get; } = [];

    [ObservableProperty] private string _filter = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private CommandEditorViewModel? _editor;

    public bool HasSelection => Editor is not null;

    public string Summary => _rows.Count == 0
        ? "No commands yet. A command listens to every connected chat and fires the 'Command triggered' trigger."
        : $"{_rows.Count} command(s)";

    partial void OnFilterChanged(string value) => Refresh();

    /// <summary>Rebuilds the grouped list from the store, reusing row view models, and reloads the editor if needed.</summary>
    private void Refresh()
    {
        var all = _store.All();
        var seen = new HashSet<Guid>();
        foreach (var c in all)
        {
            seen.Add(c.Id);
            if (_rows.TryGetValue(c.Id, out var row)) row.Definition = c;
            else _rows[c.Id] = new CommandRowViewModel(this, c);
        }
        foreach (var gone in _rows.Keys.Where(id => !seen.Contains(id)).ToArray()) _rows.Remove(gone);

        var visible = all.Where(c => Filter.Length == 0
                                     || c.Name.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                                     || c.Group.Contains(Filter, StringComparison.OrdinalIgnoreCase)
                                     || c.Commands.Any(t => t.Contains(Filter, StringComparison.OrdinalIgnoreCase)));
        Groups.Clear();
        foreach (var group in visible.GroupBy(c => c.Group, StringComparer.OrdinalIgnoreCase)
                     .OrderBy(g => g.Key.Length == 0 ? 1 : 0).ThenBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var rows = group.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).Select(c => _rows[c.Id]).ToArray();
            Groups.Add(new CommandGroupViewModel(this, group.First().Group, rows));
        }

        if (Editor is { } editor)
        {
            if (_store.Get(editor.Id) is { } stored) editor.Reload(stored);
            else Select(null);
        }
        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void Select(CommandRowViewModel? row)
    {
        foreach (var r in _rows.Values) r.IsSelected = r == row;
        Editor = row is not null && _store.Get(row.Id) is { } copy ? new CommandEditorViewModel(_store, _groups, copy) : null;
    }

    internal void SetEnabled(CommandRowViewModel row, bool enabled) => _store.SetEnabled(row.Id, enabled);

    internal void SetGroupEnabled(string group, bool enabled) => _store.SetGroupEnabled(group, enabled);

    internal bool IsGroupExpanded(string group) => !_collapsed.Contains(group);

    internal void SetGroupExpanded(string group, bool expanded)
    {
        if (expanded) _collapsed.Remove(group);
        else _collapsed.Add(group);
    }

    /// <summary>Ctrl+* (Streamer.bot's Expand All).</summary>
    [RelayCommand]
    private void ExpandAll()
    {
        foreach (var group in Groups) group.IsExpanded = true;
    }

    /// <summary>Ctrl+- (Streamer.bot's Collapse All).</summary>
    [RelayCommand]
    private void CollapseAll()
    {
        foreach (var group in Groups) group.IsExpanded = false;
    }

    /// <summary>Ctrl+O: turns the selected command on or off.</summary>
    [RelayCommand]
    private void ToggleSelectedEnabled()
    {
        if (Editor is not null && _rows.TryGetValue(Editor.Id, out var row)) row.Enabled = !row.Enabled;
    }

    /// <summary>The selected command's group ("" when it has none), for Ctrl+R.</summary>
    public string? SelectedGroup => Editor is not null && _store.Get(Editor.Id) is { } c ? c.Group : null;

    /// <summary>Ctrl+R: renames the selected command's group for every command in it. Returns the number renamed.</summary>
    public int RenameSelectedGroup(string newName)
    {
        if (SelectedGroup is not { Length: > 0 } old || string.IsNullOrWhiteSpace(newName)) return 0;
        var expanded = IsGroupExpanded(old);
        var renamed = _store.RenameGroup(old, newName.Trim());
        SetGroupExpanded(newName.Trim(), expanded);
        return renamed;
    }

    private void AddAndSelect(CommandDefinition command)
    {
        _store.Save(command);
        Filter = "";
        if (_rows.TryGetValue(command.Id, out var row)) Select(row);
    }

    [RelayCommand]
    private void NewCommand()
    {
        var name = "New command";
        for (var i = 2; _rows.Values.Any(r => r.Name.Equals(name, StringComparison.OrdinalIgnoreCase)); i++) name = $"New command {i}";
        var group = Editor is { } e && _store.Get(e.Id) is { } current ? current.Group : "";
        AddAndSelect(new CommandDefinition { Name = name, Group = group, Commands = ["!" + name.ToLowerInvariant().Replace(" ", "")] });
    }

    [RelayCommand]
    private void DuplicateCommand()
    {
        if (Editor is null || _store.Get(Editor.Id) is not { } copy) return;
        copy.Id = Guid.NewGuid();
        copy.Name += " (copy)";
        AddAndSelect(copy);
    }

    [RelayCommand]
    private void DeleteCommand()
    {
        if (Editor is null) return;
        var id = Editor.Id;
        Select(null);
        _store.Remove(id);
    }
}
