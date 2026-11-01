using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using Cuebot.Integrations.Common;
using Cuebot.Integrations.Spotify;
using static Cuebot.Integrations.Common.Steps;

namespace Cuebot.Integrations.NowPlaying;

/// <summary>The "Song changed" trigger. Type ids are stored in actions.json and must never change.</summary>
internal sealed class NowPlayingTriggers : ITriggerProvider
{
    public IEnumerable<ITriggerType> GetTriggers() =>
    [
        Trigger(NowPlayingService.Category, NowPlayingService.SongChangedEvent, "Song changed",
            [new("player", "Player") { Help = "Only songs from this player (part of its name, e.g. spotify); empty for any" }],
            (e, s) => s["player"].Trim() is not { Length: > 0 } wanted || e.Text("player").Contains(wanted, StringComparison.OrdinalIgnoreCase)),
    ];
}

/// <summary>Now playing steps. Type ids are stored in actions.json and must never change.</summary>
internal static class NowPlayingSubActions
{
    public static readonly string[] TypeIds = ["nowplaying.get", "nowplaying.mediaControl"];

    public static IReadOnlyList<ISubActionHandler> Create(NowPlayingService nowPlaying) =>
    [
        Make("nowplaying.get", "Get now playing", [], async (ctx, _, ct) =>
        {
            var current = await nowPlaying.GetCurrentAsync(ct).ConfigureAwait(false);
            foreach (var (key, value) in NowPlayingService.SongArgs(current))
                if (key.StartsWith("nowPlaying.", StringComparison.OrdinalIgnoreCase)) ctx.Args[key] = value;
            ctx.Args["nowPlaying.found"] = current is not null;
        }),
        Make("nowplaying.mediaControl", "Media control",
            [
                new("command", "Button", ParameterKind.Choice)
                {
                    DefaultValue = nameof(MediaCommand.PlayPause), Choices = Enum.GetNames<MediaCommand>(),
                    ChoiceLabels = new Dictionary<string, string>
                    {
                        [nameof(MediaCommand.PlayPause)] = "Play / pause", [nameof(MediaCommand.Next)] = "Next song",
                        [nameof(MediaCommand.Previous)] = "Previous song",
                    },
                },
                new("player", "Player") { Help = "Part of the player's name, e.g. vlc; empty for the one now playing" },
            ],
            async (ctx, v, ct) =>
            {
                var command = Enum.TryParse<MediaCommand>(v["command"].Trim().Replace(" ", "").Replace("/", ""), true, out var c)
                    ? c : throw new ArgumentException($"Unknown button '{v["command"]}'.");
                var target = await nowPlaying.ControlAsync(command, v["player"], ct).ConfigureAwait(false);
                ctx.Args["nowPlaying.player"] = target.Name;
            }),
    ];

    private static ISubActionHandler Make(string typeId, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) =>
        new IntegrationSubAction(typeId, NowPlayingService.Category, name, parameters, run);
}

/// <summary>Spotify steps. Type ids are stored in actions.json and must never change.</summary>
internal static class SpotifySubActions
{
    public static readonly string[] TypeIds = ["spotify.addToQueue", "spotify.skip", "spotify.previous", "spotify.playPause", "spotify.setVolume"];

    public static IReadOnlyList<ISubActionHandler> Create(SpotifyService spotify) =>
    [
        new AddToQueueStep(spotify),
        Make("spotify.skip", "Skip song", [], (_, _, ct) => spotify.NextAsync(ct)),
        Make("spotify.previous", "Previous song", [], (_, _, ct) => spotify.PreviousAsync(ct)),
        Make("spotify.playPause", "Pause / resume",
            [
                new("mode", "Do", ParameterKind.Choice)
                {
                    DefaultValue = "Toggle", Choices = ["Toggle", "Pause", "Resume"],
                    ChoiceLabels = new Dictionary<string, string> { ["Toggle"] = "Pause if playing, play if paused" },
                },
            ],
            async (ctx, v, ct) =>
            {
                switch (v["mode"].Trim().ToLowerInvariant())
                {
                    case "pause": await spotify.PauseAsync(ct).ConfigureAwait(false); ctx.Args["spotify.playing"] = false; break;
                    case "resume" or "play": await spotify.ResumeAsync(ct).ConfigureAwait(false); ctx.Args["spotify.playing"] = true; break;
                    default: ctx.Args["spotify.playing"] = await spotify.TogglePlayAsync(ct).ConfigureAwait(false); break;
                }
            }),
        Make("spotify.setVolume", "Set volume",
            [new("volume", "Volume (0-100)") { DefaultValue = "50" }],
            (_, v, ct) =>
            {
                var volume = Number(v["volume"], "Volume", 50);
                return spotify.SetVolumeAsync((int)Math.Round(volume), ct);
            }),
    ];

    private static ISubActionHandler Make(string typeId, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) =>
        new IntegrationSubAction(typeId, SpotifyService.Category, name, parameters, run);

    /// <summary>Song requests: never fails the action; runs "Added" or "Not added" (with %spotify.error%) instead.</summary>
    internal sealed class AddToQueueStep(SpotifyService spotify) : ISubActionHandler
    {
        public const string Added = "Added";
        public const string NotAdded = "Not added";

        public string TypeId => "spotify.addToQueue";
        public string Category => SpotifyService.Category;
        public string DisplayName => "Add song to Spotify queue";
        public IReadOnlyList<string> Branches { get; } = [Added, NotAdded];

        public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
            [new("song", "Song") { DefaultValue = "%rawInput%", Help = "A song name to search for, or a Spotify song link" }];

        public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
        {
            foreach (var key in new[] { "title", "artist", "album", "uri", "link", "error" }) context.Args["spotify." + key] = "";
            try
            {
                var result = await spotify.AddToQueueAsync(parameters["song"], ct).ConfigureAwait(false);
                context.Args["spotify.added"] = true;
                context.Args["spotify.title"] = result.Track.Title;
                context.Args["spotify.artist"] = result.Track.Artist;
                context.Args["spotify.album"] = result.Track.Album;
                context.Args["spotify.uri"] = result.Track.Uri;
                context.Args["spotify.link"] = result.Track.Link;
                return SubActionResult.TakeBranch(Added);
            }
            catch (Exception ex) when (ex is SpotifyException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                var message = ex is SpotifyException ? ex.Message : "Cannot reach Spotify right now. Try again in a minute.";
                context.Args["spotify.added"] = false;
                context.Args["spotify.error"] = message;
                context.Log.Warn(SpotifyService.Category, $"Song request not added: {message}");
                return SubActionResult.TakeBranch(NotAdded);
            }
        }
    }
}

/// <summary>Now playing and Spotify for user scripts.</summary>
internal sealed class NowPlayingScriptFunctions(NowPlayingService nowPlaying, SpotifyService spotify) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("nowplaying.get", "The song now playing: {found, title, artist, album, player, artUrl, duration, link, playing}", [], async (_, _, ct) =>
        {
            var current = await nowPlaying.GetCurrentAsync(ct).ConfigureAwait(false);
            var song = current?.Song ?? Song.None;
            return new
            {
                found = current is not null, title = song.Title, artist = song.Artist, album = song.Album, player = current?.Name ?? "",
                artUrl = song.ArtUrl, duration = song.Duration?.TotalSeconds ?? 0, link = song.Link, playing = current?.IsPlaying ?? false,
            };
        }),
        new("nowplaying.control", "Press a media key: command PlayPause, Play, Pause, Next or Previous; optional player name",
            ["command", "player"], async (_, a, ct) =>
            {
                var command = Enum.Parse<MediaCommand>(a.GetString("command", nameof(MediaCommand.PlayPause)), ignoreCase: true);
                return (await nowPlaying.ControlAsync(command, a.GetString("player"), ct).ConfigureAwait(false)).Name;
            }),
        new("spotify.isSignedIn", "True when Cuebot is signed in to Spotify", [], (_, _, _) => Task.FromResult<object?>(spotify.IsSignedIn)),
        new("spotify.addToQueue", "Add a song (name or Spotify link) to the queue; returns {title, artist, uri, link}", ["song"], async (_, a, ct) =>
        {
            var r = await spotify.AddToQueueAsync(a.GetString("song"), ct).ConfigureAwait(false);
            return new { title = r.Track.Title, artist = r.Track.Artist, uri = r.Track.Uri, link = r.Track.Link };
        }),
    ];
}
