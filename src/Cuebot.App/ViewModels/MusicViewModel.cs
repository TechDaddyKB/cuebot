using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core;
using Cuebot.Core.Desktop.Clipboard;
using Cuebot.Integrations.NowPlaying;
using Cuebot.Integrations.Spotify;

namespace Cuebot.App.ViewModels;

/// <summary>
/// The Music page: what plays now (from a player on this computer or Spotify), the media keys, the text file for OBS,
/// and setting up Spotify (the streamer's own Spotify app) for song requests.
/// </summary>
public sealed partial class MusicViewModel : PageViewModel
{
    public const string PageTitle = "Music";

    private readonly NowPlayingService _nowPlaying;
    private readonly SpotifyService _spotify;
    private readonly IClipboardService? _clipboard;
    private CancellationTokenSource? _signIn;

    [ObservableProperty] private string _info = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _fileMessage = "";
    [ObservableProperty] private string _spotifyInfo = "";
    [ObservableProperty] private string _spotifyError = "";
    [ObservableProperty] private string _testSong = "";
    [ObservableProperty] private bool _busy;

    public MusicViewModel(NowPlayingService nowPlaying, IClipboardService? clipboard = null) : base(PageTitle)
    {
        _nowPlaying = nowPlaying;
        _spotify = nowPlaying.Spotify;
        _clipboard = clipboard;
        nowPlaying.Changed += () => OnUi(RefreshSongs);
        _spotify.StatusChanged += () => OnUi(RefreshSpotify);
        RefreshSongs();
    }

    /// <summary>Opens links (tests replace it).</summary>
    public Action<Uri> OpenBrowser { get; init; } = uri => Cuebot.App.Shell.UrlLauncher.Open(uri.AbsoluteUri);

    private NowPlayingSettings S => _nowPlaying.Settings;

    // ---------------------------------------------------------------- now playing

    public bool Watching
    {
        get => S.Enabled;
        set
        {
            if (S.Enabled == value) return;
            _ = RunAsync(() => _nowPlaying.SetEnabledAsync(value), value ? "Cuebot now watches what plays." : "Stopped watching.");
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<string> Sources { get; } =
        ["Automatic (whatever plays)", "One player on this computer", "Spotify (signed-in account)"];

    public int SourceIndex
    {
        get => (int)S.Source;
        set
        {
            if (value < 0 || value == (int)S.Source) return;
            _ = RunAsync(() => _nowPlaying.SetSourceAsync((NowPlayingSource)value), null);
            OnPropertyChanged();
            OnPropertyChanged(nameof(PicksPlayer));
        }
    }

    public bool PicksPlayer => S.Source == NowPlayingSource.Player;

    public string Player
    {
        get => S.Player;
        set
        {
            S.Player = value.Trim();
            _nowPlaying.SaveSettings();
            OnPropertyChanged();
        }
    }

    public string SystemName => _nowPlaying.System.Name;

    public string SystemStatus => !_nowPlaying.System.IsSupported || _nowPlaying.System.IsRunning
        ? _nowPlaying.System.StatusText
        : "Tick \"Watch what plays\" to look for music players on this computer.";

    public string NowTitle => _nowPlaying.Current is { } c ? (c.Song.Title.Length > 0 ? c.Song.Title : "(no title)") : "Nothing is playing";

    public string NowDetails => _nowPlaying.Current is { } c
        ? string.Join(" · ", new[] { c.Song.Artist, c.Song.Album, c.Song.DurationText, c.Name, c.IsPlaying ? "" : "paused" }.Where(t => t.Length > 0))
        : _nowPlaying.IsWatching ? "Start a song in your music player." : "Cuebot looks when an action asks, or all the time while \"Watch what plays\" is ticked.";

    /// <summary>The players Cuebot sees, for the player box.</summary>
    public ObservableCollection<string> PlayerNames { get; } = [];

    public string PlayersLine => PlayerNames.Count == 0 ? "" : "Open players: " + string.Join(", ", PlayerNames);

    [RelayCommand]
    private Task PlayPause() => ControlAsync(MediaCommand.PlayPause);

    [RelayCommand]
    private Task Next() => ControlAsync(MediaCommand.Next);

    [RelayCommand]
    private Task Previous() => ControlAsync(MediaCommand.Previous);

    private Task ControlAsync(MediaCommand command) => RunAsync(async () =>
    {
        var target = await _nowPlaying.ControlAsync(command, PicksPlayer ? S.Player : "");
        Info = $"Sent to {target.Name}.";
    }, null);

    [RelayCommand]
    private Task Refresh() => RunAsync(async () =>
    {
        var current = await _nowPlaying.GetCurrentAsync();
        Info = current is null ? "Nothing is playing right now." : $"Now playing: {NowPlayingService.Describe(current)} ({current.Name}).";
    }, null);

    // ---------------------------------------------------------------- text file

    public bool WriteTextFile
    {
        get => S.WriteTextFile;
        set
        {
            S.WriteTextFile = value;
            _nowPlaying.SaveSettings();
            OnPropertyChanged();
            if (value && !S.Enabled) Watching = true;   // the file only updates while Cuebot watches
        }
    }

    public string TextFilePath
    {
        get => S.TextFilePath;
        set
        {
            S.TextFilePath = value.Trim();
            _nowPlaying.SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(EffectiveTextFile));
        }
    }

    public string EffectiveTextFile => _nowPlaying.TextFilePath;

    public string TextFormat
    {
        get => S.TextFormat;
        set
        {
            S.TextFormat = value;
            _nowPlaying.SaveSettings();
            OnPropertyChanged();
        }
    }

    public string IdleText
    {
        get => S.IdleText;
        set
        {
            S.IdleText = value;
            _nowPlaying.SaveSettings();
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private Task CopyFilePath() => CopyAsync(EffectiveTextFile, "File path copied. In OBS, add a Text source, tick \"Read from file\" and paste it.", isSpotify: false, file: true);

    // ---------------------------------------------------------------- Spotify

    public string ClientId
    {
        get => _spotify.Settings.ClientId;
        set
        {
            _spotify.Settings.ClientId = value.Trim();
            _spotify.SaveSettings();
            _spotify.RefreshStatus();
            OnPropertyChanged();
        }
    }

    public decimal? RedirectPort
    {
        get => _spotify.Settings.RedirectPort;
        set
        {
            _spotify.Settings.RedirectPort = (int)Math.Clamp(value ?? SpotifyAuth.DefaultPort, 1024, 65535);
            _spotify.SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(RedirectUri));
        }
    }

    public string RedirectUri => _spotify.RedirectUri;

    public bool SignedIn => _spotify.IsSignedIn;

    public bool NotSignedIn => !_spotify.IsSignedIn;

    public bool SigningIn => _signIn is not null;

    public string SpotifyState => _spotify.State switch
    {
        ConnectionState.Connected => "Signed in",
        ConnectionState.Connecting => "Signing in…",
        ConnectionState.Error => "Needs attention",
        _ => _spotify.IsSignedIn ? "Signed in" : "Not signed in",
    };

    public string SpotifyStatus => _spotify.StatusMessage;

    [RelayCommand]
    private Task CopyRedirect() => CopyAsync(RedirectUri, "Copied. Paste it under Redirect URIs in your Spotify app, click Add, then Save.", isSpotify: true);

    [RelayCommand]
    private void OpenDashboard() => OpenBrowser(new Uri("https://developer.spotify.com/dashboard"));

    [RelayCommand]
    private async Task SignIn()
    {
        if (_signIn is not null) return;
        SpotifyError = "";
        SpotifyInfo = "Your browser opens Spotify. Click Agree there, then come back here.";
        _signIn = new CancellationTokenSource();
        RefreshSpotify();
        try
        {
            await _spotify.SignInAsync(OpenBrowser, _signIn.Token);
            SpotifyInfo = "Signed in. Try adding a song below.";
        }
        catch (OperationCanceledException)
        {
            SpotifyInfo = "Sign-in cancelled.";
        }
        catch (Exception ex)
        {
            SpotifyInfo = "";
            SpotifyError = ex.Message;
        }
        finally
        {
            _signIn.Dispose();
            _signIn = null;
            RefreshSpotify();
        }
    }

    [RelayCommand]
    private void CancelSignIn() => _signIn?.Cancel();

    [RelayCommand]
    private async Task SignOut()
    {
        await _spotify.SignOutAsync();
        SpotifyError = "";
        SpotifyInfo = "Signed out. To also remove Cuebot from your Spotify account, open spotify.com → Account → Manage apps.";
        RefreshSpotify();
    }

    [RelayCommand]
    private async Task AddTestSong()
    {
        SpotifyError = "";
        SpotifyInfo = "";
        try
        {
            var result = await _spotify.AddToQueueAsync(TestSong);
            SpotifyInfo = $"Added to your queue: {result.Track.Title}{(result.Track.Artist.Length > 0 ? " by " + result.Track.Artist : "")}.";
        }
        catch (Exception ex) when (ex is SpotifyException or HttpRequestException or TaskCanceledException)
        {
            SpotifyError = ex is SpotifyException ? ex.Message : "Cannot reach Spotify right now. Try again in a minute.";
        }
    }

    // ---------------------------------------------------------------- helpers

    private async Task RunAsync(Func<Task> work, string? done)
    {
        Error = "";
        Info = "";
        Busy = true;
        try
        {
            await work();
            if (done is not null) Info = done;
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Busy = false;
            RefreshSongs();
        }
    }

    private async Task CopyAsync(string text, string done, bool isSpotify, bool file = false)
    {
        void Say(string info, string error)
        {
            if (isSpotify) { SpotifyInfo = info; SpotifyError = error; }
            else if (file) { FileMessage = info.Length > 0 ? info : error; }
            else { Info = info; Error = error; }
        }
        if (_clipboard is not { IsAvailable: true } clipboard)
        {
            Say("", "The clipboard is not available; select the text and copy it by hand.");
            return;
        }
        try
        {
            await clipboard.SetTextAsync(text);
            Say(done, "");
        }
        catch (Exception ex)
        {
            Say("", "Could not copy: " + ex.Message);
        }
    }

    private void RefreshSongs()
    {
        var names = _nowPlaying.Sessions.Select(s => s.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!names.SequenceEqual(PlayerNames))
        {
            PlayerNames.Clear();
            foreach (var name in names) PlayerNames.Add(name);
        }
        OnPropertyChanged(nameof(NowTitle));
        OnPropertyChanged(nameof(NowDetails));
        OnPropertyChanged(nameof(SystemStatus));
        OnPropertyChanged(nameof(PlayersLine));
        OnPropertyChanged(nameof(Watching));
    }

    private void RefreshSpotify()
    {
        OnPropertyChanged(nameof(SignedIn));
        OnPropertyChanged(nameof(NotSignedIn));
        OnPropertyChanged(nameof(SigningIn));
        OnPropertyChanged(nameof(SpotifyState));
        OnPropertyChanged(nameof(SpotifyStatus));
        RefreshSongs();
    }
}
