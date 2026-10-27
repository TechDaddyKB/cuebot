using Cuebot.Core;
using Cuebot.Core.Logging;

namespace Cuebot.App.Shell;

/// <summary>Everything <see cref="Program"/> prepares before the UI (or headless loop) starts.</summary>
public sealed class ShellContext
{
    /// <summary>Set by <see cref="Program"/> for the desktop app; null in tests and the designer.</summary>
    public static ShellContext? Current { get; set; }

    public required StartupOptions Options { get; init; }
    public required AppPaths Paths { get; init; }
    public required AppSettingsStore Settings { get; init; }
    public SingleInstance? Instance { get; init; }

    /// <summary>Started early so the answer is usually ready by the time the window would be hidden.</summary>
    public Task<bool> TrayProbe { get; set; } = Task.FromResult(OperatingSystem.IsWindows());

    /// <summary>Created by the UI or headless runner; disposed once by <see cref="Program"/>.</summary>
    public AppHost? Host { get; set; }

    /// <summary>The app asked to start again after shutting down (restore, settings).</summary>
    public bool RestartRequested { get; set; }

    private readonly TaskCompletionSource _uiReady = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the main window has opened (or, when starting in the tray, the UI thread is running).</summary>
    public Task UiReady => _uiReady.Task;

    public void MarkUiReady() => _uiReady.TrySetResult();

    private readonly List<(LogLevel Level, string Message)> _pending = [];

    /// <summary>Things that happened before the log existed (backups, restores), written once it does.</summary>
    public void Note(LogLevel level, string message)
    {
        lock (_pending) _pending.Add((level, message));
    }

    public void FlushNotes(AppLog log)
    {
        lock (_pending)
        {
            foreach (var (level, message) in _pending) log.Write(level, "Cuebot", message);
            _pending.Clear();
        }
    }
}
