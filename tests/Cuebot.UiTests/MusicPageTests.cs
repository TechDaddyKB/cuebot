using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels;
using Cuebot.App.Views;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Integrations;
using Cuebot.Integrations.NowPlaying;
using Cuebot.Integrations.Spotify;
using Cuebot.UiTests.Speech;
using Cuebot.UiTests.Support;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.UiTests;

/// <summary>
/// The Music page: the song from a (fake) player, the source picker, the media keys, the text file and the Spotify set-up
/// (redirect address to copy, Client ID, sign-in buttons) render without binding errors in both themes
/// (screenshots music-dark.png / music-light.png).
/// </summary>
public sealed class MusicPageTests : IAsyncDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-music-").FullName;
    private readonly FakePlayers _players = new();
    private readonly ServiceProvider _services;

    public MusicPageTests()
    {
        var collection = new ServiceCollection().AddCuebotCore(new AppPaths(_dataDir)).AddIntegrations();
        collection.AddSingleton(sp => new NowPlayingService(sp.GetRequiredService<JsonFile<IntegrationsSettings>>(), sp.GetRequiredService<EventBus>(),
            sp.GetRequiredService<AppLog>(), sp.GetRequiredService<SpotifyService>(), _players, _dataDir, TimeSpan.FromMilliseconds(10)));
        _services = collection.BuildServiceProvider();
    }

    [AvaloniaFact]
    public async Task Shows_the_song_sets_up_spotify_and_copies_the_redirect_address_in_both_themes()
    {
        var app = Application.Current!;
        var original = app.RequestedThemeVariant;
        try
        {
            TestApp.BindingErrors.Clear();
            var clipboard = new FakeClipboard();
            var opened = new List<Uri>();
            var vm = new MusicViewModel(_services.GetRequiredService<NowPlayingService>(), clipboard) { OpenBrowser = opened.Add };
            var window = new Window
            {
                Content = new MusicView { DataContext = vm },
                Width = 1000,
                Height = 1700,
                Background = app.FindResource("CuebotPanel") as Avalonia.Media.IBrush,
            };
            window.Show();
            UiPump.Pump();
            Assert.Equal("Nothing is playing", vm.NowTitle);

            vm.Watching = true;
            _players.Set(new PlayerSession("org.mpris.MediaPlayer2.vlc", "VLC media player", PlaybackState.Playing,
                new Song { Title = "Never Gonna Give You Up", Artist = "Rick Astley", Album = "Whenever You Need Somebody", Duration = TimeSpan.FromSeconds(213) }));
            await Until(() => vm.NowTitle == "Never Gonna Give You Up", "song");
            Assert.Contains("Rick Astley", vm.NowDetails);
            Assert.Contains("3:33", vm.NowDetails);
            Assert.Equal("Open players: VLC media player", vm.PlayersLine);

            await vm.NextCommand.ExecuteAsync(null);
            Assert.Equal("Sent to VLC media player.", vm.Info);
            Assert.Equal(MediaCommand.Next, Assert.Single(_players.Keys));

            vm.SourceIndex = (int)NowPlayingSource.Player;
            await Until(() => vm.PicksPlayer, "player box");
            vm.Player = " vlc ";
            var settings = _services.GetRequiredService<JsonFile<IntegrationsSettings>>().Value;
            Assert.Equal(NowPlayingSource.Player, settings.NowPlaying.Source);
            Assert.Equal("vlc", settings.NowPlaying.Player);

            vm.WriteTextFile = true;
            vm.TextFormat = "%title% - %artist%";
            await Until(() => File.Exists(vm.EffectiveTextFile) && File.ReadAllText(vm.EffectiveTextFile) == "Never Gonna Give You Up - Rick Astley", "text file");
            await vm.CopyFilePathCommand.ExecuteAsync(null);
            Assert.Equal(vm.EffectiveTextFile, clipboard.Text);

            // Spotify set-up: the loopback redirect address, the Client ID, the developer site.
            Assert.Equal("http://127.0.0.1:43117/callback", vm.RedirectUri);
            Assert.Equal(vm.RedirectUri, window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "RedirectUri").Text);
            await vm.CopyRedirectCommand.ExecuteAsync(null);
            Assert.Equal("http://127.0.0.1:43117/callback", clipboard.Text);
            Assert.Contains("Redirect URIs", vm.SpotifyInfo);
            vm.RedirectPort = 5555;
            Assert.Equal("http://127.0.0.1:5555/callback", vm.RedirectUri);
            vm.RedirectPort = 43117;
            vm.OpenDashboardCommand.Execute(null);
            Assert.Equal("https://developer.spotify.com/dashboard", Assert.Single(opened).AbsoluteUri);
            vm.ClientId = "  0123456789abcdef0123456789abcdef ";
            Assert.Equal("0123456789abcdef0123456789abcdef", settings.Spotify.ClientId);
            Assert.True(vm.NotSignedIn);
            Assert.Equal("Not signed in", vm.SpotifyState);
            vm.TestSong = "never gonna give you up";
            await vm.AddTestSongCommand.ExecuteAsync(null);
            Assert.Contains("Sign in", vm.SpotifyError);

            UiPump.Pump();
            foreach (var (theme, name) in new[] { (ThemeVariant.Dark, "dark"), (ThemeVariant.Light, "light") })
            {
                app.RequestedThemeVariant = theme;
                UiPump.Pump();
                UiPump.Save(window, $"music-{name}");
            }
            Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
            window.Close();
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }

    private static async Task Until(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Timed out waiting for " + what);
            await Task.Delay(10);
            UiPump.Pump();
        }
    }

    private sealed class FakePlayers : ISystemMediaSource
    {
        private IReadOnlyList<PlayerSession> _sessions = [];

        public string Name => "Linux media players (MPRIS)";
        public bool IsSupported => true;
        public bool IsRunning { get; private set; }
        public string StatusText => _sessions.Count == 1 ? "Watching 1 music player." : "No music player is open.";
        public IReadOnlyList<PlayerSession> Sessions => _sessions;
        public string? CurrentSessionId => null;
        public List<MediaCommand> Keys { get; } = [];

        public event Action? Changed;

        public void Set(params PlayerSession[] sessions)
        {
            _sessions = sessions;
            Changed?.Invoke();
        }

        public Task StartAsync(CancellationToken ct = default)
        {
            IsRunning = true;
            Changed?.Invoke();
            return Task.CompletedTask;
        }

        public Task StopAsync()
        {
            IsRunning = false;
            return Task.CompletedTask;
        }

        public Task<bool> ControlAsync(string sessionId, MediaCommand command, CancellationToken ct = default)
        {
            Keys.Add(command);
            return Task.FromResult(true);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
    }
}
