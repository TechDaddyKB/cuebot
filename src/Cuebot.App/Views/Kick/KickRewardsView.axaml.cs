using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels.Kick;

namespace Cuebot.App.Views.Kick;

/// <summary>The Kick page's rewards editor. On the reward list, Delete deletes the selected reward and Ctrl+D duplicates it.</summary>
public partial class KickRewardsView : UserControl
{
    public KickRewardsView()
    {
        InitializeComponent();
        PageKeys.Handle(this, e =>
        {
            if (DataContext is not KickRewardsEditorViewModel vm) return false;
            var inList = e.Source is Visual source && (source == RewardList || source.FindAncestorOfType<ListBox>() == RewardList);
            if (!inList) return false;
            return e.Key switch
            {
                Key.Delete when PageKeys.Plain(e) => PageKeys.Run(vm.DeleteCommand),
                Key.D when PageKeys.Ctrl(e) => PageKeys.Run(vm.DuplicateCommand),
                _ => false,
            };
        }, selected: () => (DataContext as KickRewardsEditorViewModel)?.Selected);
    }
}
