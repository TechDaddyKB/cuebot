using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.App.ViewModels.History;

/// <summary>One event seen on the event bus, with the trigger types that listen for it.</summary>
public sealed class TriggerEventRow(BotEvent e, IReadOnlyList<ITriggerType> triggers)
{
    public BotEvent Event { get; } = e;
    public string Type => Event.Type;
    public string Time => Event.Time.ToLocalTime().ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
    public IReadOnlyList<ITriggerType> TriggerTypes { get; } = triggers;

    /// <summary>"Twitch › Chat message" (first listening trigger), or the raw type when no trigger listens.</summary>
    public string Title => TriggerTypes.Count == 0 ? Event.Type : $"{TriggerTypes[0].Category} › {TriggerTypes[0].DisplayName}";

    public string Preview
    {
        get
        {
            var text = string.Join(" · ", Event.Args.Take(4).Select(p => $"{p.Key}={VariableResolver.Format(p.Value)}"));
            text = text.ReplaceLineEndings(" ");
            return text.Length > 120 ? text[..117] + "..." : text;
        }
    }
}

/// <summary>One trigger type offered by "Create action from this trigger".</summary>
public sealed record TriggerTypeOption(string Display, ITriggerType Type);

/// <summary>
/// Trigger Viewer: a live list of events published on the event bus (newest first, bounded), with their arguments,
/// search, pause and clear, and "Create action from this trigger".
/// </summary>
public sealed partial class TriggerViewerViewModel : ObservableObject
{
    public const int Capacity = 500;

    private readonly Registry _registry;
    private readonly LinkedList<TriggerEventRow> _all = new();
    private readonly Dictionary<string, IReadOnlyList<ITriggerType>> _byEventType = new(StringComparer.Ordinal);

    public TriggerViewerViewModel(EventBus bus, Registry registry)
    {
        _registry = registry;
        bus.Subscribe(e => Ui(() => Add(e)));
    }

    public ObservableCollection<TriggerEventRow> Events { get; } = [];
    public ObservableCollection<NameValueRow> Args { get; } = [];
    public ObservableCollection<TriggerTypeOption> TriggerOptions { get; } = [];

    /// <summary>Raised by "Create action from this trigger"; the main window creates the action and opens it.</summary>
    public event Action<ITriggerType>? CreateActionRequested;

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private int _totalSeen;
    [ObservableProperty] private int _ignoredWhilePaused;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(HasTriggerOptions), nameof(SelectedSummary), nameof(SelectedTitle))]
    private TriggerEventRow? _selectedEvent;

    [ObservableProperty] private TriggerTypeOption? _selectedTrigger;

    public bool HasSelection => SelectedEvent is not null;
    public string SelectedTitle => SelectedEvent?.Title ?? "";
    public bool HasTriggerOptions => TriggerOptions.Count > 0;

    public string SelectedSummary => SelectedEvent is not { } e
        ? ""
        : e.TriggerTypes.Count == 0
            ? $"{e.Type} at {e.Time} · no trigger listens for this event"
            : $"{e.Type} at {e.Time} · {e.TriggerTypes.Count} trigger type(s) listen for it";

    public string Summary => IsPaused
        ? $"Paused · {IgnoredWhilePaused} event(s) ignored"
        : TotalSeen == 0
            ? "Waiting for events: chat messages, follows, OBS scene changes, timers and everything else that can trigger an action."
            : $"{Events.Count} shown · {TotalSeen} seen (the last {Capacity} are kept)";

    partial void OnSearchChanged(string value) => ApplyFilter();
    partial void OnIsPausedChanged(bool value) => OnPropertyChanged(nameof(Summary));
    partial void OnIgnoredWhilePausedChanged(int value) => OnPropertyChanged(nameof(Summary));
    partial void OnTotalSeenChanged(int value) => OnPropertyChanged(nameof(Summary));

    partial void OnSelectedEventChanged(TriggerEventRow? value)
    {
        Args.Clear();
        TriggerOptions.Clear();
        if (value is not null)
        {
            foreach (var p in value.Event.Args.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
                Args.Add(new NameValueRow(p.Key, VariableResolver.Format(p.Value)));
            foreach (var t in value.TriggerTypes) TriggerOptions.Add(new TriggerTypeOption($"{t.Category} › {t.DisplayName}", t));
        }
        SelectedTrigger = TriggerOptions.FirstOrDefault();
        OnPropertyChanged(nameof(HasTriggerOptions));
    }

    private static void Ui(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
        else Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }

    /// <summary>Records an event (UI thread).</summary>
    internal void Add(BotEvent e)
    {
        if (IsPaused)
        {
            IgnoredWhilePaused++;
            return;
        }
        var row = new TriggerEventRow(e, TriggersFor(e.Type));
        _all.AddFirst(row);
        TotalSeen++;
        if (Matches(row)) Events.Insert(0, row);
        while (_all.Count > Capacity)
        {
            var oldest = _all.Last!.Value;
            _all.RemoveLast();
            Events.Remove(oldest);
        }
        OnPropertyChanged(nameof(Summary));
    }

    private IReadOnlyList<ITriggerType> TriggersFor(string eventType)
    {
        if (!_byEventType.TryGetValue(eventType, out var list))
            _byEventType[eventType] = list = _registry.Triggers.Where(t => t.EventTypes.Contains(eventType)).ToArray();
        return list;
    }

    private bool Matches(TriggerEventRow row)
    {
        var q = Search.Trim();
        if (q.Length == 0) return true;
        return row.Type.Contains(q, StringComparison.OrdinalIgnoreCase)
               || row.Title.Contains(q, StringComparison.OrdinalIgnoreCase)
               || row.Event.Args.Any(p => p.Key.Contains(q, StringComparison.OrdinalIgnoreCase)
                                          || VariableResolver.Format(p.Value).Contains(q, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyFilter()
    {
        var selected = SelectedEvent;
        Events.SyncTo(_all.Where(Matches).ToArray());
        SelectedEvent = selected is not null && Events.Contains(selected) ? selected : null;
        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void TogglePause()
    {
        IsPaused = !IsPaused;
        if (!IsPaused) IgnoredWhilePaused = 0;
    }

    [RelayCommand]
    private void Clear()
    {
        _all.Clear();
        Events.Clear();
        SelectedEvent = null;
        TotalSeen = 0;
        IgnoredWhilePaused = 0;
        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void CreateAction()
    {
        if (SelectedTrigger is { } option) CreateActionRequested?.Invoke(option.Type);
    }
}
