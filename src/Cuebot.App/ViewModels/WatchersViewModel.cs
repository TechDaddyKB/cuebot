using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.SystemTools;
using Cuebot.Core.Storage;

namespace Cuebot.App.ViewModels;

/// <summary>
/// Editor for one watcher. Check boxes save at once; text fields save after a short pause in typing, so a watcher
/// is not restarted on every keystroke.
/// </summary>
public abstract partial class WatcherItemViewModel : ObservableObject
{
    private readonly WatcherService _service;
    private readonly DispatcherTimer _debounce;

    protected WatcherItemViewModel(WatcherService service, WatcherDefinition definition)
    {
        _service = service;
        Definition = definition;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Background, (_, _) => Flush());
        _debounce.Stop();
        RefreshStatus();
    }

    public WatcherDefinition Definition { get; private set; }
    public Guid Id => Definition.Id;

    /// <summary>A text edit is waiting to be saved.</summary>
    public bool HasPendingEdit { get; private set; }

    public string Name
    {
        get => Definition.Name;
        set => Edit(() => Definition.Name = value, debounce: true);
    }

    public bool Enabled
    {
        get => Definition.Enabled;
        set
        {
            if (Definition.Enabled != value) Edit(() => Definition.Enabled = value, debounce: false);
        }
    }

    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private bool _isError;

    protected void Edit(Action change, bool debounce, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        change();
        OnPropertyChanged(property);
        if (debounce)
        {
            HasPendingEdit = true;
            _debounce.Stop();
            _debounce.Start();
        }
        else
        {
            Flush();
        }
    }

    /// <summary>Saves pending edits now (and restarts the watcher).</summary>
    public void Flush()
    {
        _debounce.Stop();
        HasPendingEdit = false;
        _service.Save(Definition);
    }

    /// <summary>Reads the live status, and the stored definition when there is no edit in progress (e.g. "Set watcher state").</summary>
    internal void Refresh(WatcherDefinition stored)
    {
        if (!HasPendingEdit && stored.Enabled != Definition.Enabled)
        {
            Definition = stored;
            OnPropertyChanged(string.Empty);
        }
        RefreshStatus();
    }

    private void RefreshStatus()
    {
        var status = _service.GetStatus(Id);
        IsRunning = status.State == WatcherState.Running;
        IsError = status.State == WatcherState.Error;
        StatusText = status.State switch
        {
            WatcherState.Running => "Running",
            WatcherState.Waiting => "Waiting" + (status.Message is { Length: > 0 } m ? $": {m}" : ""),
            WatcherState.Error => "Error" + (status.Message is { Length: > 0 } e ? $": {e}" : ""),
            _ => Definition.Enabled ? "Stopped" : "Off",
        };
    }

    public void StopDebounce() => _debounce.Stop();

    internal Action<WatcherItemViewModel>? DuplicateRequested { get; set; }
    internal Action<WatcherItemViewModel>? RemoveRequested { get; set; }

    [RelayCommand]
    private void Duplicate() => DuplicateRequested?.Invoke(this);

    [RelayCommand]
    private void Remove() => RemoveRequested?.Invoke(this);
}

public sealed class FileWatcherItemViewModel(WatcherService service, FileWatcherDefinition definition)
    : WatcherItemViewModel(service, definition)
{
    private FileWatcherDefinition D => (FileWatcherDefinition)Definition;

    public string Folder { get => D.Folder; set => Edit(() => D.Folder = value, debounce: true); }
    public string Filter { get => D.Filter; set => Edit(() => D.Filter = value, debounce: true); }
    public bool IncludeSubfolders { get => D.IncludeSubfolders; set => Edit(() => D.IncludeSubfolders = value, debounce: false); }
    public bool Created { get => D.Created; set => Edit(() => D.Created = value, debounce: false); }
    public bool Changed { get => D.Changed; set => Edit(() => D.Changed = value, debounce: false); }
    public bool Deleted { get => D.Deleted; set => Edit(() => D.Deleted = value, debounce: false); }
    public bool Renamed { get => D.Renamed; set => Edit(() => D.Renamed = value, debounce: false); }
}

public sealed class FileTailItemViewModel(WatcherService service, FileTailDefinition definition)
    : WatcherItemViewModel(service, definition)
{
    private FileTailDefinition D => (FileTailDefinition)Definition;

    public string FilePath { get => D.FilePath; set => Edit(() => D.FilePath = value, debounce: true); }
    public bool SkipEmptyLines { get => D.SkipEmptyLines; set => Edit(() => D.SkipEmptyLines = value, debounce: false); }
}

public sealed class ProcessWatcherItemViewModel(WatcherService service, ProcessWatcherDefinition definition)
    : WatcherItemViewModel(service, definition)
{
    private ProcessWatcherDefinition D => (ProcessWatcherDefinition)Definition;

    public string ProcessName { get => D.ProcessName; set => Edit(() => D.ProcessName = value, debounce: true); }
}

/// <summary>Watchers page: folder watchers, file tails and process watchers from watchers.json, with live status.</summary>
public sealed partial class WatchersViewModel : PageViewModel
{
    private readonly WatcherService _service;

    public WatchersViewModel(WatcherService service) : base("Watchers")
    {
        _service = service;
        service.Changed += () => OnUi(Refresh);
        Refresh();
    }

    public ObservableCollection<FileWatcherItemViewModel> FileWatchers { get; } = [];
    public ObservableCollection<FileTailItemViewModel> FileTails { get; } = [];
    public ObservableCollection<ProcessWatcherItemViewModel> ProcessWatchers { get; } = [];

    [ObservableProperty] private int _selectedTabIndex;

    internal void Refresh()
    {
        Sync(FileWatchers, _service.FileWatchers, d => new FileWatcherItemViewModel(_service, d));
        Sync(FileTails, _service.FileTails, d => new FileTailItemViewModel(_service, d));
        Sync(ProcessWatchers, _service.ProcessWatchers, d => new ProcessWatcherItemViewModel(_service, d));
    }

    private void Sync<TItem, TDef>(ObservableCollection<TItem> target, IReadOnlyList<TDef> stored, Func<TDef, TItem> create)
        where TItem : WatcherItemViewModel where TDef : WatcherDefinition
    {
        var rows = new List<TItem>();
        foreach (var definition in stored)
        {
            var row = target.FirstOrDefault(r => r.Id == definition.Id);
            if (row is null)
            {
                row = create(definition);
                row.DuplicateRequested = item => DuplicateCommand.Execute(item);
                row.RemoveRequested = item => RemoveCommand.Execute(item);
            }
            else row.Refresh(definition);
            rows.Add(row);
        }
        foreach (var gone in target.Where(r => !rows.Contains(r))) gone.StopDebounce();
        target.SyncTo(rows);
    }

    private static string NextName(IEnumerable<WatcherItemViewModel> existing, string prefix)
    {
        var name = prefix;
        for (var i = 2; existing.Any(e => string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)); i++) name = $"{prefix} {i}";
        return name;
    }

    [RelayCommand]
    private void AddFileWatcher()
    {
        // Disabled until it has a folder, so it does not start with an error.
        _service.Save(new FileWatcherDefinition { Name = NextName(FileWatchers, "Folder watcher"), Enabled = false });
        SelectedTabIndex = 0;
    }

    [RelayCommand]
    private void AddFileTail()
    {
        _service.Save(new FileTailDefinition { Name = NextName(FileTails, "File tail"), Enabled = false });
        SelectedTabIndex = 1;
    }

    [RelayCommand]
    private void AddProcessWatcher()
    {
        _service.Save(new ProcessWatcherDefinition { Name = NextName(ProcessWatchers, "Process watcher"), Enabled = false });
        SelectedTabIndex = 2;
    }

    [RelayCommand]
    private void Duplicate(WatcherItemViewModel item)
    {
        item.Flush();
        var copy = item.Definition switch
        {
            FileWatcherDefinition f => (WatcherDefinition)Json.Clone(f),
            FileTailDefinition t => Json.Clone(t),
            ProcessWatcherDefinition p => Json.Clone(p),
            _ => null,
        };
        if (copy is null) return;
        copy.Id = Guid.NewGuid();
        copy.Name += " (copy)";
        _service.Save(copy);
    }

    [RelayCommand]
    private void Remove(WatcherItemViewModel item)
    {
        item.StopDebounce();
        _service.Remove(item.Id);
    }
}
