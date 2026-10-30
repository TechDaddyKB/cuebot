using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Media;
using Cuebot.Core.Overlays;
using Cuebot.Core.Points;
using Cuebot.Core.Variables;
using Cuebot.Tests.ChatExtras;

namespace Cuebot.Tests.Media;

public sealed class MediaRequestsTests : IDisposable
{
    private const string A = "dQw4w9WgXcQ";
    private const string B = "M7lc1UVf-VE";
    private const string C = "9bZkp7q19f0";

    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly OverlayFeed _feed;
    private readonly ManualClock _clock = new();
    private readonly PointsService _points;
    private readonly TempDir _dir = new();
    private readonly FakeLookup _lookup = new();
    private readonly List<JsonObject> _sent = [];
    private readonly List<BotEvent> _events = [];

    public MediaRequestsTests()
    {
        _bus = new EventBus(_log);
        _feed = new OverlayFeed(_log);
        _feed.Subscribe(m => _sent.Add(JsonNode.Parse(m.Json)!.AsObject()));
        _bus.Subscribe(e => { if (e.Type.StartsWith("cuebot.media", StringComparison.Ordinal)) _events.Add(e); });
        _points = new PointsService(new UserVariables(null, _log, _clock), null, _bus, _log, time: _clock);
    }

    public void Dispose()
    {
        _points.Dispose();
        _dir.Dispose();
    }

    private MediaRequests Create(AppPaths? paths = null) => new(paths, _log, _bus, _feed, _lookup, _points, time: _clock);

    private void Chat(string platform, string user, string text, bool moderator = false) =>
        _bus.Publish(new BotEvent(ChatHub.MessageEvent, new Dictionary<string, object?>
        {
            ["platform"] = platform, ["user"] = user, ["userId"] = user.ToLowerInvariant() + "-id", ["message"] = text, ["isModerator"] = moderator,
        }));

    private static async Task Until(Func<bool> condition)
    {
        for (var i = 0; i < 200 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition());
    }

    [Theory]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", A)]
    [InlineData("https://youtube.com/watch?feature=share&v=dQw4w9WgXcQ&t=42", A)]
    [InlineData("https://youtu.be/dQw4w9WgXcQ?si=abc", A)]
    [InlineData("youtube.com/shorts/dQw4w9WgXcQ", A)]
    [InlineData("https://music.youtube.com/watch?v=dQw4w9WgXcQ", A)]
    [InlineData("https://www.youtube.com/embed/dQw4w9WgXcQ", A)]
    [InlineData("dQw4w9WgXcQ", A)]
    [InlineData("never gonna give you up", null)]
    [InlineData("https://vimeo.com/123", null)]
    public void Links_give_their_video_id(string text, string? id) => Assert.Equal(id, MediaRequests.VideoId(text));

    [Fact]
    public async Task Requests_from_every_platform_queue_up_and_play_in_turn()
    {
        using var media = Create();
        media.UpdateSettings(s => s.MaxPerViewer = 1);
        Chat("Kick", "Ann", "!sr https://youtu.be/" + A);
        await Until(() => media.Current is not null);
        Assert.Equal(("Song A", "Ann", "Kick"), (media.Current!.Title, media.Current.UserName, media.Current.Platform));
        Assert.Single(_events, e => e.Type == MediaEvents.Started);

        Chat("Rumble", "Bob", "!sr " + B);
        await Until(() => media.Queue.Count == 1);
        Assert.Equal("that one is already in the queue", (await media.RequestAsync(B, "YouTube", "cy-id", "Cy")).Error);
        Assert.Equal("you already have 1 in the queue", (await media.RequestAsync(C, "Kick", "ann-id", "Ann")).Error);
        Assert.Equal("that video cannot be played here (private, removed or not embeddable)", (await media.RequestAsync("xxxxxxxxxxx", "Kick", "x", "X")).Error);
        Assert.Equal("that is not a YouTube link", (await media.RequestAsync("a song please", "Kick", "x", "X")).Error);
        var state = _sent.Last(m => m["listener"]!.GetValue<string>() == MediaRequests.Listener)["event"]!;
        Assert.Equal((A, 1, "Song B"), (state["current"]!["videoId"]!.GetValue<string>(), state["queueLength"]!.GetValue<int>(),
            state["queue"]![0]!["title"]!.GetValue<string>()));

        Assert.False(media.Ended("not-playing"));           // a late report from a second player changes nothing
        Assert.True(media.Ended(media.Current.Id));
        Assert.Equal("Song B", media.Current!.Title);
        Assert.Equal(["Song A"], media.History.Select(h => h.Title));
        Chat("Rumble", "Bob", "!skip");                      // not a moderator
        Assert.Equal("Song B", media.Current!.Title);
        Chat("Twitch", "Mod", "!skip", moderator: true);
        Assert.Null(media.Current);
        Assert.Single(_events, e => e.Type == MediaEvents.Skipped);
    }

    [Fact]
    public async Task Paid_requests_are_refunded_when_taken_back_removed_or_cleared()
    {
        _points.Add("Twitch", "ann-id", "Ann", 1000);
        using var media = Create();
        media.UpdateSettings(s => { s.Cost = 100; s.MaxPerViewer = 3; });
        media.SetPaused(true);                               // nothing starts while paused
        Assert.Equal("it costs 100 points", (await media.RequestAsync(A, "Kick", "nobody", "Nobody")).Error);
        foreach (var id in new[] { A, B, C }) await media.RequestAsync(id, "Twitch", "ann-id", "Ann");
        Assert.Equal((700L, 3), (_points.Get("Twitch", "ann-id"), media.Queue.Count));
        Assert.Null(media.Current);

        Chat("Twitch", "Ann", "!wrongsong");                 // her latest: C
        Assert.Equal(800L, _points.Get("Twitch", "ann-id"));
        Assert.Equal(["Song A", "Song B"], media.Queue.Select(q => q.Title));
        Assert.True(media.Move(media.Queue[1].Id, -1));
        Assert.Equal("Song B", media.Queue[0].Title);
        media.Remove(media.Queue[0].Id);
        Assert.Equal(900, _points.Get("Twitch", "ann-id"));

        media.SetPaused(false);                              // resuming starts the queue
        Assert.Equal("Song A", media.Current!.Title);
        await media.RequestAsync(B, "Twitch", "ann-id", "Ann");
        Assert.Equal(1, media.Clear());
        Assert.Equal(900, _points.Get("Twitch", "ann-id"));   // the one playing is not refunded; the cleared one is
        Assert.Equal(("Song A", 0), (media.Current!.Title, media.Queue.Count));

        // YouTube will not play it here: refunded, and the queue moves on.
        await media.RequestAsync(C, "Twitch", "ann-id", "Ann");
        Assert.False(media.Failed(media.Queue[0].Id));       // only what plays
        media.Skip();
        Assert.Equal(800, _points.Get("Twitch", "ann-id"));
        Assert.True(media.Failed(media.Current!.Id));
        Assert.Equal((900L, null as MediaItem), (_points.Get("Twitch", "ann-id"), media.Current));
    }

    [Fact]
    public async Task Closed_or_subscriber_only_requests_and_a_restart()
    {
        var paths = new AppPaths(_dir.Path);
        var media = Create(paths);
        media.UpdateSettings(s => { s.Open = false; s.Command = "!song now"; });
        Assert.Equal("requests are closed right now", (await media.RequestAsync(A, "Kick", "a", "Ann")).Error);
        Assert.NotNull((await media.RequestAsync(A, "Kick", "a", "Ann", free: true)).Item);   // the streamer still can
        media.UpdateSettings(s => { s.Open = true; s.SubscribersOnly = true; });
        Assert.Equal("requests are for subscribers", (await media.RequestAsync(B, "Kick", "b", "Bob")).Error);
        await media.RequestAsync(B, "Kick", "b", "Bob", subscriber: true);
        Assert.Equal("!song", media.Settings.Command);
        media.Dispose();

        using var again = Create(paths);
        Assert.Equal(("Song A", 1), (again.Current!.Title, again.Queue.Count));
    }

    [Fact]
    public async Task OEmbed_finds_titles_and_refuses_what_cannot_be_embedded()
    {
        var http = new FakeHttp();
        http.Json("https://www.youtube.com/oembed?format=json&url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3D" + A,
            """{"title":"Never Gonna Give You Up","author_name":"Rick Astley"}""");
        var lookup = new YouTubeOEmbedLookup(http.Client());
        Assert.Equal(("Never Gonna Give You Up", "Rick Astley"), await lookup.FindAsync(A, default));
        Assert.Null(await lookup.FindAsync(B, default));     // 404 (or 401 for embedding off)
    }

    private sealed class FakeLookup : IMediaLookup
    {
        public Task<(string Title, string Author)?> FindAsync(string videoId, CancellationToken ct) =>
            Task.FromResult<(string, string)?>(videoId switch
            {
                A => ("Song A", "Artist A"),
                B => ("Song B", "Artist B"),
                C => ("Song C", "Artist C"),
                _ => null,
            });
    }
}
