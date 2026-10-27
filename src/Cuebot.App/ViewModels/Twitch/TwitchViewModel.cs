using CommunityToolkit.Mvvm.ComponentModel;
using Cuebot.App.ViewModels.Twitch;
using Cuebot.Twitch;

namespace Cuebot.App.ViewModels;

/// <summary>
/// Twitch page (Streamer.bot parity): one tab per area. Service status changes arrive on background threads and
/// are marshaled to the UI thread.
/// </summary>
public sealed partial class TwitchViewModel : PageViewModel
{
    public TwitchViewModel(TwitchService twitch) : base("Twitch")
    {
        Accounts = new TwitchAccountsViewModel(twitch);
        Channel = new TwitchChannelViewModel(twitch);
        Rewards = new TwitchRewardsViewModel(twitch);
        Polls = new TwitchPollsViewModel(twitch);
        Moderation = new TwitchModerationViewModel(twitch);
        Settings = new TwitchSettingsViewModel(twitch);
        Tabs = [Accounts, Channel, Rewards, Polls, Moderation, Settings];
        twitch.StatusChanged += () => OnUi(Refresh);
    }

    public TwitchAccountsViewModel Accounts { get; }
    public TwitchChannelViewModel Channel { get; }
    public TwitchRewardsViewModel Rewards { get; }
    public TwitchPollsViewModel Polls { get; }
    public TwitchModerationViewModel Moderation { get; }
    public TwitchSettingsViewModel Settings { get; }
    internal IReadOnlyList<TwitchTabViewModel> Tabs { get; }

    [ObservableProperty] private int _selectedTab;

    public string StatusText => Accounts.IsAuthorized ? $"{Accounts.State}: {Accounts.StatusMessage}" : "Not signed in";

    private void Refresh()
    {
        foreach (var tab in Tabs) tab.Refresh();
        OnPropertyChanged(nameof(StatusText));
    }

    public override string ToString() => Title;
}
