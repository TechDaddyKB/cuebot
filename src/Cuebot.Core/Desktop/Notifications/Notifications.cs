using System.Diagnostics;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Desktop.Notifications;

/// <summary>A desktop notification ("toast").</summary>
public sealed record DesktopNotification(string Title, string Message)
{
    /// <summary>Optional image file shown with the notification.</summary>
    public string? IconPath { get; init; }

    /// <summary>Optional id reported by the "Toast notification clicked" trigger.</summary>
    public string Id { get; init; } = "";

    public int DurationSeconds { get; init; } = 5;
}

public enum NotificationTarget
{
    /// <summary>The system's notifications where available (Linux notify-send), else the in-app toast.</summary>
    Auto,

    /// <summary>Cuebot's own toast window in the screen corner (all platforms, needs the desktop app).</summary>
    InApp,

    /// <summary>The system's notification service only.</summary>
    System,
}

/// <summary>Shows notifications. The default <see cref="DesktopNotifier"/> routes to the system or in-app presenter.</summary>
public interface INotifier
{
    /// <returns>Where it was shown ("system", "in-app").</returns>
    /// <exception cref="InvalidOperationException">No way to show it (e.g. headless without notify-send).</exception>
    Task<string> ShowAsync(DesktopNotification notification, NotificationTarget target = NotificationTarget.Auto, CancellationToken ct = default);
}

/// <summary>Cuebot's own toast window. The desktop app registers one; headless mode keeps <see cref="NullToastPresenter"/>.</summary>
public interface IToastPresenter
{
    bool IsAvailable { get; }

    /// <param name="activated">Call when the user clicks the toast.</param>
    void Show(DesktopNotification notification, Action activated);
}

public sealed class NullToastPresenter : IToastPresenter
{
    public bool IsAvailable => false;
    public void Show(DesktopNotification notification, Action activated) =>
        throw new InvalidOperationException("In-app toasts need the desktop app (not available in headless mode)");
}

/// <summary>The operating system's notification service.</summary>
public interface ISystemNotifier
{
    bool IsAvailable { get; }
    Task ShowAsync(DesktopNotification notification, CancellationToken ct);
}

/// <summary>Linux: freedesktop notifications through the <c>notify-send</c> command (libnotify), when installed.</summary>
public sealed class NotifySendNotifier(AppLog log, string? executable = null) : ISystemNotifier
{
    private readonly Lazy<string?> _path = new(() => executable ?? (OperatingSystem.IsLinux() ? FindOnPath("notify-send") : null));

    public bool IsAvailable => _path.Value is not null;

    public async Task ShowAsync(DesktopNotification notification, CancellationToken ct)
    {
        var path = _path.Value ?? throw new InvalidOperationException("notify-send is not installed (package libnotify-bin / libnotify)");
        var start = new ProcessStartInfo(path) { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        foreach (var arg in Arguments(notification)) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start notify-send");
        var stderr = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        if (process.ExitCode != 0)
        {
            var error = (await stderr).Trim();
            log.Warn("Notifications", $"notify-send failed ({process.ExitCode}): {error}");
            throw new InvalidOperationException("notify-send failed: " + (error.Length > 0 ? error : $"exit code {process.ExitCode}"));
        }
    }

    /// <summary>Command-line arguments (the text is passed as separate arguments, never through a shell).</summary>
    public static IReadOnlyList<string> Arguments(DesktopNotification n)
    {
        var args = new List<string> { "--app-name=Cuebot", $"--expire-time={Math.Clamp(n.DurationSeconds, 1, 120) * 1000}" };
        if (!string.IsNullOrWhiteSpace(n.IconPath)) args.Add("--icon=" + n.IconPath.Trim());
        args.Add("--");
        args.Add(n.Title.Length > 0 ? n.Title : "Cuebot");
        if (n.Message.Length > 0) args.Add(n.Message);
        return args;
    }

    private static string? FindOnPath(string name)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(dir, name);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}

/// <summary>desktop.json.</summary>
public sealed class DesktopSettings
{
    /// <summary>Where "Show desktop notification" goes when the sub-action says Auto.</summary>
    public NotificationTarget NotificationTarget { get; set; } = NotificationTarget.Auto;

    /// <summary>Poll the clipboard and fire "Clipboard changed" (off by default).</summary>
    public bool WatchClipboard { get; set; }
}

public static class DesktopEvents
{
    public const string ToastActivated = "desktop.toastActivated";
    public const string ClipboardChanged = "desktop.clipboardChanged";
}

/// <summary>
/// Routes notifications: Auto prefers the system service (Linux notify-send) and falls back to the in-app toast
/// (always on Windows). Clicking an in-app toast publishes "desktop.toastActivated" (toastId, title, message).
/// </summary>
public sealed class DesktopNotifier(ISystemNotifier system, IToastPresenter toast, EventBus bus, AppLog log,
    JsonFile<DesktopSettings>? settings = null) : INotifier
{
    public async Task<string> ShowAsync(DesktopNotification notification, NotificationTarget target = NotificationTarget.Auto,
        CancellationToken ct = default)
    {
        if (target == NotificationTarget.Auto) target = settings?.Value.NotificationTarget ?? NotificationTarget.Auto;
        var useSystem = target == NotificationTarget.System || (target == NotificationTarget.Auto && system.IsAvailable);
        if (useSystem)
        {
            if (!system.IsAvailable) throw new InvalidOperationException("No system notification service (install notify-send)");
            await system.ShowAsync(notification, ct);
            return "system";
        }
        if (!toast.IsAvailable)
        {
            throw new InvalidOperationException("Cannot show notifications here: in-app toasts need the desktop app"
                                                + (OperatingSystem.IsLinux() ? " and notify-send is not installed" : ""));
        }
        toast.Show(notification, () =>
        {
            log.Debug("Notifications", $"Toast '{notification.Title}' clicked");
            bus.Publish(new BotEvent(DesktopEvents.ToastActivated, new Dictionary<string, object?>
            {
                ["toastId"] = notification.Id,
                ["toastTitle"] = notification.Title,
                ["toastMessage"] = notification.Message,
            }));
        });
        return "in-app";
    }
}
