using Avalonia.Controls;
using Avalonia.Controls.Chrome;
using Avalonia.Input;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Media;
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
        ZoomViewport.SizeChanged += (_, _) => LayOutZoomedShell();
        // Tunnel: the box itself takes the first Enter (to close its list) before a bubbling handler sees it.
        Jump.AddHandler(KeyDownEvent, OnJumpKeyDown, RoutingStrategies.Tunnel);
        Jump.SelectionChanged += (_, _) => { if (Jump.SelectedItem is string title) JumpTo(title); };
        // Esc leaves full screen (the decorations' own way back is not reachable on every platform). Fields that use
        // Esc themselves, like the jump box, mark it handled first.
        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled || WindowState != WindowState.FullScreen) return;
            ToggleFullScreen();
            e.Handled = true;
        };
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

    /// <summary>The state to go back to when full screen ends (it may have started from the ⤡ caption button).</summary>
    private WindowState _beforeFullScreen = WindowState.Normal;

    protected override void OnPropertyChanged(Avalonia.AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty && change.NewValue is WindowState.FullScreen)
            _beforeFullScreen = change.OldValue is WindowState.Maximized ? WindowState.Maximized : WindowState.Normal;
    }

    private void ToggleFullScreen() =>
        WindowState = WindowState == WindowState.FullScreen ? _beforeFullScreen : WindowState.FullScreen;

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
            Jump.IsDropDownOpen = false;
            e.Handled = true;
        }
        // An empty, closed box lets Esc through (it leaves full screen): focus can stay in the box after a jump.
        else if (e.Key == Key.Escape && (!string.IsNullOrEmpty(Jump.Text) || Jump.IsDropDownOpen))
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
        if (_boundVm is not null)
        {
            _boundVm.JumpRequested -= OnJumpRequested;
            _boundVm.FullScreenRequested -= ToggleFullScreen;
            _boundVm.PropertyChanged -= OnViewModelPropertyChanged;
        }
        if (DataContext is not MainWindowViewModel vm) return;
        _boundVm = vm;
        vm.PropertyChanged += OnViewModelPropertyChanged;
        vm.JumpRequested += OnJumpRequested;
        vm.FullScreenRequested += ToggleFullScreen;
        vm.IsTypingText = IsTypingText;
        foreach (var shortcut in AppShortcuts.Window)
        {
            if (!vm.ShortcutCommands.TryGetValue(shortcut.Id, out var command)) continue;
            foreach (var gesture in shortcut.Bindings)
                KeyBindings.Add(new KeyBinding { Gesture = KeyGesture.Parse(gesture), Command = command, CommandParameter = ShortcutTag });
        }
        ApplyZoom();
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWindowViewModel.UiScale) or nameof(MainWindowViewModel.CompactDensity)) ApplyZoom();
    }

    /// <summary>Scales the shell and sets its density. Popups follow through <c>Popup.InheritsTransform</c>.</summary>
    private void ApplyZoom()
    {
        if (_boundVm is not { } vm) return;
        var scale = UiZoom.Normalize(vm.UiScale);
        UiZoom.Current = scale;
        Classes.Set("compact", vm.CompactDensity);
        if (ZoomLayer.LayoutTransform is not ScaleTransform current || current.ScaleX != scale)
            ZoomLayer.LayoutTransform = new ScaleTransform(scale, scale);
        if (OperatingSystem.IsWindows())
        {
            // The caption buttons are not scaled, so the bar keeps room for them in scaled units.
            ExtendClientAreaTitleBarHeightHint = 52 * scale;
            var m = TopBarContent.Margin;
            TopBarContent.Margin = new Avalonia.Thickness(m.Left, m.Top, CaptionButtonsWidth / scale, m.Bottom);
        }
        LayOutZoomedShell();
    }

    /// <summary>
    /// Gives the shell the window's size in scaled units, but never less than its minimum: a small window at a high
    /// zoom scrolls. Scrolling stays off otherwise, so the viewer takes no keys or wheel events from the pages.
    /// </summary>
    private void LayOutZoomedShell()
    {
        var viewport = ZoomViewport.Bounds.Size;
        if (viewport.Width <= 0 || viewport.Height <= 0) return;
        var scale = UiZoom.Normalize(_boundVm?.UiScale ?? UiZoom.Default);
        var (width, height) = UiZoom.ContentSize(viewport.Width, viewport.Height, scale);
        ShellRoot.Width = width;
        ShellRoot.Height = height;
        ZoomViewport.HorizontalScrollBarVisibility = width * scale > viewport.Width + 0.5 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        ZoomViewport.VerticalScrollBarVisibility = height * scale > viewport.Height + 0.5 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }

    /// <summary>True when keyboard focus is in a text field or code editor.</summary>
    private bool IsTypingText() =>
        FocusManager?.GetFocusedElement() is Avalonia.Visual v
        && (v.FindAncestorOfType<TextBox>(includeSelf: true) is not null
            || v.FindAncestorOfType<AutoCompleteBox>(includeSelf: true) is not null
            || v.FindAncestorOfType<Cuebot.App.Controls.CodeEditor>(includeSelf: true) is not null);

}
