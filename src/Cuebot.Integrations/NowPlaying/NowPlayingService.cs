using System.Globalization;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Integrations.Spotify;

namespace Cuebot.Integrations.NowPlaying;

/// <summary>
/// "Now playing": picks the song from the chosen source (a player on this computer or Spotify), publishes
/// <see cref="SongChangedEvent"/> when it changes, keeps the optional text file for OBS up to date, and presses media
/// keys. Events are raised on background threads.
/// </summary>
public sealed class NowPlayingService : IConnectionStatus, IAsyncDisposable
{
    public const string Category = "Now playing";
    public const string SongChangedEvent = "nowplaying.songChanged";
    public const string DefaultFileName = "now-playing.txt";

    private readonly JsonFile<IntegrationsSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly SpotifyService _spotify;
    private readonly ISystemMediaSource _system;
    private readonly string _dataFolder;
    private readonly object _gate = new();
    private readonly object _fileGate = new();
    private readonly TimeSpan _announceDelay;
    private readonly Timer _announceTimer;
    private PlayerSession? _current;
    private Song? _lastAnnounced;
    private string? _lastFileText;
    private string? _lastFilePath;
    private bool _watching;
    private string? _fileError;

    public NowPlayingService(JsonFile<IntegrationsSettings> settings, EventBus bus, AppLog log, SpotifyService spotify,
        ISystemMediaSource system, string dataFolder, TimeSpan? announceDelay = null)
    {
        // Players often send a song in pieces (a file name first, then the real title): announce once it settled.
        _announceDelay = announceDelay ?? TimeSpan.FromMilliseconds(750);
        _announceTimer = new Timer(_ => Announce());
        _settings = settings;
        _bus = bus;
        _log = log;
        _spotify = spotify;
        _system = system;
        _dataFolder = dataFolder;
        system.Changed += Update;
        spotify.PlaybackChanged += Update;
        spotify.StatusChanged += Update;
    }

    public NowPlayingSettings Settings => _settings.Value.NowPlaying;

    public ISystemMediaSource System => _system;

    public SpotifyService Spotify => _spotify;

    public string Name => "Now playing";

    public ConnectionState State => _watching ? ConnectionState.Connected : ConnectionState.Disconnected;

    public string StatusMessage => !_watching ? "Not watching." : Current is { } c ? $"{Describe(c)} ({c.Name})" : "Nothing is playing.";

    public bool IsWatching => _watching;

    /// <summary>The song "now playing" means right now (null when nothing plays in the chosen source).</summary>
    public PlayerSession? Current { get { lock (_gate) return _current; } }

    /// <summary>Every player Cuebot sees, Spotify last.</summary>
    public IReadOnlyList<PlayerSession> Sessions =>
        [.. _system.Sessions, .. _spotify.Playback is { } p ? new[] { p.ToSession() } : []];

    /// <summary>Where the text file goes (the setting, or now-playing.txt in the data folder).</summary>
    public string TextFilePath => Settings.TextFilePath.Trim() is { Length: > 0 } path ? path : Path.Combine(_dataFolder, DefaultFileName);

    public event Action? StatusChanged;

    /// <summary>The current song or the players changed.</summary>
    public event Action? Changed;

    public void SaveSettings()
    {
        _settings.ScheduleSave();
        Update();
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (Settings.Enabled) await StartWatchingAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Starts reading the players (also used on demand by "Get now playing" and "Media control").</summary>
    public async Task StartWatchingAsync(CancellationToken ct = default)
    {
        if (!_watching) _log.Info(Category, $"Watching what plays ({Settings.Source})");
        _watching = true;
        if (Settings.Source != NowPlayingSource.Spotify && _system.IsSupported) await _system.StartAsync(ct).ConfigureAwait(false);
        Update();
    }

    public async Task StopWatchingAsync()
    {
        _watching = false;
        await _system.StopAsync().ConfigureAwait(false);
        Update();
    }

    /// <summary>Turns watching on or off and remembers it.</summary>
    public async Task SetEnabledAsync(bool enabled)
    {
        Settings.Enabled = enabled;
        _settings.ScheduleSave();
        if (enabled) await StartWatchingAsync().ConfigureAwait(false);
        else await StopWatchingAsync().ConfigureAwait(false);
    }

    /// <summary>Changes the source (starting the computer's players when the new source needs them).</summary>
    public async Task SetSourceAsync(NowPlayingSource source, string? player = null)
    {
        Settings.Source = source;
        if (player is not null) Settings.Player = player.Trim();
        _settings.ScheduleSave();
        if (_watching && source != NowPlayingSource.Spotify && _system.IsSupported) await _system.StartAsync().ConfigureAwait(false);
        Update();
    }

    /// <summary>
    /// The song now, for steps: starts watching when needed and waits briefly for the first answer, and asks Spotify
    /// directly when it has not been checked yet.
    /// </summary>
    public async Task<PlayerSession?> GetCurrentAsync(CancellationToken ct = default)
    {
        var wasWatching = _watching && (_system.IsRunning || Settings.Source == NowPlayingSource.Spotify || !_system.IsSupported);
        if (!wasWatching)
        {
            var first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnChanged() => first.TrySetResult();
            _system.Changed += OnChanged;
            try
            {
                await StartWatchingAsync(ct).ConfigureAwait(false);
                if (_system.IsRunning) await Task.WhenAny(first.Task, Task.Delay(TimeSpan.FromSeconds(3), ct)).ConfigureAwait(false);
            }
            finally
            {
                _system.Changed -= OnChanged;
            }
        }
        if (Settings.Source != NowPlayingSource.Player && _spotify.IsSignedIn && _spotify.Playback is null && Current is null)
        {
            try
            {
                _ = await _spotify.GetCurrentlyPlayingAsync(ct).ConfigureAwait(false);
                _spotify.WakePoll();
                await Task.Delay(200, ct).ConfigureAwait(false);
            }
            catch (SpotifyException ex)
            {
                _log.Debug(Category, $"Spotify: {ex.Message}");
            }
        }
        Update();
        return Current;
    }

    /// <summary>
    /// Presses a media key: on the player named <paramref name="player"/> (part of its name; empty: the one now
    /// playing). Spotify songs go through Spotify itself.
    /// </summary>
    /// <returns>The player that got the key.</returns>
    public async Task<PlayerSession> ControlAsync(MediaCommand command, string player = "", CancellationToken ct = default)
    {
        await GetCurrentAsync(ct).ConfigureAwait(false);
        var wanted = player.Trim();
        var target = wanted.Length > 0
            ? Sessions.Where(s => SessionPicker.Matches(s, wanted)).OrderByDescending(s => s.IsPlaying).FirstOrDefault()
              ?? throw new InvalidOperationException($"No open music player is called \"{wanted}\".")
            : Current ?? Sessions.FirstOrDefault(s => !s.FromSpotify)
              ?? throw new InvalidOperationException("No music player is open.");
        if (target.FromSpotify)
        {
            switch (command)
            {
                case MediaCommand.Next: await _spotify.NextAsync(ct).ConfigureAwait(false); break;
                case MediaCommand.Previous: await _spotify.PreviousAsync(ct).ConfigureAwait(false); break;
                case MediaCommand.Play: await _spotify.ResumeAsync(ct).ConfigureAwait(false); break;
                case MediaCommand.Pause: await _spotify.PauseAsync(ct).ConfigureAwait(false); break;
                default: await _spotify.TogglePlayAsync(ct).ConfigureAwait(false); break;
            }
            return target;
        }
        if (!await _system.ControlAsync(target.Id, command, ct).ConfigureAwait(false))
            throw new InvalidOperationException($"{target.Name} did not answer. Is it still open?");
        return target;
    }

    /// <summary>Re-picks the song; announces a new one and updates the text file.</summary>
    private void Update()
    {
        PlayerSession? current;
        bool changed;
        lock (_gate)
        {
            var s = Settings;
            var spotify = _spotify.Playback?.ToSession();
            current = SessionPicker.Pick(_system.Sessions, _system.CurrentSessionId, spotify, s.Source, s.Player);
            changed = current != _current;
            _current = current;
            if (_watching && current is { IsPlaying: true } && !current.Song.SameSongAs(_lastAnnounced))
            {
                try { _announceTimer.Change(_announceDelay, Timeout.InfiniteTimeSpan); }
                catch (ObjectDisposedException) { }   // shutting down
            }
        }
        WriteTextFile(current);
        if (changed) StatusChanged?.Invoke();
        Changed?.Invoke();
    }

    /// <summary>Publishes "Song changed" when the song is still new once it settled.</summary>
    private void Announce()
    {
        PlayerSession? announce = null;
        lock (_gate)
        {
            if (_watching && _current is { IsPlaying: true } current && !current.Song.SameSongAs(_lastAnnounced))
            {
                _lastAnnounced = current.Song;
                announce = current;
            }
        }
        if (announce is null) return;
        _log.Debug(Category, $"Song changed: {Describe(announce)} ({announce.Name})");
        _bus.Publish(new BotEvent(SongChangedEvent, SongArgs(announce)));
    }

    /// <summary>The variables a song gives actions (also under nowPlaying.* so one message works everywhere).</summary>
    public static Dictionary<string, object?> SongArgs(PlayerSession? session)
    {
        var song = session?.Song ?? Song.None;
        var seconds = song.Duration is { } d ? (long)Math.Round(d.TotalSeconds) : 0;
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["title"] = song.Title,
            ["artist"] = song.Artist,
            ["album"] = song.Album,
            ["player"] = session?.Name ?? "",
            ["artUrl"] = song.ArtUrl,
            ["duration"] = seconds,
            ["durationText"] = song.DurationText,
            ["link"] = song.Link,
            ["uri"] = song.Uri,
            ["source"] = session is null ? "" : session.FromSpotify ? "spotify" : "system",
            ["playing"] = session?.IsPlaying ?? false,
        };
        foreach (var (key, value) in args.ToArray()) args["nowPlaying." + key] = value;
        return args;
    }

    /// <summary>"Artist - Title" (or just one of them).</summary>
    public static string Describe(PlayerSession session) =>
        session.Song.Artist.Length > 0 && session.Song.Title.Length > 0 ? $"{session.Song.Artist} - {session.Song.Title}"
        : session.Song.Title.Length > 0 ? session.Song.Title : session.Song.Artist;

    /// <summary>The file text for a song: the format with %title%, %artist%, %album%, %player%, %duration% filled in.</summary>
    public static string FormatText(string format, PlayerSession? session, string idle)
    {
        if (session is not { IsPlaying: true } || session.Song.IsEmpty) return idle;
        var song = session.Song;
        var text = (format.Length > 0 ? format : "%artist% - %title%")
            .Replace("%title%", song.Title, StringComparison.OrdinalIgnoreCase)
            .Replace("%artist%", song.Artist, StringComparison.OrdinalIgnoreCase)
            .Replace("%album%", song.Album, StringComparison.OrdinalIgnoreCase)
            .Replace("%player%", session.Name, StringComparison.OrdinalIgnoreCase)
            .Replace("%duration%", song.DurationText, StringComparison.OrdinalIgnoreCase);
        // "Artist - " with no artist reads badly; trim leftover separators.
        return text.Trim().Trim('-', '–', '|', '·').Trim();
    }

    private void WriteTextFile(PlayerSession? current)
    {
        var s = Settings;
        if (!s.WriteTextFile || !_watching) return;
        var text = FormatText(s.TextFormat, current, s.IdleText);
        var path = TextFilePath;
        lock (_fileGate)
        {
            if (text == _lastFileText && path == _lastFilePath) return;
            _lastFileText = text;
            _lastFilePath = path;
            WriteFile(path, text);
        }
    }

    private void WriteFile(string path, string text)
    {
        try
        {
            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);
            var temp = path + ".tmp";
            File.WriteAllText(temp, text);
            File.Move(temp, path, overwrite: true);
            _fileError = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            _lastFileText = null;
            if (_fileError != ex.Message) _log.Warn(Category, $"Could not write the now playing file {path}: {ex.Message}");
            _fileError = ex.Message;
        }
    }

    public async ValueTask DisposeAsync()
    {
        _system.Changed -= Update;
        _spotify.PlaybackChanged -= Update;
        _spotify.StatusChanged -= Update;
        await _announceTimer.DisposeAsync().ConfigureAwait(false);
        await _system.DisposeAsync().ConfigureAwait(false);
    }
}
