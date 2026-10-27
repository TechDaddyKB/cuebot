using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;

namespace Cuebot.App.ViewModels;

public sealed class TimerItemViewModel : ObservableObject
{
    private readonly Action _changed;
    private TimerState? _state;

    public TimerItemViewModel(TimerDefinition definition, Action changed, Action<TimerItemViewModel>? remove = null)
    {
        Definition = definition;
        _changed = changed;
        RemoveCommand = new RelayCommand(() => remove?.Invoke(this));
        ValidateCron();
    }

    public TimerDefinition Definition { get; }

    public IRelayCommand RemoveCommand { get; }

    private void Set<T>(T current, T value, Action<T> assign, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(ScheduleSummary));
        _changed();
    }

    public string Name
    {
        get => Definition.Name;
        set => Set(Definition.Name, value, v => Definition.Name = v);
    }

    public decimal? IntervalSeconds
    {
        get => Definition.IntervalSeconds;
        set => Set(Definition.IntervalSeconds, (int)Math.Max(1, value ?? 1), v => Definition.IntervalSeconds = v);
    }

    public bool Enabled
    {
        get => Definition.Enabled;
        set => Set(Definition.Enabled, value, v => Definition.Enabled = v);
    }

    /// <summary>Off: the timer fires once after it is enabled (or the app starts).</summary>
    public bool Repeat
    {
        get => Definition.Repeat;
        set => Set(Definition.Repeat, value, v => Definition.Repeat = v);
    }

    public bool RandomInterval
    {
        get => Definition.RandomInterval;
        set
        {
            Set(Definition.RandomInterval, value, v => Definition.RandomInterval = v);
            OnPropertyChanged(nameof(ShowsFixedInterval));
            OnPropertyChanged(nameof(ShowsRandomInterval));
            OnPropertyChanged(nameof(IntervalError));
        }
    }

    public decimal? MinIntervalSeconds
    {
        get => Definition.MinIntervalSeconds;
        set
        {
            Set(Definition.MinIntervalSeconds, (int)Math.Max(1, value ?? 1), v => Definition.MinIntervalSeconds = v);
            OnPropertyChanged(nameof(IntervalError));
        }
    }

    public decimal? MaxIntervalSeconds
    {
        get => Definition.MaxIntervalSeconds;
        set
        {
            Set(Definition.MaxIntervalSeconds, (int)Math.Max(1, value ?? 1), v => Definition.MaxIntervalSeconds = v);
            OnPropertyChanged(nameof(IntervalError));
        }
    }

    public string IntervalError => Definition.RandomInterval && Definition.MinIntervalSeconds > Definition.MaxIntervalSeconds
        ? "The minimum is above the maximum."
        : "";

    /// <summary>When above zero, a due timer only fires if this many chat messages arrived since it last fired.</summary>
    public decimal? MinChatLines
    {
        get => Definition.MinChatLines;
        set => Set(Definition.MinChatLines, (int)Math.Max(0, value ?? 0), v => Definition.MinChatLines = v);
    }

    /// <summary>5-field cron schedule; blank uses the interval instead.</summary>
    public string Cron
    {
        get => Definition.Cron ?? "";
        set
        {
            var cron = string.IsNullOrWhiteSpace(value) ? null : value;
            Set(Definition.Cron, cron, v => Definition.Cron = v);
            ValidateCron();
            OnPropertyChanged(nameof(HasCron));
            OnPropertyChanged(nameof(ShowsFixedInterval));
            OnPropertyChanged(nameof(ShowsRandomInterval));
        }
    }

    public bool HasCron => !string.IsNullOrWhiteSpace(Definition.Cron);
    public bool ShowsFixedInterval => !HasCron && !Definition.RandomInterval;
    public bool ShowsRandomInterval => !HasCron && Definition.RandomInterval;

    public string CronError { get; private set; } = "";
    public string CronNext { get; private set; } = "";
    public bool HasCronError => CronError.Length > 0;

    private void ValidateCron()
    {
        CronError = CronNext = "";
        if (!string.IsNullOrWhiteSpace(Definition.Cron))
        {
            if (!CronExpression.TryParse(Definition.Cron.Trim(), out var cron, out var error)) CronError = error;
            else if (cron.Next(DateTimeOffset.Now) is { } next) CronNext = "Next: " + When(next);
            else CronNext = "This schedule never fires.";
        }
        OnPropertyChanged(nameof(CronError));
        OnPropertyChanged(nameof(CronNext));
        OnPropertyChanged(nameof(HasCronError));
    }

    public string ScheduleSummary
    {
        get
        {
            var d = Definition;
            var schedule = HasCron ? $"cron {d.Cron}"
                : d.RandomInterval ? $"every {d.MinIntervalSeconds}-{d.MaxIntervalSeconds} s at random"
                : $"every {d.IntervalSeconds} s";
            if (!d.Repeat) schedule = "once, " + schedule.Replace("every ", "after ");
            if (d.MinChatLines > 0) schedule += $", if {d.MinChatLines}+ chat lines";
            return schedule;
        }
    }

    // ---- live state ----

    /// <summary>"Next in 4:32 (12:30:00) · 3 chat lines", from the timer service.</summary>
    public string LiveStatus
    {
        get
        {
            if (_state is not { } s) return "";
            if (!s.Enabled) return "Off";
            var next = s.NextFire is { } at ? $"Next in {Countdown(at - DateTimeOffset.Now)} ({When(at)})" : "Not scheduled yet";
            return Definition.MinChatLines > 0 ? $"{next} · {s.ChatLines}/{Definition.MinChatLines} chat lines" : $"{next} · {s.ChatLines} chat lines";
        }
    }

    internal void UpdateState(TimerState? state)
    {
        _state = state;
        OnPropertyChanged(nameof(LiveStatus));
    }

    private static string Countdown(TimeSpan left)
    {
        if (left < TimeSpan.Zero) left = TimeSpan.Zero;
        return left.TotalHours >= 1
            ? $"{(int)left.TotalHours}:{left.Minutes:00}:{left.Seconds:00}"
            : $"{left.Minutes}:{left.Seconds:00}";
    }

    private static string When(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
            : local.ToString("ddd yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
    }
}

/// <summary>Timers fire the "Timer" trigger at an interval, a random interval, or on a cron schedule.</summary>
public sealed partial class TimersViewModel : PageViewModel
{
    private readonly ProfileService _profile;
    private readonly TimerService? _service;
    private readonly DispatcherTimer _clock;

    public TimersViewModel(ProfileService profile, TimerService? service = null) : base("Timers")
    {
        _profile = profile;
        _service = service;
        Timers = new ObservableCollection<TimerItemViewModel>(profile.Profile.Timers.Select(Wrap));
        if (service is not null) service.StateChanged += _ => OnUi(RefreshStates);
        // Countdowns tick while the page is shown (the view starts and stops the clock).
        _clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => RefreshStates());
        _clock.Stop();
        RefreshStates();
    }

    public ObservableCollection<TimerItemViewModel> Timers { get; }

    public void StartLiveUpdates()
    {
        RefreshStates();
        _clock.Start();
    }

    public void StopLiveUpdates() => _clock.Stop();

    internal void RefreshStates()
    {
        if (_service is null) return;
        var states = _service.States();
        foreach (var timer in Timers) timer.UpdateState(states.FirstOrDefault(s => s.Id == timer.Definition.Id));
    }

    private TimerItemViewModel Wrap(TimerDefinition t) => new(t, () =>
    {
        _profile.NotifyChanged();
        RefreshStates();
    }, RemoveTimer);

    [RelayCommand]
    private void AddTimer()
    {
        var timer = new TimerDefinition { Name = $"Timer {Timers.Count + 1}" };
        _profile.Profile.Timers.Add(timer);
        _profile.NotifyChanged();
        Timers.Add(Wrap(timer));
        RefreshStates();
    }

    [RelayCommand]
    private void RemoveTimer(TimerItemViewModel timer)
    {
        _profile.Profile.Timers.Remove(timer.Definition);
        _profile.NotifyChanged();
        Timers.Remove(timer);
    }
}
