using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Subathon;

namespace Cuebot.App.ViewModels.Extras;

/// <summary>
/// The value list of one timer (seconds) or goal (points). Every change is saved at once and only counts for events
/// that come in afterwards, so a streamer flooded with subs can turn the values down mid-subathon.
/// </summary>
public sealed partial class EventValuesViewModel : ObservableObject
{
    private readonly Action<Action<EventValues>> _save;
    private bool _loading = true;

    public EventValuesViewModel(EventValues v, string unit, Action<Action<EventValues>> save)
    {
        _save = save;
        Unit = unit;
        Follow = (decimal)v.Follow;
        TwitchTier1 = (decimal)v.TwitchTier1;
        TwitchTier2 = (decimal)v.TwitchTier2;
        TwitchTier3 = (decimal)v.TwitchTier3;
        KickSub = (decimal)v.KickSub;
        YouTubeMembership = (decimal)v.YouTubeMembership;
        OtherSub = (decimal)v.OtherSub;
        CountGiftSubs = v.CountGiftSubs;
        Per100Bits = (decimal)v.Per100Bits;
        Per100Kicks = (decimal)v.Per100Kicks;
        Per100Jewels = (decimal)v.Per100Jewels;
        PerCurrencyUnit = (decimal)v.PerCurrencyUnit;
        RaidEach = (decimal)v.RaidEach;
        RaidPerViewer = (decimal)v.RaidPerViewer;
        Redemptions = string.Join(Environment.NewLine, v.Redemptions.Select(r => $"{r.Key} = {r.Value.ToString("0.###", CultureInfo.InvariantCulture)}"));
        _loading = false;
    }

    /// <summary>"seconds" or "points", for the labels.</summary>
    public string Unit { get; }

    [ObservableProperty] private decimal? _follow;
    [ObservableProperty] private decimal? _twitchTier1;
    [ObservableProperty] private decimal? _twitchTier2;
    [ObservableProperty] private decimal? _twitchTier3;
    [ObservableProperty] private decimal? _kickSub;
    [ObservableProperty] private decimal? _youTubeMembership;
    [ObservableProperty] private decimal? _otherSub;
    [ObservableProperty] private bool _countGiftSubs;
    [ObservableProperty] private decimal? _per100Bits;
    [ObservableProperty] private decimal? _per100Kicks;
    [ObservableProperty] private decimal? _per100Jewels;
    [ObservableProperty] private decimal? _perCurrencyUnit;
    [ObservableProperty] private decimal? _raidEach;
    [ObservableProperty] private decimal? _raidPerViewer;
    [ObservableProperty] private string _redemptions = "";
    [ObservableProperty] private string _redemptionsError = "";

    partial void OnFollowChanged(decimal? value) => Set(v => v.Follow = D(value));
    partial void OnTwitchTier1Changed(decimal? value) => Set(v => v.TwitchTier1 = D(value));
    partial void OnTwitchTier2Changed(decimal? value) => Set(v => v.TwitchTier2 = D(value));
    partial void OnTwitchTier3Changed(decimal? value) => Set(v => v.TwitchTier3 = D(value));
    partial void OnKickSubChanged(decimal? value) => Set(v => v.KickSub = D(value));
    partial void OnYouTubeMembershipChanged(decimal? value) => Set(v => v.YouTubeMembership = D(value));
    partial void OnOtherSubChanged(decimal? value) => Set(v => v.OtherSub = D(value));
    partial void OnCountGiftSubsChanged(bool value) => Set(v => v.CountGiftSubs = value);
    partial void OnPer100BitsChanged(decimal? value) => Set(v => v.Per100Bits = D(value));
    partial void OnPer100KicksChanged(decimal? value) => Set(v => v.Per100Kicks = D(value));
    partial void OnPer100JewelsChanged(decimal? value) => Set(v => v.Per100Jewels = D(value));
    partial void OnPerCurrencyUnitChanged(decimal? value) => Set(v => v.PerCurrencyUnit = D(value));
    partial void OnRaidEachChanged(decimal? value) => Set(v => v.RaidEach = D(value));
    partial void OnRaidPerViewerChanged(decimal? value) => Set(v => v.RaidPerViewer = D(value));

    partial void OnRedemptionsChanged(string value)
    {
        if (_loading) return;
        var parsed = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in value.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = line.LastIndexOf('=');
            if (at <= 0 || !double.TryParse(line[(at + 1)..].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n < 0)
            {
                RedemptionsError = $"Write each reward as Name = number, like Hydrate = 60. Not understood: {line}";
                return;
            }
            parsed[line[..at].Trim()] = n;
        }
        RedemptionsError = "";
        _save(v => v.Redemptions = parsed);
    }

    private static double D(decimal? value) => value is { } v && v > 0 ? (double)v : 0;

    private void Set(Action<EventValues> change)
    {
        if (!_loading) _save(change);
    }
}

/// <summary>One subathon timer: its clock and controls, its settings and its value list.</summary>
public sealed partial class SubathonTimerCardViewModel : ObservableObject
{
    private readonly SubathonService _service;
    private bool _loading = true;

    public SubathonTimerCardViewModel(SubathonService service, SubathonTimer t)
    {
        _service = service;
        Id = t.Id;
        Name = t.Name;
        StartingMinutes = (decimal)Math.Round(t.InitialSeconds / 60, 2);
        MaxHours = (decimal)Math.Round(t.MaxSeconds / 3600, 2);
        Multiplier = (decimal)t.Multiplier;
        AddWhilePaused = t.AddWhilePaused;
        StartOnStreamOnline = t.StartOnStreamOnline;
        PauseOnStreamOffline = t.PauseOnStreamOffline;
        Values = new EventValuesViewModel(t.Values, "seconds", change => service.UpdateTimer(Id, x => change(x.Values)));
        _loading = false;
        Show(t);
    }

    public string Id { get; }
    public EventValuesViewModel Values { get; }
    public ConfirmClick ResetConfirm { get; } = new("Reset", "Click again to reset");
    public ConfirmClick RemoveConfirm { get; } = new("Delete", "Click again to delete");

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private decimal? _startingMinutes;
    [ObservableProperty] private decimal? _maxHours;
    [ObservableProperty] private decimal? _multiplier;
    [ObservableProperty] private bool _addWhilePaused;
    [ObservableProperty] private bool _startOnStreamOnline;
    [ObservableProperty] private bool _pauseOnStreamOffline;
    [ObservableProperty] private decimal? _adjustMinutes = 5;
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] private string _stateText = "";
    [ObservableProperty] private string _detailText = "";
    [ObservableProperty] private bool _isRunning;

    /// <summary>Raised when the timer is deleted, so the page drops the card.</summary>
    public event Action<SubathonTimerCardViewModel>? Removed;

    partial void OnNameChanged(string value) => Save(t => t.Name = value);
    partial void OnStartingMinutesChanged(decimal? value) => Save(t => t.InitialSeconds = (double)(value ?? 1) * 60);
    partial void OnMaxHoursChanged(decimal? value) => Save(t => t.MaxSeconds = (double)(value ?? 0) * 3600);
    partial void OnMultiplierChanged(decimal? value) => Save(t => t.Multiplier = (double)(value ?? 1));
    partial void OnAddWhilePausedChanged(bool value) => Save(t => t.AddWhilePaused = value);
    partial void OnStartOnStreamOnlineChanged(bool value) => Save(t => t.StartOnStreamOnline = value);
    partial void OnPauseOnStreamOfflineChanged(bool value) => Save(t => t.PauseOnStreamOffline = value);

    private void Save(Action<SubathonTimer> change)
    {
        if (!_loading) _service.UpdateTimer(Id, change);
    }

    /// <summary>Shows the timer's clock and state (settings fields are left alone, so typing is never overwritten).</summary>
    internal void Show(SubathonTimer t)
    {
        TimeText = SubathonService.Format(t.RemainingSeconds);
        IsRunning = t.State == TimerState.Running;
        StateText = t.State switch
        {
            TimerState.Running => "Running",
            TimerState.Paused => "Paused",
            TimerState.Ended => "Time is up",
            _ => "Not started",
        };
        DetailText = $"Ran {SubathonService.Format(t.ElapsedSeconds)}, events added {SubathonService.Format(t.AddedSeconds)}"
                     + (t.Multiplier != 1 ? $", multiplier ×{t.Multiplier.ToString("0.##", CultureInfo.InvariantCulture)}" : "");
        _loading = true;
        Multiplier = (decimal)t.Multiplier;   // sub-actions can change it (happy hour)
        _loading = false;
    }

    private double AdjustSeconds => (double)(AdjustMinutes ?? 0) * 60;

    [RelayCommand] private void Start() => _service.Start(Id);
    [RelayCommand] private void Pause() => _service.Pause(Id);
    [RelayCommand] private void AddTime() => _service.AddTime(Id, AdjustSeconds);
    [RelayCommand] private void RemoveTime() => _service.AddTime(Id, -AdjustSeconds);
    [RelayCommand] private void SetTime() => _service.SetTime(Id, AdjustSeconds);

    [RelayCommand]
    private void Reset()
    {
        if (ResetConfirm.Confirm()) _service.Reset(Id);
    }

    [RelayCommand]
    private void Remove()
    {
        if (!RemoveConfirm.Confirm()) return;
        _service.RemoveTimer(Id);
        Removed?.Invoke(this);
    }
}

public sealed partial class GoalStepRowViewModel(GoalLadderCardViewModel owner, string name, decimal? points) : ObservableObject
{
    [ObservableProperty] private string _name = name;
    [ObservableProperty] private decimal? _points = points;
    [ObservableProperty] private bool _reached;

    partial void OnNameChanged(string value) => owner.SaveSteps();
    partial void OnPointsChanged(decimal? value) => owner.SaveSteps();

    [RelayCommand] private void Remove() => owner.RemoveStep(this);
}

/// <summary>One goal ladder: its points and controls, its steps (kept in ascending order) and its value list.</summary>
public sealed partial class GoalLadderCardViewModel : ObservableObject
{
    private readonly SubathonService _service;
    private bool _loading = true;

    public GoalLadderCardViewModel(SubathonService service, GoalLadder g)
    {
        _service = service;
        Id = g.Id;
        Name = g.Name;
        Multiplier = (decimal)g.Multiplier;
        ResetOnNewStream = g.ResetOnNewStream;
        Values = new EventValuesViewModel(g.Values, "points", change => service.UpdateGoal(Id, x => change(x.Values)));
        LoadSteps(g);
        _loading = false;
        Show(g);
    }

    public string Id { get; }
    public EventValuesViewModel Values { get; }
    public ObservableCollection<GoalStepRowViewModel> Steps { get; } = [];
    public ConfirmClick ResetConfirm { get; } = new("Reset to 0", "Click again to reset");
    public ConfirmClick RemoveConfirm { get; } = new("Delete", "Click again to delete");

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private decimal? _multiplier;
    [ObservableProperty] private bool _resetOnNewStream;
    [ObservableProperty] private decimal? _adjustPoints = 1;
    [ObservableProperty] private string _pointsText = "";
    [ObservableProperty] private string _nextText = "";
    [ObservableProperty] private double _progress;

    public event Action<GoalLadderCardViewModel>? Removed;

    partial void OnNameChanged(string value) => Save(g => g.Name = value);
    partial void OnMultiplierChanged(decimal? value) => Save(g => g.Multiplier = (double)(value ?? 1));
    partial void OnResetOnNewStreamChanged(bool value) => Save(g => g.ResetOnNewStream = value);

    private void Save(Action<GoalLadder> change)
    {
        if (!_loading) _service.UpdateGoal(Id, change);
    }

    internal void Show(GoalLadder g)
    {
        PointsText = Num(g.Points) + " points";
        var next = g.Steps.FirstOrDefault(s => s.Points > g.Points);
        var previous = g.Steps.LastOrDefault(s => s.Points <= g.Points)?.Points ?? 0;
        NextText = next is not null ? $"Next: {next.Name} at {Num(next.Points)} ({Num(next.Points - g.Points)} to go)"
            : g.Steps.Count > 0 ? "Every step reached" : "Add steps below";
        Progress = next is null ? (g.Steps.Count > 0 ? 100 : 0) : Math.Clamp((g.Points - previous) / Math.Max(0.0001, next.Points - previous) * 100, 0, 100);
        foreach (var row in Steps)
            row.Reached = row.Points is { } p && ((double)p <= g.Points || g.ReachedThrough is { } r && (double)p <= r);
        _loading = true;
        Multiplier = (decimal)g.Multiplier;
        _loading = false;
    }

    private void LoadSteps(GoalLadder g)
    {
        Steps.Clear();
        foreach (var s in g.Steps) Steps.Add(new GoalStepRowViewModel(this, s.Name, (decimal)s.Points));
    }

    /// <summary>Saves the steps as typed; the service keeps them in ascending order (the list is re-sorted on refresh).</summary>
    internal void SaveSteps()
    {
        if (_loading) return;
        _service.UpdateGoal(Id, g => g.Steps = Steps.Select(r => new GoalStep { Name = r.Name, Points = (double)(r.Points ?? 0) }).ToList());
    }

    /// <summary>Puts the rows in the stored (ascending) order, unless a row is still being filled in.</summary>
    internal void SortSteps(GoalLadder g)
    {
        if (Steps.Any(r => r.Name.Trim().Length == 0)) return;
        var current = Steps.Select(r => (r.Name.Trim(), (double)(r.Points ?? 0))).ToList();
        var stored = g.Steps.Select(s => (s.Name, s.Points)).ToList();
        if (current.SequenceEqual(stored)) return;
        _loading = true;
        LoadSteps(g);
        _loading = false;
        Show(g);
    }

    internal void RemoveStep(GoalStepRowViewModel row)
    {
        Steps.Remove(row);
        SaveSteps();
    }

    private double AdjustValue => (double)(AdjustPoints ?? 0);

    [RelayCommand]
    private void AddStep()
    {
        var top = Steps.Select(r => r.Points ?? 0).DefaultIfEmpty(0).Max();
        _loading = true;
        Steps.Add(new GoalStepRowViewModel(this, "", top + 10));
        _loading = false;
    }

    [RelayCommand] private void AddPoints() => _service.AddPoints(Id, AdjustValue);
    [RelayCommand] private void RemovePoints() => _service.AddPoints(Id, -AdjustValue);
    [RelayCommand] private void SetPoints() => _service.SetPoints(Id, AdjustValue);

    [RelayCommand]
    private void Reset()
    {
        if (ResetConfirm.Confirm()) _service.ResetGoal(Id);
    }

    [RelayCommand]
    private void Remove()
    {
        if (!RemoveConfirm.Confirm()) return;
        _service.RemoveGoal(Id);
        Removed?.Invoke(this);
    }

    private static string Num(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);
}

/// <summary>Subathon timers and goal ladders: one card each, a clock that ticks every second.</summary>
public sealed partial class SubathonGoalsViewModel : PageViewModel
{
    public const string PageTitle = "Subathon & Goals";
    private readonly SubathonService _service;
    private readonly DispatcherTimer _clock;

    public SubathonGoalsViewModel(SubathonService service) : base(PageTitle)
    {
        _service = service;
        service.Changed += () => OnUi(Refresh);
        Refresh();
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Tick());
        _clock.Start();
    }

    public ObservableCollection<SubathonTimerCardViewModel> Timers { get; } = [];
    public ObservableCollection<GoalLadderCardViewModel> Goals { get; } = [];

    [ObservableProperty] private int _selectedTabIndex;

    public bool HasNoTimers => Timers.Count == 0;
    public bool HasNoGoals => Goals.Count == 0;

    internal void Refresh()
    {
        var timers = _service.Timers();
        Timers.Where(c => timers.All(t => t.Id != c.Id)).ToList().ForEach(c => Timers.Remove(c));
        foreach (var t in timers)
        {
            if (Timers.FirstOrDefault(c => c.Id == t.Id) is { } card) card.Show(t);
            else
            {
                var added = new SubathonTimerCardViewModel(_service, t);
                added.Removed += c => Timers.Remove(c);
                Timers.Add(added);
            }
        }
        var goals = _service.Goals();
        Goals.Where(c => goals.All(g => g.Id != c.Id)).ToList().ForEach(c => Goals.Remove(c));
        foreach (var g in goals)
        {
            if (Goals.FirstOrDefault(c => c.Id == g.Id) is { } card)
            {
                card.Show(g);
                card.SortSteps(g);
            }
            else
            {
                var added = new GoalLadderCardViewModel(_service, g);
                added.Removed += c => Goals.Remove(c);
                Goals.Add(added);
            }
        }
        OnPropertyChanged(nameof(HasNoTimers));
        OnPropertyChanged(nameof(HasNoGoals));
    }

    /// <summary>Moves the clocks of running timers on (the service is not changed, so nothing is saved).</summary>
    internal void Tick()
    {
        if (!Timers.Any(c => c.IsRunning)) return;
        foreach (var t in _service.Timers())
            if (Timers.FirstOrDefault(c => c.Id == t.Id) is { } card) card.Show(t);
    }

    [RelayCommand]
    private void AddTimer()
    {
        _service.AddTimer();
        SelectedTabIndex = 0;
    }

    [RelayCommand]
    private void AddGoal()
    {
        _service.AddGoal();
        SelectedTabIndex = 1;
    }
}
