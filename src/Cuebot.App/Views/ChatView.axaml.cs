using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels;

namespace Cuebot.App.Views;

public partial class ChatView : UserControl
{
    private ChatViewModel? _vm;

    public ChatView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.LineAdded -= ScrollToNewest;
            _vm = DataContext as ChatViewModel;
            if (_vm is not null) _vm.LineAdded += ScrollToNewest;
        };
        // Tunnel so Enter/Tab/arrows reach us before the TextBox uses them.
        DraftBox.AddHandler(KeyDownEvent, OnDraftKeyDown, RoutingStrategies.Tunnel);
    }

    private void ScrollToNewest()
    {
        if (ChatList.ItemCount > 0) ChatList.ScrollIntoView(ChatList.ItemCount - 1);
    }

    private void OnDraftKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not ChatViewModel vm) return;
        if (vm.IsSuggesting)
        {
            switch (e.Key)
            {
                case Key.Down:
                    vm.MoveSuggestion(1);
                    e.Handled = true;
                    return;
                case Key.Up:
                    vm.MoveSuggestion(-1);
                    e.Handled = true;
                    return;
                case Key.Tab or Key.Enter:
                    if (vm.AcceptSuggestion())
                    {
                        DraftBox.CaretIndex = vm.Draft.Length;
                        e.Handled = true;
                        return;
                    }
                    break;
                case Key.Escape:
                    vm.CloseSuggestions();
                    e.Handled = true;
                    return;
            }
        }
        if (e.Key != Key.Enter) return;
        vm.SendCommand.Execute(null);
        e.Handled = true;
    }

    private void OnSuggestionTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is not ChatViewModel vm
            || (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not EmoteSuggestionViewModel suggestion)
            return;
        vm.AcceptSuggestion(suggestion);
        DraftBox.Focus();
        DraftBox.CaretIndex = vm.Draft.Length;
    }

    /// <summary>Builds the message actions menu for the line under the pointer (or the selected line).</summary>
    private void OnChatContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (DataContext is not ChatViewModel vm) return;
        var line = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as ChatLineViewModel
                   ?? vm.SelectedLine;
        if (line is null) return;
        vm.SelectedLine = line;
        var menu = BuildMenu(vm, line);
        menu.Open(ChatList);
        e.Handled = true;
    }

    /// <summary>A context menu with the line's available actions (public for tests).</summary>
    public static ContextMenu BuildMenu(ChatViewModel vm, ChatLineViewModel line)
    {
        var menu = new ContextMenu();
        foreach (var entry in vm.GetMenu(line))
        {
            if (entry.IsSeparator)
            {
                menu.Items.Add(new Separator());
                continue;
            }
            var item = new MenuItem { Header = entry.Label };
            item.Click += async (_, _) => await entry.Run!();
            menu.Items.Add(item);
        }
        return menu;
    }
}
