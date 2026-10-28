using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Twitch;

namespace Cuebot.App.ViewModels.Twitch;

/// <summary>One reward in the Rewards tab, editable when Cuebot created it.</summary>
public sealed partial class TwitchRewardItem : ObservableObject
{
    private readonly TwitchCounters _counters;

    /// <param name="counters">Where the group and counter settings live; without it they are kept only in memory.</param>
    public TwitchRewardItem(TwitchReward? reward = null, TwitchCounters? counters = null)
    {
        _counters = counters ??= new TwitchCounters(() => new TwitchSettings(), () => { });
        if (reward is null)
        {
            IsNew = true;
            IsManageable = true;
            return;
        }
        // Set before Id, so loading them does not save them back.
        Group = counters.GetGroup(reward.Id);
        (PersistCounter, PersistUserCounter) = counters.GetPersistence(reward.Id);
        Id = reward.Id;
        IsManageable = reward.IsManageable;
        Title = reward.Title;
        Prompt = reward.Prompt;
        Cost = reward.Cost;
        BackgroundColor = reward.BackgroundColor;
        IsEnabled = reward.IsEnabled;
        IsPaused = reward.IsPaused;
        IsUserInputRequired = reward.IsUserInputRequired;
        SkipQueue = reward.SkipQueue;
        MaxPerStream = reward.MaxPerStream;
        MaxPerUserPerStream = reward.MaxPerUserPerStream;
        GlobalCooldownSeconds = reward.GlobalCooldownSeconds;
    }

    public string Id { get; private set; } = "";
    public bool IsNew { get; private set; }
    public bool IsManageable { get; }
    public bool IsReadOnly => !IsManageable;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private string _title = "New reward";
    [ObservableProperty] private string _prompt = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private decimal _cost = 100;
    [ObservableProperty] private string _backgroundColor = "#9147FF";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private bool _isEnabled = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private bool _isPaused;
    [ObservableProperty] private bool _isUserInputRequired;
    [ObservableProperty] private bool _skipQueue;
    [ObservableProperty] private decimal _maxPerStream;
    [ObservableProperty] private decimal _maxPerUserPerStream;
    [ObservableProperty] private decimal _globalCooldownSeconds;

    // Cuebot-side settings: saved right away, for every reward (also ones made on the Twitch dashboard).
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private string _group = "";
    [ObservableProperty] private bool _persistCounter;
    [ObservableProperty] private bool _persistUserCounter;

    public string CounterSummary => Id.Length == 0
        ? ""
        : $"Redeemed {_counters.GetRewardCounter(Id).Counter} times (%counter%); per-user counts are in %userCounter%.";

    public string Summary =>
        $"{Cost} points{(IsEnabled ? "" : " · disabled")}{(IsPaused ? " · paused" : "")}{(IsManageable ? "" : " · not editable")}"
        + (Group.Trim().Length > 0 ? $" · {Group.Trim()}" : "");

    partial void OnGroupChanged(string value)
    {
        if (Id.Length > 0) _counters.SetGroup(Id, value);
    }

    partial void OnPersistCounterChanged(bool value) => SavePersistence();

    partial void OnPersistUserCounterChanged(bool value) => SavePersistence();

    private void SavePersistence()
    {
        if (Id.Length > 0) _counters.SetPersistence(Id, PersistCounter, PersistUserCounter);
    }

    [RelayCommand]
    private void ResetCounters()
    {
        if (Id.Length == 0) return;
        _counters.ResetRewardCounter(Id);
        _counters.ResetRewardUserCounters(Id);
        OnPropertyChanged(nameof(CounterSummary));
    }

    public RewardUpdate ToUpdate() => new()
    {
        Title = Title,
        Prompt = Prompt,
        Cost = (int)Cost,
        BackgroundColor = BackgroundColor,
        IsEnabled = IsEnabled,
        IsPaused = IsNew ? null : IsPaused,
        IsUserInputRequired = IsUserInputRequired,
        SkipQueue = SkipQueue,
        MaxPerStream = (int)MaxPerStream,
        MaxPerUserPerStream = (int)MaxPerUserPerStream,
        GlobalCooldownSeconds = (int)GlobalCooldownSeconds,
    };

    /// <summary>A new, unsaved reward with this one's settings (Ctrl+D).</summary>
    public TwitchRewardItem Duplicate() => new(counters: _counters)
    {
        Title = Title + " (copy)",
        Prompt = Prompt,
        Cost = Cost,
        BackgroundColor = BackgroundColor,
        IsEnabled = IsEnabled,
        IsUserInputRequired = IsUserInputRequired,
        SkipQueue = SkipQueue,
        MaxPerStream = MaxPerStream,
        MaxPerUserPerStream = MaxPerUserPerStream,
        GlobalCooldownSeconds = GlobalCooldownSeconds,
        Group = Group,
    };

    internal void Saved(TwitchReward reward)
    {
        var wasNew = IsNew;
        Id = reward.Id;
        IsNew = false;
        if (!wasNew) return;
        if (Group.Trim().Length > 0) _counters.SetGroup(Id, Group);
        SavePersistence();
        OnPropertyChanged(nameof(CounterSummary));
    }
}

/// <summary>Rewards tab: list the channel's rewards; create, edit and delete the ones Cuebot owns.</summary>
public sealed partial class TwitchRewardsViewModel(TwitchService twitch) : TwitchTabViewModel(twitch)
{
    public ObservableCollection<TwitchRewardItem> Rewards { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelection))] private TwitchRewardItem? _selected;

    public bool HasSelection => Selected is not null;

    [RelayCommand]
    private Task Reload() => Run(async () =>
    {
        var rewards = await Api.GetRewardsAsync();
        Rewards.Clear();
        foreach (var reward in rewards.OrderBy(r => r.Cost)) Rewards.Add(new TwitchRewardItem(reward, Api.Counters));
        Info = $"{rewards.Count} rewards; {rewards.Count(r => r.IsManageable)} created by Cuebot can be edited here.";
    });

    [RelayCommand]
    private void New()
    {
        var item = new TwitchRewardItem(counters: Api.Counters);
        Rewards.Add(item);
        Selected = item;
    }

    /// <summary>Ctrl+D: a copy of the selected reward, saved to Twitch with Save.</summary>
    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is not { } source) return;
        var copy = source.Duplicate();
        Rewards.Add(copy);
        Selected = copy;
    }

    /// <summary>Ctrl+O: turns the selected reward on or off, saving it to Twitch when it is one Cuebot created.</summary>
    [RelayCommand]
    private async Task ToggleEnabled()
    {
        if (Selected is not { IsManageable: true } item) return;   // Twitch only lets an app change its own rewards
        item.IsEnabled = !item.IsEnabled;
        if (item.IsManageable && !item.IsNew) await SaveCommand.ExecuteAsync(null);
    }

    [RelayCommand]
    private Task Save() => Run(async () =>
    {
        if (Selected is not { } item) return;
        if (!item.IsManageable) throw new InvalidOperationException("Twitch only lets an app change rewards it created.");
        var saved = item.IsNew ? await Api.CreateRewardAsync(item.ToUpdate()) : await Api.UpdateRewardAsync(item.Id, item.ToUpdate());
        item.Saved(saved);
    }, "Reward saved.");

    [RelayCommand]
    private Task Delete() => Run(async () =>
    {
        if (Selected is not { } item) return;
        if (!item.IsNew) await Api.DeleteRewardAsync(item.Id);
        Rewards.Remove(item);
        Selected = null;
    }, "Reward deleted.");
}
