using Avalonia.Controls;
using Avalonia.Interactivity;
using Cuebot.App.ViewModels.Extras;

namespace Cuebot.App.Views.Extras;

public partial class DecksView : UserControl
{
    public DecksView() => InitializeComponent();

    private void OnOpenDeck(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: DeckEditorViewModel deck }) Cuebot.App.Shell.UrlLauncher.Open(deck.Url);
    }
}
