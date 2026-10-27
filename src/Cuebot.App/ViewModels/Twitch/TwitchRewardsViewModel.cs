using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Twitch;

namespace Cuebot.App.ViewModels.Twitch;

/// <summary>One reward in the Rewards tab, editable when Cuebot created it.</summary>
public sealed partial class TwitchRewardItem : ObservableObject
{
    public TwitchRewardItem(TwitchReward? reward = null)
    {
        if (reward is null)
        {
            IsNew = true;
            IsManageable = true;
            return;
        }
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

    public string Summary =>
        $"{Cost} points{(IsEnabled ? "" : " · disabled")}{(IsPaused ? " · paused" : "")}{(IsManageable ? "" : " · not editable")}";

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

    internal void Saved(TwitchReward reward)
    {
        Id = reward.Id;
        IsNew = false;
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
        foreach (var reward in rewards.OrderBy(r => r.Cost)) Rewards.Add(new TwitchRewardItem(reward));
        Info = $"{rewards.Count} rewards; {rewards.Count(r => r.IsManageable)} created by Cuebot can be edited here.";
    });

    [RelayCommand]
    private void New()
    {
        var item = new TwitchRewardItem();
        Rewards.Add(item);
        Selected = item;
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
