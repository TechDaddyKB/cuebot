using Cuebot.Core.Logging;
using Cuebot.Integrations.NowPlaying.Mpris;
using Cuebot.Integrations.NowPlaying.Smtc;

namespace Cuebot.Integrations.NowPlaying;

/// <summary>
/// The music players on this computer: MPRIS on Linux, the Windows media controls on Windows. Events are raised on
/// background threads.
/// </summary>
public interface ISystemMediaSource : IAsyncDisposable
{
    /// <summary>"Linux media players (MPRIS)", "Windows media controls".</summary>
    string Name { get; }

    /// <summary>False where Cuebot cannot read players (macOS); <see cref="StatusText"/> says why.</summary>
    bool IsSupported { get; }

    bool IsRunning { get; }

    /// <summary>A short, friendly line for the Music page.</summary>
    string StatusText { get; }

    IReadOnlyList<PlayerSession> Sessions { get; }

    /// <summary>The session the system itself calls current (Windows), or null.</summary>
    string? CurrentSessionId { get; }

    /// <summary>Players, songs or status changed.</summary>
    event Action? Changed;

    /// <summary>Starts watching (returns at once; the first list follows through <see cref="Changed"/>).</summary>
    Task StartAsync(CancellationToken ct = default);

    Task StopAsync();

    /// <summary>Presses a media key on one player. False when the player is gone.</summary>
    Task<bool> ControlAsync(string sessionId, MediaCommand command, CancellationToken ct = default);
}

public static class SystemMediaSources
{
    /// <summary>The source for this operating system.</summary>
    public static ISystemMediaSource ForThisSystem(AppLog log) =>
        OperatingSystem.IsLinux() || OperatingSystem.IsFreeBSD() ? new MprisSource(log)
        : OperatingSystem.IsWindows() ? new SmtcSource(log)
        : new UnsupportedMediaSource(OperatingSystem.IsMacOS()
            ? "Cuebot cannot see music players on macOS yet. Sign in to Spotify below to show your Spotify songs."
            : "Cuebot cannot see music players on this system. Sign in to Spotify below to show your Spotify songs.");
}

/// <summary>Where players cannot be read (macOS).</summary>
public sealed class UnsupportedMediaSource(string reason) : ISystemMediaSource
{
    public string Name => "Music players";
    public bool IsSupported => false;
    public bool IsRunning => false;
    public string StatusText => reason;
    public IReadOnlyList<PlayerSession> Sessions => [];
    public string? CurrentSessionId => null;

    public event Action? Changed { add { } remove { } }

    public Task StartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync() => Task.CompletedTask;
    public Task<bool> ControlAsync(string sessionId, MediaCommand command, CancellationToken ct = default) => Task.FromResult(false);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
