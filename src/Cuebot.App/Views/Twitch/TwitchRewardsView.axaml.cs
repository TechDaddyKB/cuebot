using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels.Twitch;

namespace Cuebot.App.Views.Twitch;

/// <summary>
/// Twitch Rewards tab. Keys (Streamer.bot's Channel Point Rewards tab): Ctrl+A adds a reward; on the reward list,
/// Delete deletes the selected one, Ctrl+D duplicates it, Ctrl+O turns it on or off.
/// </summary>
public partial class TwitchRewardsView : UserControl
{
    public TwitchRewardsView()
    {
        InitializeComponent();
        PageKeys.Handle(this, e =>
        {
            if (DataContext is not TwitchRewardsViewModel vm) return false;
            if (e.Key == Key.A && PageKeys.Ctrl(e)) return PageKeys.Run(vm.NewCommand);
            var inList = e.Source is Visual source && (source == RewardList || source.FindAncestorOfType<ListBox>() == RewardList);
            if (!inList) return false;
            return e.Key switch
            {
                Key.Delete when PageKeys.Plain(e) => PageKeys.Run(vm.DeleteCommand),
                Key.D when PageKeys.Ctrl(e) => PageKeys.Run(vm.DuplicateCommand),
                Key.O when PageKeys.Ctrl(e) => PageKeys.Run(vm.ToggleEnabledCommand),
                _ => false,
            };
        }, selected: () => (DataContext as TwitchRewardsViewModel)?.Selected);
    }
}
