using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Cuebot.Core.Desktop.Clipboard;
using Cuebot.Core.Desktop.Notifications;

namespace Cuebot.App.Views.Extras.Desktop;

/// <summary>The desktop app's clipboard, through any open Cuebot window (the main window may be hidden in the tray).</summary>
public sealed class AvaloniaClipboardService : IClipboardService
{
    public bool IsAvailable => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime;

    public Task SetTextAsync(string text, CancellationToken ct = default) =>
        Dispatcher.UIThread.InvokeAsync(() => Clipboard().SetTextAsync(text)).WaitAsync(ct);

    public Task<string?> GetTextAsync(CancellationToken ct = default) =>
        Dispatcher.UIThread.InvokeAsync(() => Clipboard().TryGetTextAsync()).WaitAsync(ct);

    private static IClipboard Clipboard()
    {
        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
        var window = desktop?.MainWindow ?? desktop?.Windows.FirstOrDefault();
        return window?.Clipboard ?? throw new InvalidOperationException(NullClipboardService.Reason);
    }
}

/// <summary>
/// In-app toasts: small borderless, top-most windows stacked in the bottom-right corner of the primary screen.
/// They don't take focus, close after their duration, and report a click through the activation callback.
/// </summary>
public sealed class AvaloniaToastPresenter : IToastPresenter
{
    private const int Gap = 12;
    private readonly List<Window> _open = [];

    public bool IsAvailable => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime;

    public void Show(DesktopNotification notification, Action activated) =>
        Dispatcher.UIThread.Post(() => ShowOnUi(notification, activated));

    private void ShowOnUi(DesktopNotification notification, Action activated)
    {
        var window = CreateWindow(notification);
        var clicked = false;
        window.PointerPressed += (_, _) =>
        {
            if (clicked) return;
            clicked = true;
            activated();
            window.Close();
        };
        window.Closed += (_, _) =>
        {
            _open.Remove(window);
            Restack();
        };
        _open.Add(window);
        window.Opened += (_, _) => Restack();
        window.Show();
        DispatcherTimer.RunOnce(() => { if (window.IsVisible) window.Close(); }, TimeSpan.FromSeconds(Math.Clamp(notification.DurationSeconds, 1, 120)));
    }

    /// <summary>The toast window (public for screenshots and tests).</summary>
    public static Window CreateWindow(DesktopNotification notification)
    {
        var text = new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center };
        if (notification.Title.Length > 0)
            text.Children.Add(new TextBlock { Text = notification.Title, FontWeight = FontWeight.SemiBold, FontSize = 15, TextWrapping = TextWrapping.Wrap });
        if (notification.Message.Length > 0)
            text.Children.Add(new TextBlock { Text = notification.Message, TextWrapping = TextWrapping.Wrap, Opacity = 0.85 });

        var row = new DockPanel();
        if (LoadIcon(notification.IconPath) is { } icon)
        {
            var image = new Image { Source = icon, Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(image, Dock.Left);
            row.Children.Add(image);
        }
        row.Children.Add(text);

        var card = new Border
        {
            Padding = new Thickness(16, 12),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = row,
        };
        if (Application.Current?.TryFindResource("CuebotCard", out var background) == true && background is IBrush brush) card.Background = brush;
        if (Application.Current?.TryFindResource("CuebotAccent", out var accent) == true && accent is IBrush border) card.BorderBrush = border;

        return new Window
        {
            Title = notification.Title.Length > 0 ? notification.Title : "Cuebot",
            Content = card,
            Width = 360,
            SizeToContent = SizeToContent.Height,
            WindowDecorations = WindowDecorations.None,
            Topmost = true,
            ShowActivated = false,
            ShowInTaskbar = false,
            CanResize = false,
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = Brushes.Transparent,
        };
    }

    private void Restack()
    {
        var bottom = 0;
        foreach (var window in _open.AsEnumerable().Reverse())
        {
            var screen = window.Screens.Primary ?? window.Screens.All.FirstOrDefault();
            if (screen is null) continue;
            var area = screen.WorkingArea;
            var scale = screen.Scaling;
            var width = (int)(window.Bounds.Width * scale);
            var height = (int)(Math.Max(window.Bounds.Height, 60) * scale);
            var gap = (int)(Gap * scale);
            bottom += gap + height;
            window.Position = new PixelPoint(area.Right - width - gap, area.Bottom - bottom);
        }
    }

    private static Bitmap? LoadIcon(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try { return new Bitmap(path); }
        catch (Exception) { return null; }
    }
}
