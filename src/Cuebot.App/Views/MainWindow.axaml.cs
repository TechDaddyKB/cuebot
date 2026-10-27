using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Cuebot.App.Shell;
using Cuebot.App.ViewModels;

namespace Cuebot.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Tunnel: the list boxes would otherwise swallow Up/Down at their ends.
        NavHost.AddHandler(KeyDownEvent, OnNavKeyDown, RoutingStrategies.Tunnel);
        DataContextChanged += (_, _) => BindShortcuts();
    }

    private const string ShortcutTag = "cuebot-shortcut";

    /// <summary>Window KeyBindings for <see cref="AppShortcuts.Window"/> (the list the Settings page shows).</summary>
    private void BindShortcuts()
    {
        foreach (var old in KeyBindings.Where(k => Equals(k.CommandParameter, ShortcutTag)).ToArray()) KeyBindings.Remove(old);
        if (DataContext is not MainWindowViewModel vm) return;
        vm.IsTypingText = IsTypingText;
        foreach (var shortcut in AppShortcuts.Window)
        {
            if (!vm.ShortcutCommands.TryGetValue(shortcut.Id, out var command)) continue;
            KeyBindings.Add(new KeyBinding { Gesture = KeyGesture.Parse(shortcut.Gesture), Command = command, CommandParameter = ShortcutTag });
        }
    }

    /// <summary>True when keyboard focus is in a text field or code editor.</summary>
    private bool IsTypingText() =>
        FocusManager?.GetFocusedElement() is Avalonia.Visual v
        && (v.FindAncestorOfType<TextBox>(includeSelf: true) is not null
            || v.FindAncestorOfType<AutoCompleteBox>(includeSelf: true) is not null
            || v.FindAncestorOfType<Cuebot.App.Controls.CodeEditor>(includeSelf: true) is not null);

    /// <summary>
    /// Sidebar keyboard navigation: each section is its own list, so Up on the first page / Down on the last page moves
    /// to the previous / next expanded section, like one continuous list. Tab reaches the section headers (Enter or
    /// Space toggles them).
    /// </summary>
    private void OnNavKeyDown(object? sender, KeyEventArgs e)
    {
        if ((e.Source as Avalonia.Visual)?.FindAncestorOfType<ListBox>(includeSelf: true) is not { DataContext: NavGroupViewModel group }
            || DataContext is not MainWindowViewModel vm) return;
        if (e.Key is not (Key.Up or Key.Down) || e.KeyModifiers != KeyModifiers.None) return;
        var index = group.Selected is null ? -1 : group.Pages.ToList().IndexOf(group.Selected);
        var down = e.Key == Key.Down;
        if ((down && index < group.Pages.Count - 1) || (!down && index > 0)) return; // the list moves within itself

        var groups = vm.NavGroups;
        var at = groups.ToList().IndexOf(group);
        for (var i = at + (down ? 1 : -1); i >= 0 && i < groups.Count; i += down ? 1 : -1)
        {
            if (!groups[i].IsExpanded || groups[i].Pages.Count == 0) continue;
            var target = down ? groups[i].Pages[0] : groups[i].Pages[^1];
            vm.SelectedPage = target;
            FocusPage(groups[i], target);
            e.Handled = true;
            return;
        }
    }

    private void FocusPage(NavGroupViewModel group, PageViewModel page)
    {
        var list = NavHost.GetVisualDescendants().OfType<ListBox>().FirstOrDefault(l => l.DataContext == group);
        if (list?.ContainerFromItem(page) is ListBoxItem item)
        {
            item.Focus(NavigationMethod.Directional);
            item.BringIntoView();
        }
    }
}
