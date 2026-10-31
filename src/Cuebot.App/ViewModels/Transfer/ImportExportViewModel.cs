using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Desktop.Clipboard;
using Cuebot.Core.Transfer;

namespace Cuebot.App.ViewModels.Transfer;

/// <summary>
/// "Import / Export": paste or open a Cuebot or Streamer.bot import string, review it as a tree (include checkboxes,
/// conflict markers, overwrite / add as new / skip), read the import report, then import. The export tab builds a
/// Cuebot string or .cuebot file from selected actions, commands, timers and global variables.
/// </summary>
public sealed class ImportExportViewModel : PageViewModel
{
    private readonly TransferService _transfer;
    private readonly ProfileService _profile;
    private readonly CommandStore _commands;
    private readonly GlobalVariables _globals;
    private readonly IClipboardService? _clipboard;
    private readonly Func<string?>? _backupBeforeReplace;
    private ImportPlan? _plan;
    private string _importText = "";
    private string _importStatus = "";
    private string _previewHeader = "";
    private string _exportStatus = "";
    private string _exportString = "";
    private bool _includeDependencies = true;
    private bool _importing;

    /// <param name="backupBeforeReplace">Called before an import that overwrites existing items; makes a backup when
    /// the "Back up before an import replaces data" setting is on and returns its file name, else null.</param>
    public ImportExportViewModel(TransferService transfer, ProfileService profile, CommandStore commands, GlobalVariables globals,
        IClipboardService? clipboard = null, Func<string?>? backupBeforeReplace = null) : base("Import / Export")
    {
        _backupBeforeReplace = backupBeforeReplace;
        _transfer = transfer;
        _profile = profile;
        _commands = commands;
        _globals = globals;
        _clipboard = clipboard;
        PreviewCommand = new RelayCommand(Preview);
        ImportCommand = new AsyncRelayCommand(ImportAsync, () => HasPlan && !_importing);
        ClearImportCommand = new RelayCommand(ClearImport);
        PasteCommand = new AsyncRelayCommand(PasteAsync);
        BuildExportCommand = new RelayCommand(BuildExport);
        CopyExportCommand = new AsyncRelayCommand(CopyExportAsync);
        RefreshExportCommand = new RelayCommand(RefreshExportLists);
        _profile.Changed += () => OnUi(RefreshExportLists);
        _commands.Changed += () => OnUi(RefreshExportLists);
        RefreshExportLists();
    }

    // ---------------- import ----------------

    public string ImportText
    {
        get => _importText;
        set => SetProperty(ref _importText, value);
    }

    public string ImportStatus
    {
        get => _importStatus;
        set => SetProperty(ref _importStatus, value);
    }

    /// <summary>"Name · by Author · Streamer.bot" for the loaded preview.</summary>
    public string PreviewHeader
    {
        get => _previewHeader;
        private set => SetProperty(ref _previewHeader, value);
    }

    public bool HasPlan => _plan is not null;
    public ImportPlan? Plan => _plan;

    public ObservableCollection<ImportGroupViewModel> PreviewGroups { get; } = [];
    public ObservableCollection<string> ReportMapped { get; } = [];
    public ObservableCollection<string> ReportPlaceholders { get; } = [];
    public ObservableCollection<string> ReportWarnings { get; } = [];
    public bool HasPlaceholders => ReportPlaceholders.Count > 0;
    public bool HasReportWarnings => ReportWarnings.Count > 0;
    public bool HasMapped => ReportMapped.Count > 0;

    public IRelayCommand PreviewCommand { get; }
    public IAsyncRelayCommand ImportCommand { get; }
    public IRelayCommand ClearImportCommand { get; }
    public IAsyncRelayCommand PasteCommand { get; }

    /// <summary>Loads text from an opened file and previews it.</summary>
    public void LoadText(string text, string source)
    {
        ImportText = text;
        Preview();
        if (HasPlan) ImportStatus = $"Loaded {source}. " + ImportStatus;
    }

    public void Preview()
    {
        ClearPreview();
        try
        {
            _plan = _transfer.Preview(ImportText);
        }
        catch (TransferFormatException ex)
        {
            ImportStatus = "Cannot import: " + ex.Message;
            RaisePlanChanged();
            return;
        }

        var header = _plan.Bundle.Header;
        PreviewHeader = string.Join(" · ", new[]
        {
            header.Name.Length > 0 ? header.Name : "Untitled import",
            header.Author.Length > 0 ? "by " + header.Author : "",
            header.BundleVersion.Length > 0 ? "v" + header.BundleVersion : "",
            _plan.Source == PayloadKind.StreamerBot ? "from Streamer.bot" : "Cuebot bundle",
        }.Where(s => s.Length > 0)) + (header.Description.Length > 0 ? "\n" + header.Description : "");

        foreach (var (kind, title) in new[]
                 {
                     (TransferKind.Action, "Actions"), (TransferKind.Queue, "Queues"), (TransferKind.Command, "Commands"),
                     (TransferKind.Timer, "Timers"), (TransferKind.Global, "Global variables"),
                 })
        {
            var items = _plan.Of(kind).Select(i => new ImportItemViewModel(i)).ToList();
            if (items.Count > 0) PreviewGroups.Add(new ImportGroupViewModel(title, items));
        }
        foreach (var line in _plan.Report.Mapped) ReportMapped.Add(line);
        foreach (var p in _plan.Report.Placeholders) ReportPlaceholders.Add($"{p.ActionName}: {p.ItemKind} type {p.OriginalType}");
        foreach (var w in _plan.Report.Warnings) ReportWarnings.Add(w);
        var conflicts = _plan.Items.Count(i => i.HasConflict);
        ImportStatus = $"{_plan.Items.Count} item(s) ready to import" + (conflicts > 0 ? $", {conflicts} conflict(s) — choose overwrite, add as new or skip" : "")
                       + (ReportPlaceholders.Count > 0 ? $", {ReportPlaceholders.Count} placeholder(s)" : "") + ".";
        RaisePlanChanged();
    }

    public async Task ImportAsync()
    {
        if (_plan is null) return;
        _importing = true;
        ImportCommand.NotifyCanExecuteChanged();
        try
        {
            var replaces = _plan.Items.Any(i => i.Include && i.HasConflict && i.Mode == ImportMode.Overwrite);
            var backup = replaces ? await Task.Run(() => _backupBeforeReplace?.Invoke()) : null;
            var changes = await _transfer.ApplyAsync(_plan);
            ClearPreview();
            ImportStatus = "Imported: " + changes.Summary + "." + (changes.Renamed.Count > 0 ? " Renamed: " + string.Join("; ", changes.Renamed) : "")
                           + (backup is null ? "" : $" A backup of what you had before is in Settings > Backups ({backup}).");
        }
        finally
        {
            _importing = false;
            RaisePlanChanged();
        }
    }

    private void ClearImport()
    {
        ImportText = "";
        ImportStatus = "";
        ClearPreview();
        RaisePlanChanged();
    }

    private async Task PasteAsync()
    {
        if (_clipboard is not { IsAvailable: true }) return;
        if (await _clipboard.GetTextAsync() is { Length: > 0 } text)
        {
            ImportText = text;
            Preview();
        }
    }

    private void ClearPreview()
    {
        _plan = null;
        PreviewHeader = "";
        PreviewGroups.Clear();
        ReportMapped.Clear();
        ReportPlaceholders.Clear();
        ReportWarnings.Clear();
    }

    private void RaisePlanChanged()
    {
        OnPropertyChanged(nameof(HasPlan));
        OnPropertyChanged(nameof(Plan));
        OnPropertyChanged(nameof(HasPlaceholders));
        OnPropertyChanged(nameof(HasReportWarnings));
        OnPropertyChanged(nameof(HasMapped));
        ImportCommand.NotifyCanExecuteChanged();
    }

    // ---------------- export ----------------

    public ObservableCollection<ExportChoiceViewModel> ExportActions { get; } = [];
    public ObservableCollection<ExportChoiceViewModel> ExportCommands { get; } = [];
    public ObservableCollection<ExportChoiceViewModel> ExportTimers { get; } = [];
    public ObservableCollection<ExportChoiceViewModel> ExportGlobals { get; } = [];

    public string ExportName
    {
        get => _exportName;
        set => SetProperty(ref _exportName, value);
    }

    public string ExportAuthor
    {
        get => _exportAuthor;
        set => SetProperty(ref _exportAuthor, value);
    }

    public string ExportDescription
    {
        get => _exportDescription;
        set => SetProperty(ref _exportDescription, value);
    }

    public string ExportVersion
    {
        get => _exportVersion;
        set => SetProperty(ref _exportVersion, value);
    }

    private string _exportName = "";
    private string _exportAuthor = "";
    private string _exportDescription = "";
    private string _exportVersion = "";

    public bool IncludeDependencies
    {
        get => _includeDependencies;
        set => SetProperty(ref _includeDependencies, value);
    }

    /// <summary>The last built import string.</summary>
    public string ExportString
    {
        get => _exportString;
        private set => SetProperty(ref _exportString, value);
    }

    public string ExportStatus
    {
        get => _exportStatus;
        set => SetProperty(ref _exportStatus, value);
    }

    public IRelayCommand BuildExportCommand { get; }
    public IAsyncRelayCommand CopyExportCommand { get; }
    public IRelayCommand RefreshExportCommand { get; }

    public ExportSelection Selection()
    {
        var selection = new ExportSelection
        {
            IncludeDependencies = IncludeDependencies,
            Header = new TransferHeader
            {
                Name = ExportName.Trim(), Author = ExportAuthor.Trim(), Description = ExportDescription.Trim(), BundleVersion = ExportVersion.Trim(),
            },
        };
        foreach (var a in ExportActions.Where(c => c.IsSelected)) selection.ActionIds.Add(a.Id);
        foreach (var c in ExportCommands.Where(c => c.IsSelected)) selection.CommandIds.Add(c.Id);
        foreach (var t in ExportTimers.Where(c => c.IsSelected)) selection.TimerIds.Add(t.Id);
        foreach (var g in ExportGlobals.Where(c => c.IsSelected)) selection.GlobalNames.Add(g.Name);
        return selection;
    }

    public void BuildExport()
    {
        var bundle = _transfer.Export(Selection());
        if (bundle.IsEmpty)
        {
            ExportString = "";
            ExportStatus = "Select at least one action, command, timer or global variable.";
            return;
        }
        ExportString = TransferCodec.Encode(bundle);
        ExportStatus = $"{bundle.Actions.Count} action(s), {bundle.Queues.Count} queue(s), {bundle.Commands.Count} command(s), "
                       + $"{bundle.Timers.Count} timer(s), {bundle.Globals.Count} global(s) · {ExportString.Length:N0} characters.";
    }

    /// <summary>Indented bundle JSON for a .cuebot file, or null when nothing is selected.</summary>
    public string? ExportFileContent()
    {
        var bundle = _transfer.Export(Selection());
        if (bundle.IsEmpty)
        {
            ExportStatus = "Select at least one action, command, timer or global variable.";
            return null;
        }
        return TransferCodec.ToJson(bundle);
    }

    public void ReportSaved(string fileName) => ExportStatus = $"Saved {fileName}.";

    private async Task CopyExportAsync()
    {
        if (ExportString.Length == 0) BuildExport();
        if (ExportString.Length == 0) return;
        if (_clipboard is not { IsAvailable: true })
        {
            ExportStatus = "The clipboard is not available; select the text and copy it instead.";
            return;
        }
        await _clipboard.SetTextAsync(ExportString);
        ExportStatus = "Copied the import string to the clipboard.";
    }

    private void RefreshExportLists()
    {
        var snapshot = _profile.Snapshot;
        Sync(ExportActions, snapshot.Actions.OrderBy(a => a.Group).ThenBy(a => a.Name).Select(a => (a.Id, a.Name, a.Group)));
        Sync(ExportCommands, _commands.Snapshot.OrderBy(c => c.Group).ThenBy(c => c.Name).Select(c => (c.Id, c.Name, c.Group)));
        Sync(ExportTimers, snapshot.Timers.OrderBy(t => t.Name).Select(t => (t.Id, t.Name, "")));
        Sync(ExportGlobals, _globals.Snapshot().OrderBy(g => g.Name).Select(g => (Guid.Empty, g.Name, g.Persisted ? "" : "temporary")));
    }

    /// <summary>Rebuilds a choice list, keeping what was ticked.</summary>
    private static void Sync(ObservableCollection<ExportChoiceViewModel> list, IEnumerable<(Guid Id, string Name, string Group)> items)
    {
        var selected = list.Where(c => c.IsSelected).Select(c => c.Key).ToHashSet();
        list.Clear();
        foreach (var (id, name, group) in items)
        {
            var choice = new ExportChoiceViewModel(id, name, group);
            choice.IsSelected = selected.Contains(choice.Key);
            list.Add(choice);
        }
    }
}

/// <summary>A tickable action/command/timer/global on the export tab.</summary>
public sealed class ExportChoiceViewModel(Guid id, string name, string group) : ObservableObject
{
    private bool _isSelected;

    public Guid Id { get; } = id;
    public string Name { get; } = name;
    public string Group { get; } = group;
    public string Key => Id == Guid.Empty ? "g:" + Name.ToLowerInvariant() : Id.ToString();
    public bool HasGroup => Group.Length > 0;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

/// <summary>A category node in the import preview tree ("Actions", "Commands", …).</summary>
public sealed class ImportGroupViewModel : ObservableObject
{
    public ImportGroupViewModel(string title, IReadOnlyList<ImportItemViewModel> items)
    {
        Title = title;
        Items = items;
        foreach (var item in items) item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ImportItemViewModel.Include)) OnPropertyChanged(nameof(IncludeAll));
        };
    }

    public string Title { get; }
    public string Header => $"{Title} ({Items.Count})";
    public IReadOnlyList<ImportItemViewModel> Items { get; }

    /// <summary>Ticks or clears every item; null when mixed.</summary>
    public bool? IncludeAll
    {
        get => Items.All(i => i.Include) ? true : Items.Any(i => i.Include) ? null : false;
        set
        {
            var include = value ?? false;
            foreach (var item in Items) item.Include = include;
            OnPropertyChanged();
        }
    }
}

/// <summary>An item node in the import preview tree; its children are read-only detail lines.</summary>
public sealed class ImportItemViewModel(ImportItem item) : ObservableObject
{
    public static IReadOnlyList<string> ModeChoices { get; } = ["Overwrite", "Add as new", "Skip"];

    public ImportItem Item { get; } = item;
    public string Name => Item.Name;
    public string GroupText => Item.Group.Length > 0 ? Item.Group : "";
    public bool HasConflict => Item.HasConflict;
    public bool ContainsCode => Item.ContainsCode;
    public bool HasPlaceholders => Item.PlaceholderCount > 0;
    public bool HasWarnings => Item.Warnings.Count > 0;
    public string WarningText => string.Join("\n", Item.Warnings);
    public IReadOnlyList<ImportDetailViewModel> Details { get; } =
        item.Details.Select(d => new ImportDetailViewModel(d)).Concat(item.Warnings.Select(w => new ImportDetailViewModel("⚠ " + w))).ToList();

    public string ConflictText => Item.Conflict switch
    {
        ConflictKind.SameId => $"Already in your profile as '{Item.ConflictTargetName}'",
        ConflictKind.SameName => $"Name already used by an existing {Item.Kind.ToString().ToLowerInvariant()}",
        _ => "New",
    };

    public bool Include
    {
        get => Item.Include;
        set
        {
            if (Item.Include == value) return;
            Item.Include = value;
            OnPropertyChanged();
        }
    }

    public string Mode
    {
        get => ModeChoices[(int)Item.Mode];
        set
        {
            var index = Math.Max(0, ModeChoices.ToList().IndexOf(value));
            if ((int)Item.Mode == index) return;
            Item.Mode = (ImportMode)index;
            OnPropertyChanged();
        }
    }
}

/// <summary>A read-only line under an item in the preview tree.</summary>
public sealed class ImportDetailViewModel(string text)
{
    public string Text { get; } = text;
    public bool IsWarning => Text.StartsWith('⚠');
}
