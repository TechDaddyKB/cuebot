using Avalonia.Controls;
using Avalonia.Input;
using Cuebot.App.ViewModels;

namespace Cuebot.App.Views;

public partial class ChatView : UserControl
{
    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is ChatViewModel vm) vm.LineAdded += ScrollToNewest;
        };
    }

    private void ScrollToNewest()
    {
        if (ChatList.ItemCount > 0) ChatList.ScrollIntoView(ChatList.ItemCount - 1);
    }

    private void OnDraftKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || DataContext is not ChatViewModel vm) return;
        vm.SendCommand.Execute(null);
        e.Handled = true;
    }
}
