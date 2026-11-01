using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Cuebot.Core.Desktop.DBus;
using Cuebot.Core.Logging;
using Cuebot.Integrations.NowPlaying;
using Cuebot.Integrations.NowPlaying.Mpris;
using Cuebot.Integrations.NowPlaying.Smtc;
using Cuebot.Integrations.Spotify;
using Cuebot.Integrations.Tests.Support;

namespace Cuebot.Integrations.Tests;

public sealed class MprisParserTests
{
    /// <summary>Through the real wire format, so values look exactly like the bus delivers them.</summary>
    internal static DBusMessage Wire(DBusMessage message) => DBusMessage.Parse(message.Serialize());

    internal static Dictionary<string, DBusVariant> Metadata(string title, string[] artists, string album = "", long lengthUs = 0, string art = "")
    {
        var map = new Dictionary<string, DBusVariant>
        {
            ["mpris:trackid"] = new("o", "/org/mpris/MediaPlayer2/track/1"),
            ["xesam:title"] = new("s", title),
            ["xesam:artist"] = new("as", artists),
        };
        if (album.Length > 0) map["xesam:album"] = new("s", album);
        if (lengthUs > 0) map["mpris:length"] = new("x", lengthUs);
        if (art.Length > 0) map["mpris:artUrl"] = new("s", art);
        return map;
    }

    [Fact]
    public void Reads_metadata_as_the_bus_sends_it()
    {
        var reply = Wire(new DBusMessage
        {
            Type = DBusMessageType.MethodReturn, ReplySerial = 1, Signature = "a{sv}",
            Body = [new Dictionary<string, DBusVariant>
            {
                ["Metadata"] = new("a{sv}", Metadata("Never Gonna Give You Up", ["Rick Astley", "Friends"], "Whenever You Need Somebody", 213_000_000, "https://i.scdn.co/image/x")),
                ["PlaybackStatus"] = new("s", "Playing"),
            }],
        });
        var props = (System.Collections.IDictionary)reply.Body[0]!;
        var song = MprisParser.ParseMetadata(props["Metadata"]);
        Assert.Equal("Never Gonna Give You Up", song.Title);
        Assert.Equal("Rick Astley, Friends", song.Artist);
        Assert.Equal("Whenever You Need Somebody", song.Album);
        Assert.Equal("https://i.scdn.co/image/x", song.ArtUrl);
        Assert.Equal(TimeSpan.FromSeconds(213), song.Duration);
        Assert.Equal("3:33", song.DurationText);
        Assert.Equal(PlaybackState.Playing, MprisParser.ParseStatus(props["PlaybackStatus"]));
    }

    [Fact]
    public void Odd_players_still_give_a_song()
    {
        // uint64 length, a single artist string, no title but a file URL.
        var map = new Dictionary<object, object?>
        {
            ["xesam:url"] = new DBusVariant("s", "file:///home/me/Music/Artist%20-%20Song.mp3"),
            ["xesam:artist"] = new DBusVariant("s", "Solo"),
            ["mpris:length"] = new DBusVariant("t", 90_000_000UL),
        };
        var song = MprisParser.ParseMetadata(new DBusVariant("a{sv}", map));
        Assert.Equal("Artist - Song", song.Title);
        Assert.Equal("Solo", song.Artist);
        Assert.Equal(TimeSpan.FromSeconds(90), song.Duration);
        Assert.Same(Song.None, MprisParser.ParseMetadata(null));
        Assert.Equal(PlaybackState.Paused, MprisParser.ParseStatus(new DBusVariant("s", "Paused")));
        Assert.Equal(PlaybackState.Stopped, MprisParser.ParseStatus("Stopped"));
        Assert.Equal(PlaybackState.Stopped, MprisParser.ParseStatus(null));
    }

    [Theory]
    [InlineData("org.mpris.MediaPlayer2.spotify", "spotify")]
    [InlineData("org.mpris.MediaPlayer2.chromium.instance12345", "chromium")]
    [InlineData("org.mpris.MediaPlayer2.firefox.instance_1_42", "firefox")]
    [InlineData("org.mpris.MediaPlayer2.vlc", "vlc")]
    public void Names_players_from_their_bus_name(string bus, string name)
    {
        Assert.True(MprisParser.IsPlayerBusName(bus));
        Assert.Equal(name, MprisParser.NameFromBus(bus));
    }

    [Fact]
    public void Ignores_other_bus_names()
    {
        Assert.False(MprisParser.IsPlayerBusName("org.mpris.MediaPlayer2."));
        Assert.False(MprisParser.IsPlayerBusName("org.freedesktop.Notifications"));
        Assert.False(MprisParser.IsPlayerBusName(null));
    }
}

/// <summary>An in-memory session bus with MPRIS players.</summary>
internal sealed class FakeMprisBus : IMprisBus
{
    public sealed class Player(string uniqueName, string identity)
    {
        public string UniqueName { get; } = uniqueName;
        public string Identity { get; } = identity;
        public Dictionary<string, DBusVariant> Metadata { get; set; } = [];
        public string Status { get; set; } = "Playing";
    }

    public ConcurrentDictionary<string, Player> Players { get; } = new();
    public ConcurrentQueue<DBusMessage> Calls { get; } = new();
    public List<string> Matches { get; } = [];
    public bool Disposed { get; private set; }

    public event Action<DBusMessage>? Signal;
    public event Action<Exception?>? Closed;

    public Task<DBusMessage> CallAsync(DBusMessage call, CancellationToken ct)
    {
        Calls.Enqueue(call);
        DBusMessage Reply(string signature, params object?[] body) =>
            MprisParserTests.Wire(new DBusMessage { Type = DBusMessageType.MethodReturn, ReplySerial = 1, Signature = signature, Body = body });
        switch (call.Member)
        {
            case "AddMatch":
                lock (Matches) Matches.Add((string)call.Body[0]!);
                return Task.FromResult(Reply(""));
            case "ListNames":
                return Task.FromResult(Reply("as", new List<string>(["org.freedesktop.DBus", .. Players.Keys])));
            case "GetNameOwner":
                return Players.TryGetValue((string)call.Body[0]!, out var owner)
                    ? Task.FromResult(Reply("s", owner.UniqueName))
                    : throw new DBusException("org.freedesktop.DBus.Error.NameHasNoOwner", "no owner");
        }
        if (!Players.TryGetValue(call.Destination ?? "", out var player)) throw new DBusException("org.freedesktop.DBus.Error.ServiceUnknown", "gone");
        return call.Member switch
        {
            "GetAll" => Task.FromResult(Reply("a{sv}", new Dictionary<string, DBusVariant>
            {
                ["Metadata"] = new("a{sv}", player.Metadata), ["PlaybackStatus"] = new("s", player.Status), ["CanGoNext"] = new("b", true),
            })),
            "Get" => Task.FromResult(Reply("v", new DBusVariant("s", player.Identity))),
            _ => Task.FromResult(Reply("")),
        };
    }

    public void PropertiesChanged(string bus, Dictionary<string, DBusVariant> changed, params string[] invalidated) =>
        Signal?.Invoke(MprisParserTests.Wire(new DBusMessage
        {
            Type = DBusMessageType.Signal, Sender = Players[bus].UniqueName, Path = MprisParser.ObjectPath,
            Interface = MprisParser.PropertiesInterface, Member = "PropertiesChanged", Signature = "sa{sv}as",
            Body = [MprisParser.PlayerInterface, changed, invalidated],
        }));

    public void NameOwnerChanged(string name, string oldOwner, string newOwner) =>
        Signal?.Invoke(MprisParserTests.Wire(new DBusMessage
        {
            Type = DBusMessageType.Signal, Sender = "org.freedesktop.DBus", Path = "/org/freedesktop/DBus", Interface = "org.freedesktop.DBus",
            Member = "NameOwnerChanged", Signature = "sss", Body = [name, oldOwner, newOwner],
        }));

    public void Drop() => Closed?.Invoke(new IOException("bus went away"));

    public void Dispose() => Disposed = true;
}

public sealed class MprisSourceTests
{
    private const string Spotify = "org.mpris.MediaPlayer2.spotify";
    private const string Vlc = "org.mpris.MediaPlayer2.vlc";

    [Fact]
    public async Task Lists_players_follows_signals_and_sends_media_keys()
    {
        var bus = new FakeMprisBus();
        bus.Players[Spotify] = new FakeMprisBus.Player(":1.10", "Spotify")
        {
            Metadata = MprisParserTests.Metadata("Song A", ["Band"], "Album", 200_000_000),
        };
        await using var source = new MprisSource(new AppLog(), _ => Task.FromResult<IMprisBus>(bus), TimeSpan.FromMilliseconds(20));
        var changes = 0;
        source.Changed += () => Interlocked.Increment(ref changes);
        await source.StartAsync();
        await Wait.UntilAsync(() => source.Sessions.Count == 1, "first player");
        var session = source.Sessions[0];
        Assert.Equal(Spotify, session.Id);
        Assert.Equal("Spotify", session.Name);
        Assert.Equal("Song A", session.Song.Title);
        Assert.True(session.IsPlaying);
        Assert.Contains(MprisParser.PropertiesRule, bus.Matches);
        Assert.Contains(MprisParser.NameOwnerRule, bus.Matches);
        Assert.Equal("Watching 1 music player.", source.StatusText);

        // A new song and a pause arrive as PropertiesChanged from the player's unique name.
        bus.PropertiesChanged(Spotify, new() { ["Metadata"] = new("a{sv}", MprisParserTests.Metadata("Song B", ["Band"])) });
        await Wait.UntilAsync(() => source.Sessions[0].Song.Title == "Song B", "song B");
        bus.PropertiesChanged(Spotify, new() { ["PlaybackStatus"] = new("s", "Paused") });
        await Wait.UntilAsync(() => source.Sessions[0].State == PlaybackState.Paused, "pause");
        Assert.Equal("Song B", source.Sessions[0].Song.Title);

        // Invalidated properties are read again.
        bus.Players[Spotify].Metadata = MprisParserTests.Metadata("Song C", ["Band"]);
        bus.PropertiesChanged(Spotify, [], "Metadata");
        await Wait.UntilAsync(() => source.Sessions[0].Song.Title == "Song C", "reloaded song");

        // A player starting and one leaving.
        bus.Players[Vlc] = new FakeMprisBus.Player(":1.20", "VLC media player") { Metadata = MprisParserTests.Metadata("Live", ["DJ"]) };
        bus.NameOwnerChanged(Vlc, "", ":1.20");
        await Wait.UntilAsync(() => source.Sessions.Count == 2, "vlc");
        bus.Players.TryRemove(Spotify, out _);
        bus.NameOwnerChanged(Spotify, ":1.10", "");
        await Wait.UntilAsync(() => source.Sessions.Count == 1 && source.Sessions[0].Name == "VLC media player", "spotify gone");

        Assert.True(await source.ControlAsync(Vlc, MediaCommand.Next));
        Assert.Contains(bus.Calls, c => c is { Member: "Next", Destination: Vlc, Interface: MprisParser.PlayerInterface, Path: MprisParser.ObjectPath });
        Assert.True(await source.ControlAsync(Vlc, MediaCommand.PlayPause));
        Assert.Contains(bus.Calls, c => c.Member == "PlayPause");
        Assert.False(await source.ControlAsync(Spotify, MediaCommand.Next));   // gone
        Assert.True(changes > 3);
    }

    [Fact]
    public async Task Reconnects_when_the_bus_drops()
    {
        var connects = 0;
        FakeMprisBus? current = null;
        await using var source = new MprisSource(new AppLog(), _ =>
        {
            Interlocked.Increment(ref connects);
            current = new FakeMprisBus();
            current.Players[Vlc] = new FakeMprisBus.Player(":1.5", "VLC") { Metadata = MprisParserTests.Metadata("X", ["Y"]) };
            return Task.FromResult<IMprisBus>(current);
        }, TimeSpan.FromMilliseconds(20));
        await source.StartAsync();
        await Wait.UntilAsync(() => source.Sessions.Count == 1, "first session");
        var first = current!;
        first.Drop();
        await Wait.UntilAsync(() => connects >= 2 && source.Sessions.Count == 1, "reconnect");
        Assert.True(first.Disposed);
        await source.StopAsync();
        Assert.Empty(source.Sessions);
        Assert.False(source.IsRunning);
    }

    [Fact]
    public async Task Keeps_trying_when_there_is_no_bus()
    {
        var attempts = 0;
        var log = new AppLog();
        await using var source = new MprisSource(log, _ =>
        {
            Interlocked.Increment(ref attempts);
            throw new InvalidOperationException("No D-Bus session bus");
        }, TimeSpan.FromMilliseconds(10));
        await source.StartAsync();
        await Wait.UntilAsync(() => attempts >= 3, "retries");
        Assert.Contains("keeps trying", source.StatusText);
        Assert.Single(log.Recent(), e => e.Message.Contains("No D-Bus session bus"));   // logged once, not every retry
    }
}

public sealed class SmtcTests
{
    [Fact]
    public void Parses_the_helper_session_list()
    {
        var line = """
            {"type":"sessions","current":"Spotify.exe","sessions":[
              {"id":"Spotify.exe","status":4,"title":"Song","artist":"","album":"LP","albumArtist":"Band","durationMs":185000},
              {"id":"Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic","status":5,"title":"Other","artist":"Someone","album":"","albumArtist":"","durationMs":0},
              {"id":"308046B0AF4A39CB","status":3,"title":null,"artist":null,"album":null,"albumArtist":null,"durationMs":0}]}
            """.Replace("\n", "");
        var message = Assert.IsType<SmtcMessage>(SmtcProtocol.Parse(line));
        Assert.True(message.IsSessions);
        Assert.Equal("Spotify.exe", message.CurrentId);
        Assert.Equal(3, message.Sessions.Count);
        var spotify = message.Sessions[0];
        Assert.Equal("Spotify", spotify.Name);
        Assert.Equal(PlaybackState.Playing, spotify.State);
        Assert.Equal("Band", spotify.Song.Artist);   // album artist when the artist is empty
        Assert.Equal(TimeSpan.FromSeconds(185), spotify.Song.Duration);
        Assert.Equal("Media Player", message.Sessions[1].Name);
        Assert.Equal(PlaybackState.Paused, message.Sessions[1].State);
        Assert.Null(message.Sessions[1].Song.Duration);
        Assert.True(message.Sessions[2].Song.IsEmpty);
        Assert.Equal(PlaybackState.Stopped, message.Sessions[2].State);
    }

    [Fact]
    public void Parses_single_sessions_errors_and_ignores_noise()
    {
        var one = SmtcProtocol.Parse("""{"type":"sessions","current":"","sessions":{"id":"chrome","status":4,"title":"Video"}}""");
        Assert.Equal("Chrome", Assert.Single(one!.Sessions).Name);
        Assert.Empty(SmtcProtocol.Parse("""{"type":"sessions","current":"","sessions":[]}""")!.Sessions);
        var unsupported = SmtcProtocol.Parse("""{"type":"unsupported","message":"Unable to find type"}""")!;
        Assert.True(unsupported.IsUnsupported);
        Assert.Equal("Unable to find type", unsupported.Message);
        Assert.True(SmtcProtocol.Parse("""{"type":"error","message":"boom"}""")!.IsError);
        Assert.Null(SmtcProtocol.Parse("WARNING: something PowerShell printed"));
        Assert.Null(SmtcProtocol.Parse("{not json"));
    }

    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
    [InlineData("MSEdge", "Edge")]
    [InlineData("foobar2000.exe", "foobar2000")]
    [InlineData("SomeApp.exe", "SomeApp")]
    public void Gives_apps_friendly_names(string id, string name) => Assert.Equal(name, SmtcProtocol.FriendlyName(id));

    [Fact]
    public void Formats_commands_and_the_encoded_script()
    {
        Assert.Equal("next\tSpotify.exe", SmtcProtocol.Command(MediaCommand.Next, "Spotify.exe"));
        Assert.Equal("playpause\ta b", SmtcProtocol.Command(MediaCommand.PlayPause, "a\tb"));
        var script = SmtcProtocol.Script();
        Assert.Contains("GlobalSystemMediaTransportControlsSessionManager", script);
        Assert.Contains("ReadLineAsync", script);
        Assert.DoesNotContain(script.Split('\n'), l => l.TrimStart().StartsWith('#'));
        var args = SmtcProtocol.Arguments(script);
        Assert.StartsWith("-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand ", args);
        Assert.Equal(script, Encoding.Unicode.GetString(Convert.FromBase64String(args.Split(' ')[^1])));
        Assert.True(args.Length < 30_000, "Windows command lines stop at 32767 characters");
    }

    private sealed class FakeHelper : ILineProcess
    {
        public Channel<string?> Output { get; } = Channel.CreateUnbounded<string?>();
        public ConcurrentQueue<string> Input { get; } = new();
        public bool Killed { get; private set; }

        public async Task<string?> ReadLineAsync(CancellationToken ct) => await Output.Reader.ReadAsync(ct);

        public Task WriteLineAsync(string line, CancellationToken ct)
        {
            Input.Enqueue(line);
            return Task.CompletedTask;
        }

        public void Kill()
        {
            Killed = true;
            Output.Writer.TryWrite(null);
        }

        public void Dispose() => Kill();
    }

    [Fact]
    public async Task Runs_the_helper_restarts_it_and_sends_keys()
    {
        var helpers = new ConcurrentQueue<FakeHelper>();
        await using var source = new SmtcSource(new AppLog(), () =>
        {
            var helper = new FakeHelper();
            helpers.Enqueue(helper);
            return helper;
        }, TimeSpan.FromMilliseconds(10));
        await source.StartAsync();
        await Wait.UntilAsync(() => helpers.Count == 1, "helper started");
        var first = helpers.Single();
        first.Output.Writer.TryWrite("""{"type":"sessions","current":"Spotify.exe","sessions":[{"id":"Spotify.exe","status":4,"title":"A","artist":"B"}]}""");
        await Wait.UntilAsync(() => source.Sessions.Count == 1, "sessions");
        Assert.Equal("Spotify.exe", source.CurrentSessionId);
        Assert.True(await source.ControlAsync("Spotify.exe", MediaCommand.Next));
        Assert.Equal("next\tSpotify.exe", Assert.Single(first.Input));
        Assert.False(await source.ControlAsync("vlc.exe", MediaCommand.Next));

        first.Output.Writer.TryWrite(null);   // the helper ended by itself
        await Wait.UntilAsync(() => helpers.Count == 2, "restart");
        Assert.Empty(source.Sessions);
        var second = helpers.Last();
        await source.StopAsync();
        Assert.True(second.Killed);
        Assert.False(source.IsRunning);
    }

    [Fact]
    public async Task Stops_for_good_on_windows_without_media_controls()
    {
        var starts = 0;
        await using var source = new SmtcSource(new AppLog(), () =>
        {
            Interlocked.Increment(ref starts);
            var helper = new FakeHelper();
            helper.Output.Writer.TryWrite("""{"type":"unsupported","message":"no type"}""");
            return helper;
        }, TimeSpan.FromMilliseconds(5));
        await source.StartAsync();
        await Wait.UntilAsync(() => !source.IsSupported && !source.IsRunning, "gave up");
        await Task.Delay(50);
        Assert.Equal(1, starts);
        Assert.Contains("Windows 10 1809", source.StatusText);
    }
}

public sealed class SessionPickerTests
{
    private static PlayerSession S(string id, string name, PlaybackState state, string title = "T", bool spotify = false) =>
        new(id, name, state, new Song { Title = title, Artist = "A" }, spotify);

    [Fact]
    public void Automatic_prefers_what_plays()
    {
        var pausedVlc = S("vlc", "VLC", PlaybackState.Paused);
        var playingFox = S("firefox", "Firefox", PlaybackState.Playing);
        var playingMpv = S("mpv", "mpv", PlaybackState.Playing);
        var spotifyPlaying = S("spotify", "Spotify", PlaybackState.Playing, spotify: true);
        Assert.Same(playingFox, SessionPicker.Pick([pausedVlc, playingFox], null, null, NowPlayingSource.Automatic, ""));
        Assert.Same(playingMpv, SessionPicker.Pick([playingFox, playingMpv], "mpv", null, NowPlayingSource.Automatic, ""));   // the system's own pick
        Assert.Same(spotifyPlaying, SessionPicker.Pick([pausedVlc], null, spotifyPlaying, NowPlayingSource.Automatic, ""));
        Assert.Same(pausedVlc, SessionPicker.Pick([pausedVlc], null, null, NowPlayingSource.Automatic, ""));   // paused beats nothing
        Assert.Null(SessionPicker.Pick([S("x", "X", PlaybackState.Playing, title: "") with { Song = Song.None }], null, null, NowPlayingSource.Automatic, ""));
    }

    [Fact]
    public void Player_and_spotify_sources_only_take_that_one()
    {
        var vlc = S("org.mpris.MediaPlayer2.vlc", "VLC media player", PlaybackState.Paused);
        var fox = S("firefox", "Firefox", PlaybackState.Playing);
        var spotify = S("spotify", "Spotify", PlaybackState.Paused, spotify: true);
        Assert.Same(vlc, SessionPicker.Pick([vlc, fox], null, spotify, NowPlayingSource.Player, "vlc"));
        Assert.Same(spotify, SessionPicker.Pick([vlc, fox], null, spotify, NowPlayingSource.Player, "SPOTIFY"));
        Assert.Null(SessionPicker.Pick([vlc, fox], null, spotify, NowPlayingSource.Player, ""));
        Assert.Same(spotify, SessionPicker.Pick([vlc, fox], null, spotify, NowPlayingSource.Spotify, ""));
        Assert.Null(SessionPicker.Pick([vlc, fox], null, null, NowPlayingSource.Spotify, ""));
    }
}

/// <summary>A system source the tests drive by hand.</summary>
internal sealed class FakeSystemSource : ISystemMediaSource
{
    private IReadOnlyList<PlayerSession> _sessions = [];

    public string Name => "Fake players";
    public bool IsSupported => true;
    public bool IsRunning { get; private set; }
    public string StatusText => "fake";
    public IReadOnlyList<PlayerSession> Sessions => _sessions;
    public string? CurrentSessionId => null;
    public ConcurrentQueue<(string Id, MediaCommand Command)> Keys { get; } = new();

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
        Keys.Enqueue((sessionId, command));
        return Task.FromResult(_sessions.Any(s => s.Id == sessionId));
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class NowPlayingServiceTests : IDisposable
{
    private readonly TestEnvironment _env = new();
    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-nowplaying-").FullName;

    private (NowPlayingService Service, FakeSystemSource System) Create()
    {
        var system = new FakeSystemSource();
        var spotify = new SpotifyService(_env.Settings, _env.Log, new HttpClient(new FakeHttpHandler(_ => FakeHttpHandler.Empty())));
        return (new NowPlayingService(_env.Settings, _env.Bus, _env.Log, spotify, system, _dir, TimeSpan.FromMilliseconds(20)), system);
    }

    private static PlayerSession Playing(string title, string artist = "Band", PlaybackState state = PlaybackState.Playing) =>
        new("vlc", "VLC", state, new Song { Title = title, Artist = artist, Album = "LP", Duration = TimeSpan.FromSeconds(225) });

    [Fact]
    public async Task Announces_each_new_song_once_and_writes_the_text_file()
    {
        var (service, system) = Create();
        await using var _ = service;
        service.Settings.Enabled = true;
        service.Settings.WriteTextFile = true;
        service.Settings.TextFormat = "Now: %title% by %artist% (%duration%)";
        service.Settings.IdleText = "Nothing";
        await service.StartAsync();
        Assert.Equal(Core.ConnectionState.Connected, service.State);

        system.Set(Playing("Intro"));
        await Wait.UntilAsync(() => _env.Events(NowPlayingService.SongChangedEvent).Length == 1, "first song");
        var e = _env.Events(NowPlayingService.SongChangedEvent)[0];
        Assert.Equal("Intro", e.Args["title"]);
        Assert.Equal("Band", e.Args["artist"]);
        Assert.Equal("LP", e.Args["album"]);
        Assert.Equal("VLC", e.Args["player"]);
        Assert.Equal(225L, e.Args["duration"]);
        Assert.Equal("3:45", e.Args["durationText"]);
        Assert.Equal("system", e.Args["source"]);
        Assert.Equal("Intro", e.Args["nowPlaying.title"]);
        Assert.Equal("Now: Intro by Band (3:45)", await File.ReadAllTextAsync(service.TextFilePath));
        Assert.Equal(Path.Combine(_dir, NowPlayingService.DefaultFileName), service.TextFilePath);

        // Pausing and playing the same song again announces nothing new; the file shows the idle text meanwhile.
        system.Set(Playing("Intro", state: PlaybackState.Paused));
        Assert.Equal("Nothing", await File.ReadAllTextAsync(service.TextFilePath));
        system.Set(Playing("Intro"));
        await Task.Delay(80);
        Assert.Single(_env.Events(NowPlayingService.SongChangedEvent));

        // A song sent in pieces (file name first) is announced once, settled.
        system.Set(Playing("track01.mp3", ""));
        system.Set(Playing("Second", "Other"));
        await Wait.UntilAsync(() => _env.Events(NowPlayingService.SongChangedEvent).Length == 2, "second song");
        await Task.Delay(80);
        Assert.Equal("Second", Assert.Single(_env.Events(NowPlayingService.SongChangedEvent), x => x.Args["title"] as string != "Intro").Args["title"]);
    }

    [Fact]
    public async Task Says_nothing_while_not_watching_and_get_starts_watching()
    {
        var (service, system) = Create();
        await using var _ = service;
        await service.StartAsync();   // not enabled
        Assert.False(system.IsRunning);
        system.Set(Playing("Quiet"));
        await Task.Delay(60);
        Assert.Empty(_env.Events(NowPlayingService.SongChangedEvent));

        var current = await service.GetCurrentAsync();
        Assert.True(system.IsRunning);
        Assert.Equal("Quiet", current!.Song.Title);

        var target = await service.ControlAsync(MediaCommand.Next);
        Assert.Equal("VLC", target.Name);
        Assert.Equal(("vlc", MediaCommand.Next), Assert.Single(system.Keys));
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ControlAsync(MediaCommand.Next, "winamp"));
    }

    [Fact]
    public void Formats_the_file_text()
    {
        var song = Playing("Title", "");
        Assert.Equal("Title", NowPlayingService.FormatText("%artist% - %title%", song, ""));
        Assert.Equal("idle", NowPlayingService.FormatText("%title%", song with { State = PlaybackState.Paused }, "idle"));
        Assert.Equal("", NowPlayingService.FormatText("%title%", null, ""));
        Assert.Equal("Title on VLC", NowPlayingService.FormatText("%TITLE% on %player%", song, ""));
    }

    public void Dispose()
    {
        _env.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }
}
