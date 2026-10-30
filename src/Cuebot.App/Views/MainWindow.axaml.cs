using Avalonia.Controls;
using Avalonia.Controls.Chrome;
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
        DataContextChanged += (_, _) => BindShortcuts();
        Jump.KeyDown += OnJumpKeyDown;
        Jump.SelectionChanged += (_, _) => { if (Jump.SelectedItem is string title) JumpTo(title); };
        if (OperatingSystem.IsWindows())
        {
            // The top bar is the title bar on Windows; the system caption buttons sit over its right end.
            ExtendClientAreaToDecorationsHint = true;
            ExtendClientAreaTitleBarHeightHint = 52;
            var m = TopBarContent.Margin;
            TopBarContent.Margin = new Avalonia.Thickness(m.Left, m.Top, m.Right + CaptionButtonsWidth, m.Bottom);
            // Empty parts of the bar move the window (and double-click maximizes); its controls stay clickable.
            WindowDecorationProperties.SetElementRole(TopBar, WindowDecorationsElementRole.TitleBar);
            foreach (var control in new Control[] { Jump, TddMark, Chips })
                WindowDecorationProperties.SetElementRole(control, WindowDecorationsElementRole.User);
        }
    }

    /// <summary>Room for the Windows minimize, maximize and close buttons.</summary>
    private const double CaptionButtonsWidth = 140;

    private MainWindowViewModel? _boundVm;

    private void OnJumpRequested()
    {
        Jump.Focus(NavigationMethod.Tab);
        Jump.IsDropDownOpen = true;
    }

    private void OnJumpKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            JumpTo(Jump.SelectedItem as string ?? Jump.Text);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            Jump.Text = "";
            PageHost.Focus();
            e.Handled = true;
        }
    }

    private void JumpTo(string? title)
    {
        if (DataContext is not MainWindowViewModel vm || !vm.JumpTo(title)) return;
        Jump.Text = "";
        Jump.SelectedItem = null;
        PageHost.Focus();
    }

    private const string ShortcutTag = "cuebot-shortcut";

    /// <summary>Window KeyBindings for <see cref="AppShortcuts.Window"/> (the list the Settings page shows).</summary>
    private void BindShortcuts()
    {
        foreach (var old in KeyBindings.Where(k => Equals(k.CommandParameter, ShortcutTag)).ToArray()) KeyBindings.Remove(old);
        if (_boundVm is not null) _boundVm.JumpRequested -= OnJumpRequested;
        if (DataContext is not MainWindowViewModel vm) return;
        _boundVm = vm;
        vm.JumpRequested += OnJumpRequested;
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

}
