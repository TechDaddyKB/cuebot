using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Kick;
using Cuebot.Kick.Api;

namespace Cuebot.App.ViewModels.Kick;

/// <summary>One reward in the Kick rewards editor.</summary>
public sealed partial class KickRewardItem : ObservableObject
{
    private readonly KickRewardCounters? _counters;

    public KickRewardItem(KickReward? reward = null, KickRewardCounters? counters = null, bool madeByCuebot = false)
    {
        _counters = counters;
        if (reward is null)
        {
            IsNew = true;
            MadeByCuebot = true;
            return;
        }
        // Set before Id, so loading them does not save them back.
        if (counters is not null)
        {
            Group = counters.GetGroup(reward.Id);
            (PersistCounter, PersistUserCounter) = counters.GetPersistence(reward.Id);
        }
        Id = reward.Id;
        MadeByCuebot = madeByCuebot;
        Title = reward.Title;
        Description = reward.Description;
        Cost = reward.Cost;
        BackgroundColor = reward.BackgroundColor;
        IsEnabled = reward.IsEnabled;
        IsPaused = reward.IsPaused;
        IsUserInputRequired = reward.IsUserInputRequired;
        SkipQueue = reward.SkipsRequestQueue;
    }

    public string Id { get; private set; } = "";
    public bool IsNew { get; private set; }

    /// <summary>Cuebot created it, so Kick lets Cuebot change it. Other rewards may be refused by Kick.</summary>
    public bool MadeByCuebot { get; private set; }

    public bool MaybeReadOnly => !MadeByCuebot;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private string _title = "New reward";
    [ObservableProperty] private string _description = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private decimal _cost = 100;
    [ObservableProperty] private string _backgroundColor = "#00E701";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private bool _isEnabled = true;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private bool _isPaused;
    [ObservableProperty] private bool _isUserInputRequired;
    [ObservableProperty] private bool _skipQueue;

    // Cuebot-side settings: saved right away, for every reward (also ones made on kick.com).
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(Summary))] private string _group = "";
    [ObservableProperty] private bool _persistCounter;
    [ObservableProperty] private bool _persistUserCounter;

    public string Summary =>
        $"{Cost} points{(IsEnabled ? "" : " · disabled")}{(IsPaused ? " · paused" : "")}{(IsNew ? " · not saved yet" : MadeByCuebot ? "" : " · made outside Cuebot")}"
        + (Group.Trim().Length > 0 ? $" · {Group.Trim()}" : "");

    public string CounterSummary => Id.Length == 0 || _counters is null
        ? ""
        : $"Redeemed {_counters.GetRewardCounter(Id).Counter} times (%counter%); per-user counts are in %userCounter%.";

    partial void OnGroupChanged(string value)
    {
        if (Id.Length > 0) _counters?.SetGroup(Id, value);
    }

    partial void OnPersistCounterChanged(bool value) => SavePersistence();

    partial void OnPersistUserCounterChanged(bool value) => SavePersistence();

    private void SavePersistence()
    {
        if (Id.Length > 0) _counters?.SetPersistence(Id, PersistCounter, PersistUserCounter);
    }

    [RelayCommand]
    private void ResetCounters()
    {
        if (Id.Length == 0 || _counters is null) return;
        _counters.ResetRewardCounter(Id);
        _counters.ResetRewardUserCounters(Id);
        OnPropertyChanged(nameof(CounterSummary));
    }

    public KickRewardUpdate ToUpdate() => new()
    {
        Title = Title.Trim(),
        Description = Description,
        Cost = (int)Math.Max(1, Cost),
        BackgroundColor = BackgroundColor.Trim().Length > 0 ? BackgroundColor.Trim() : null,
        IsEnabled = IsEnabled,
        IsPaused = IsNew ? null : IsPaused,
        IsUserInputRequired = IsUserInputRequired,
        SkipsRequestQueue = SkipQueue,
    };

    /// <summary>A new, unsaved reward with this one's settings (Ctrl+D).</summary>
    public KickRewardItem Duplicate() => new(counters: _counters)
    {
        Title = (Title + " (copy)")[..Math.Min(KickChannelRewards.MaxTitleLength, Title.Length + 7)],
        Description = Description,
        Cost = Cost,
        BackgroundColor = BackgroundColor,
        IsEnabled = IsEnabled,
        IsUserInputRequired = IsUserInputRequired,
        SkipQueue = SkipQueue,
        Group = Group,
    };

    internal void Saved(KickReward reward)
    {
        var wasNew = IsNew;
        Id = reward.Id;
        IsNew = false;
        if (wasNew)
        {
            MadeByCuebot = true;
            if (Group.Trim().Length > 0) _counters?.SetGroup(Id, Group);
            SavePersistence();
        }
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(MaybeReadOnly));
        OnPropertyChanged(nameof(CounterSummary));
    }
}

/// <summary>The Kick page's rewards editor: list the channel's rewards; create, change and delete them.</summary>
public sealed partial class KickRewardsEditorViewModel(KickService kick) : ObservableObject
{
    public ObservableCollection<KickRewardItem> Rewards { get; } = [];

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasSelection))] private KickRewardItem? _selected;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _info = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UsedText), nameof(CanAdd))] private int _savedCount;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UsedText))] private bool _loaded;

    public bool HasSelection => Selected is not null;

    public bool IsSignedIn => kick.IsBroadcasterSignedIn;

    /// <summary>"12 of 50 rewards used": Kick allows 50 per channel, enabled and disabled.</summary>
    public string UsedText => Loaded ? $"{SavedCount} of {KickChannelRewards.MaxRewards} rewards used" : $"Kick allows up to {KickChannelRewards.MaxRewards} rewards";

    public bool CanAdd => SavedCount + Rewards.Count(r => r.IsNew) < KickChannelRewards.MaxRewards;

    [RelayCommand]
    private Task Reload() => Run(async () =>
    {
        var rewards = await kick.GetRewardsAsync();
        Rewards.Clear();
        foreach (var reward in rewards.OrderBy(r => r.Cost)) Rewards.Add(new KickRewardItem(reward, kick.Counters, kick.MadeByCuebot(reward.Id)));
        Loaded = true;
        Count();
        Info = rewards.Count == 0 ? "This channel has no rewards yet." : "";
    });

    [RelayCommand]
    private void New()
    {
        if (!CanAdd)
        {
            Error = $"Kick allows {KickChannelRewards.MaxRewards} rewards per channel. Delete one first.";
            return;
        }
        var item = new KickRewardItem(counters: kick.Counters);
        Rewards.Add(item);
        Selected = item;
        OnPropertyChanged(nameof(CanAdd));
    }

    /// <summary>Ctrl+D: a copy of the selected reward, created on Kick with Save.</summary>
    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is not { } source || !CanAdd) return;
        var copy = source.Duplicate();
        Rewards.Add(copy);
        Selected = copy;
        OnPropertyChanged(nameof(CanAdd));
    }

    [RelayCommand]
    private Task Save() => Run(async () =>
    {
        if (Selected is not { } item) return;
        if (item.Title.Trim().Length == 0) throw new InvalidOperationException("Give the reward a title.");
        var saved = item.IsNew ? await kick.CreateRewardAsync(item.ToUpdate()) : await kick.UpdateRewardAsync(item.Id, item.ToUpdate());
        item.Saved(saved);
        Count();
        Info = "Reward saved.";
    });

    [RelayCommand]
    private Task Delete() => Run(async () =>
    {
        if (Selected is not { } item) return;
        if (!item.IsNew) await kick.DeleteRewardAsync(item.Id);
        Rewards.Remove(item);
        Selected = null;
        Count();
        Info = "Reward deleted.";
    });

    private void Count()
    {
        SavedCount = Rewards.Count(r => !r.IsNew);
        OnPropertyChanged(nameof(CanAdd));
    }

    private async Task Run(Func<Task> operation)
    {
        Error = "";
        Info = "";
        try { await operation(); }
        catch (KickApiException ex) when (ex.Status == 403)
        {
            Error = "Kick refused: it only lets the app that made a reward change or delete it. Rewards made on kick.com can only be changed there.";
        }
        catch (Exception ex) { Error = ex.Message; }
        OnPropertyChanged(nameof(IsSignedIn));
    }
}
