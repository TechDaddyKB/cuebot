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

    /// <summary>The streamer profiles; <see cref="Paths"/> is the active one's folder. Null in tests and the designer.</summary>
    public Cuebot.Core.Profiles.ProfileCatalog? Profiles { get; init; }

    /// <summary>The profile this Cuebot opened (<see cref="Profiles"/> may already name the next one).</summary>
    public Cuebot.Core.Profiles.ProfileEntry? OpenProfile { get; init; }

    /// <summary>The profile Cuebot switched from to open this one; null on a normal start.</summary>
    public Cuebot.Core.Profiles.ProfileEntry? PreviousProfile { get; init; }

    /// <summary>
    /// Gives the profile switcher the profiles and how to restart on another one (<paramref name="switchNow"/> makes
    /// it active and quits). Nothing without profiles.
    /// </summary>
    public void AttachSwitcher(AppHost host, Action<string> switchNow)
    {
        if (Profiles is not null && OpenProfile is not null)
            host.Get<Cuebot.Core.Profiles.ProfileSwitcher>().Attach(Profiles, OpenProfile, PreviousProfile, switchNow);
    }

    /// <summary>The restart is a switch to another profile (already active in <see cref="Profiles"/>).</summary>
    public bool SwitchingProfile { get; set; }

    /// <summary>
    /// Makes another profile the one Cuebot opens and asks for the restart that opens it; the caller then quits.
    /// False when there is no such profile or it is the open one.
    /// </summary>
    public bool PrepareSwitch(string idOrName)
    {
        if (Profiles?.Find(idOrName) is not { } profile || profile.Id == OpenProfile?.Id) return false;
        Profiles.SetActive(profile.Id);
        SwitchingProfile = true;
        RestartRequested = true;
        return true;
    }

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
