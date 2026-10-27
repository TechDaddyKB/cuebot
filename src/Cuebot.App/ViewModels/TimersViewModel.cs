using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Actions;

namespace Cuebot.App.ViewModels;

public sealed class TimerItemViewModel(TimerDefinition definition, Action changed) : ObservableObject
{
    public TimerDefinition Definition { get; } = definition;

    public string Name
    {
        get => Definition.Name;
        set { Definition.Name = value; OnPropertyChanged(); changed(); }
    }

    public decimal IntervalSeconds
    {
        get => Definition.IntervalSeconds;
        set { Definition.IntervalSeconds = (int)Math.Max(1, value); OnPropertyChanged(); changed(); }
    }

    public bool Enabled
    {
        get => Definition.Enabled;
        set { Definition.Enabled = value; OnPropertyChanged(); changed(); }
    }
}

/// <summary>Timers fire the "Timer" trigger at a fixed interval.</summary>
public sealed partial class TimersViewModel : PageViewModel
{
    private readonly ProfileService _profile;

    public TimersViewModel(ProfileService profile) : base("Timers")
    {
        _profile = profile;
        Timers = new ObservableCollection<TimerItemViewModel>(profile.Profile.Timers.Select(Wrap));
    }

    public ObservableCollection<TimerItemViewModel> Timers { get; }

    private TimerItemViewModel Wrap(TimerDefinition t) => new(t, _profile.NotifyChanged);

    [RelayCommand]
    private void AddTimer()
    {
        var timer = new TimerDefinition { Name = $"Timer {Timers.Count + 1}" };
        _profile.Profile.Timers.Add(timer);
        _profile.NotifyChanged();
        Timers.Add(Wrap(timer));
    }

    [RelayCommand]
    private void RemoveTimer(TimerItemViewModel timer)
    {
        _profile.Profile.Timers.Remove(timer.Definition);
        _profile.NotifyChanged();
        Timers.Remove(timer);
    }
}
