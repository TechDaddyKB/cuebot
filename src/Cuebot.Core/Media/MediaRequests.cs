using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Overlays;
using Cuebot.Core.Points;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Media;

/// <summary>A requested video.</summary>
public sealed class MediaItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string VideoId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Author { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Platform { get; set; } = "";
    public string UserId { get; set; } = "";

    /// <summary>Points paid (refunded when it is removed before it plays).</summary>
    public long Cost { get; set; }
    public string Currency { get; set; } = "";
    public DateTimeOffset Time { get; set; }

    public string Url => "https://www.youtube.com/watch?v=" + VideoId;
}

/// <summary>How media requests work.</summary>
public sealed class MediaSettings
{
    /// <summary>Viewers may request (the queue keeps playing either way).</summary>
    public bool Open { get; set; } = true;
    public string Command { get; set; } = "!sr";

    /// <summary>Removes the viewer's own latest request (refunded).</summary>
    public string WrongCommand { get; set; } = "!wrongsong";

    /// <summary>Moderators skip what is playing.</summary>
    public string SkipCommand { get; set; } = "!skip";
    public long Cost { get; set; }
    public string Currency { get; set; } = "";

    /// <summary>Requests one viewer may have waiting or playing.</summary>
    public int MaxPerViewer { get; set; } = 2;

    /// <summary>The player moves on after this many seconds of one video (0: plays it all).</summary>
    public int MaxSeconds { get; set; } = 600;

    public int MaxQueue { get; set; } = 50;
    public bool SubscribersOnly { get; set; }
    public bool ReplyInChat { get; set; } = true;
    public int Volume { get; set; } = 50;
}

/// <summary>media-requests.json.</summary>
public sealed class MediaDocument
{
    public MediaSettings Settings { get; set; } = new();
    public MediaItem? Current { get; set; }
    public List<MediaItem> Queue { get; set; } = [];
    public List<MediaItem> History { get; set; } = [];
    public bool Paused { get; set; }
}

/// <summary>Looks a video up (title and channel); null when it does not exist or cannot be embedded.</summary>
public interface IMediaLookup
{
    Task<(string Title, string Author)?> FindAsync(string videoId, CancellationToken ct);
}

/// <summary>
/// YouTube's oEmbed endpoint: the title and channel without an API key or quota; private, removed and
/// non-embeddable videos answer with an error, so they are refused before anyone pays.
/// </summary>
public sealed class YouTubeOEmbedLookup(HttpClient http) : IMediaLookup
{
    public async Task<(string Title, string Author)?> FindAsync(string videoId, CancellationToken ct)
    {
        var url = "https://www.youtube.com/oembed?format=json&url=" + Uri.EscapeDataString("https://www.youtube.com/watch?v=" + videoId);
        using var response = await http.GetAsync(url, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        string Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        return (Text("title"), Text("author_name"));
    }
}

/// <summary>Events media requests publish.</summary>
public static class MediaEvents
{
    /// <summary>Args: requestId, title, author, videoId, url, user, platform, userId, cost, position (1 = next).</summary>
    public const string Requested = "cuebot.mediaRequested";

    /// <summary>Same args as <see cref="Requested"/>, for the video that started playing.</summary>
    public const string Started = "cuebot.mediaStarted";

    /// <summary>Same args, for a video skipped while playing.</summary>
    public const string Skipped = "cuebot.mediaSkipped";
}

/// <summary>
/// Media requests: viewers on every platform add YouTube videos to a queue with the request command (<c>!sr link</c>,
/// optionally paid in Cuebot points, limited per viewer and to subscribers if wanted), take back their latest with
/// <c>!wrongsong</c>, and moderators <c>!skip</c>. The Media request player widget plays the queue on an overlay and
/// reports when a video ends (<see cref="Ended"/>), which starts the next. Removing a waiting request refunds it.
/// Queue, settings and the latest played are kept in media-requests.json; the state goes to overlays as
/// <c>cuebot-media</c> (and in their hello as <c>media</c>).
/// </summary>
public sealed partial class MediaRequests : IDisposable
{
    public const string Listener = "cuebot-media";
    public const int HistorySize = 20;

    private readonly object _gate = new();
    private readonly JsonFile<MediaDocument>? _file;
    private readonly MediaDocument _memory = new();
    private readonly EventBus _bus;
    private readonly OverlayFeed _feed;
    private readonly IMediaLookup _lookup;
    private readonly PointsService? _points;
    private readonly ChatHub? _chat;
    private readonly AppLog _log;
    private readonly TimeProvider _time;
    private readonly IDisposable _subscription;

    public MediaRequests(AppPaths? paths, AppLog log, EventBus bus, OverlayFeed feed, IMediaLookup lookup, PointsService? points = null,
        ChatHub? chat = null, TimeProvider? time = null)
    {
        _log = log;
        _bus = bus;
        _feed = feed;
        _lookup = lookup;
        _points = points;
        _chat = chat;
        _time = time ?? TimeProvider.System;
        if (paths is not null) _file = new JsonFile<MediaDocument>(paths.File("media-requests.json"), log);
        _subscription = bus.Subscribe(OnEvent);
    }

    private MediaDocument Doc => _file?.Value ?? _memory;

    /// <summary>Raised (on the caller's thread) after any change.</summary>
    public event Action? Changed;

    /// <summary>A copy of the settings.</summary>
    public MediaSettings Settings
    {
        get { lock (_gate) return Copy(Doc.Settings); }
    }

    public MediaItem? Current
    {
        get { lock (_gate) return Doc.Current; }
    }

    public IReadOnlyList<MediaItem> Queue
    {
        get { lock (_gate) return Doc.Queue.ToArray(); }
    }

    public IReadOnlyList<MediaItem> History
    {
        get { lock (_gate) return Doc.History.ToArray(); }
    }

    public bool Paused
    {
        get { lock (_gate) return Doc.Paused; }
    }

    /// <summary>Changes settings (commands keep their first word; numbers are kept in range).</summary>
    public void UpdateSettings(Action<MediaSettings> edit)
    {
        lock (_gate)
        {
            var s = Copy(Doc.Settings);
            edit(s);
            s.Command = Word(s.Command, Doc.Settings.Command);
            s.WrongCommand = Word(s.WrongCommand, Doc.Settings.WrongCommand);
            s.SkipCommand = Word(s.SkipCommand, Doc.Settings.SkipCommand);
            s.Cost = Math.Max(0, s.Cost);
            s.MaxPerViewer = Math.Max(1, s.MaxPerViewer);
            s.MaxSeconds = Math.Max(0, s.MaxSeconds);
            s.MaxQueue = Math.Max(1, s.MaxQueue);
            s.Volume = Math.Clamp(s.Volume, 0, 100);
            s.Currency = s.Currency.Trim();
            Doc.Settings = s;
            Save();
        }
        Publish();
    }

    // ---- Requests ---------------------------------------------------------------------------------------------------

    /// <summary>A YouTube video id from a link (watch, youtu.be, shorts, embed, music) or a bare id; null otherwise.</summary>
    public static string? VideoId(string text)
    {
        text = text.Trim();
        if (BareId().IsMatch(text)) return text;
        var match = LinkId().Match(text);
        return match.Success ? match.Groups[1].Value : null;
    }

    [GeneratedRegex(@"^[A-Za-z0-9_-]{11}$")]
    private static partial Regex BareId();

    [GeneratedRegex(@"(?:youtube\.com/(?:watch\?(?:.*&)?v=|shorts/|embed/|live/)|youtu\.be/)([A-Za-z0-9_-]{11})", RegexOptions.IgnoreCase)]
    private static partial Regex LinkId();

    /// <summary>
    /// Adds a request: checks it, looks the video up, takes the points (unless <paramref name="free"/>, for requests
    /// added by the streamer or an action) and queues it, starting it at once when nothing plays. Returns the item, or
    /// the reason it was refused (in words for chat).
    /// </summary>
    public async Task<(MediaItem? Item, string Error)> RequestAsync(string text, string platform, string userId, string userName,
        bool subscriber = false, bool free = false, CancellationToken ct = default)
    {
        if (VideoId(text) is not { } videoId) return (null, "that is not a YouTube link");
        MediaSettings settings;
        lock (_gate)
        {
            settings = Copy(Doc.Settings);
            if (!free)
            {
                if (!settings.Open) return (null, "requests are closed right now");
                if (settings.SubscribersOnly && !subscriber) return (null, "requests are for subscribers");
                var mine = Doc.Queue.Count(i => Same(i, platform, userId)) + (Doc.Current is { } c && Same(c, platform, userId) ? 1 : 0);
                if (mine >= settings.MaxPerViewer) return (null, $"you already have {mine} in the queue");
            }
            if (Doc.Queue.Count >= settings.MaxQueue) return (null, "the queue is full");
            if (Doc.Queue.Any(i => i.VideoId == videoId) || Doc.Current?.VideoId == videoId) return (null, "that one is already in the queue");
        }
        (string Title, string Author)? found;
        try
        {
            found = await _lookup.FindAsync(videoId, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            _log.Warn("Media requests", $"Could not look up {videoId}: {ex.Message}");
            return (null, "YouTube could not be reached, try again");
        }
        if (found is not { } video) return (null, "that video cannot be played here (private, removed or not embeddable)");
        var cost = free ? 0 : settings.Cost;
        if (cost > 0 && (_points is null || !_points.TrySpend(platform, userId, userName, cost, out _, Currency(settings.Currency))))
            return (null, $"it costs {(_points?.Format(cost, Currency(settings.Currency)) ?? cost.ToString(CultureInfo.InvariantCulture))}");
        var item = new MediaItem
        {
            VideoId = videoId, Title = video.Title, Author = video.Author, UserName = userName, Platform = platform, UserId = userId,
            Cost = cost, Currency = settings.Currency, Time = _time.GetUtcNow(),
        };
        int position;
        bool start;
        bool late;
        lock (_gate)
        {
            // Checked again: another request may have landed while this one was being looked up.
            late = Doc.Queue.Count >= settings.MaxQueue || Doc.Queue.Any(i => i.VideoId == videoId) || Doc.Current?.VideoId == videoId;
            if (!late) Doc.Queue.Add(item);
            position = Doc.Queue.Count;
            start = Doc.Current is null && !Doc.Paused;
            if (!late) Save();
        }
        if (late)
        {
            Refund(item);
            return (null, "that one is already in the queue, or the queue is full");
        }
        var args = Args(item);
        args["position"] = start ? 0 : position;
        _bus.Publish(new BotEvent(MediaEvents.Requested, args));
        if (start) Next();
        else Publish();
        return (item, "");
    }

    /// <summary>Removes a waiting request and refunds it; false when it is not waiting.</summary>
    public bool Remove(string id)
    {
        MediaItem? item;
        lock (_gate)
        {
            item = Doc.Queue.FirstOrDefault(i => i.Id == id);
            if (item is null) return false;
            Doc.Queue.Remove(item);
            Save();
        }
        Refund(item);
        Publish();
        return true;
    }

    /// <summary>Removes a viewer's latest waiting request (refunded); null when they have none waiting.</summary>
    public MediaItem? RemoveLatest(string platform, string userId)
    {
        MediaItem? item;
        lock (_gate) item = Doc.Queue.LastOrDefault(i => Same(i, platform, userId));
        return item is not null && Remove(item.Id) ? item : null;
    }

    /// <summary>Empties the queue (every waiting request refunded); what plays carries on.</summary>
    public int Clear()
    {
        List<MediaItem> removed;
        lock (_gate)
        {
            removed = Doc.Queue.ToList();
            Doc.Queue.Clear();
            Save();
        }
        foreach (var item in removed) Refund(item);
        Publish();
        return removed.Count;
    }

    /// <summary>Moves a waiting request up (negative) or down; false when it is not waiting.</summary>
    public bool Move(string id, int by)
    {
        lock (_gate)
        {
            var at = Doc.Queue.FindIndex(i => i.Id == id);
            if (at < 0) return false;
            var item = Doc.Queue[at];
            Doc.Queue.RemoveAt(at);
            Doc.Queue.Insert(Math.Clamp(at + by, 0, Doc.Queue.Count), item);
            Save();
        }
        Publish();
        return true;
    }

    // ---- Playing ----------------------------------------------------------------------------------------------------

    /// <summary>The player finished (or gave up on) a video: the next one starts. Ignored unless it is the one playing.</summary>
    public bool Ended(string id)
    {
        lock (_gate)
            if (Doc.Current?.Id != id) return false;
        Next();
        return true;
    }

    /// <summary>
    /// The player could not play a video (removed, or its owner does not allow it outside YouTube, which oEmbed does not
    /// tell): it is refunded and the next one starts. Ignored unless it is the one playing.
    /// </summary>
    public bool Failed(string id)
    {
        MediaItem? failed;
        lock (_gate)
        {
            failed = Doc.Current;
            if (failed?.Id != id) return false;
        }
        Refund(failed);
        _log.Info("Media requests", $"\"{failed.Title}\" could not be played here; moved on" + (failed.Cost > 0 ? " and refunded it" : ""));
        Next();
        return true;
    }

    /// <summary>Skips what plays (not refunded); false when nothing plays.</summary>
    public bool Skip()
    {
        MediaItem? skipped;
        lock (_gate) skipped = Doc.Current;
        if (skipped is null) return false;
        _bus.Publish(new BotEvent(MediaEvents.Skipped, Args(skipped)));
        Next();
        return true;
    }

    /// <summary>Pauses the player, or resumes it (starting the queue when nothing plays).</summary>
    public void SetPaused(bool paused)
    {
        bool start;
        lock (_gate)
        {
            Doc.Paused = paused;
            start = !paused && Doc.Current is null && Doc.Queue.Count > 0;
            Save();
        }
        if (start) Next();
        else Publish();
    }

    /// <summary>Moves what plays to the history and starts the next waiting request (if any).</summary>
    private void Next()
    {
        MediaItem? next;
        lock (_gate)
        {
            if (Doc.Current is { } done)
            {
                Doc.History.Insert(0, done);
                if (Doc.History.Count > HistorySize) Doc.History.RemoveRange(HistorySize, Doc.History.Count - HistorySize);
            }
            next = Doc.Paused || Doc.Queue.Count == 0 ? null : Doc.Queue[0];
            if (next is not null) Doc.Queue.RemoveAt(0);
            Doc.Current = next;
            Save();
        }
        if (next is not null) _bus.Publish(new BotEvent(MediaEvents.Started, Args(next)));
        Publish();
    }

    // ---- State ------------------------------------------------------------------------------------------------------

    /// <summary>
    /// <c>{open, paused, volume, maxSeconds, command, current, queue (the next 20), queueLength}</c>; an item is
    /// <c>{id, videoId, title, author, user, platform}</c>.
    /// </summary>
    public JsonObject Snapshot()
    {
        lock (_gate)
        {
            return new JsonObject
            {
                ["open"] = Doc.Settings.Open,
                ["paused"] = Doc.Paused,
                ["volume"] = Doc.Settings.Volume,
                ["maxSeconds"] = Doc.Settings.MaxSeconds,
                ["command"] = Doc.Settings.Command,
                ["current"] = Doc.Current is { } c ? ItemJson(c) : null,
                ["queue"] = new JsonArray([.. Doc.Queue.Take(20).Select(i => (JsonNode)ItemJson(i))]),
                ["queueLength"] = Doc.Queue.Count,
            };
        }
    }

    private static JsonObject ItemJson(MediaItem i) => new()
    {
        ["id"] = i.Id, ["videoId"] = i.VideoId, ["title"] = i.Title, ["author"] = i.Author, ["user"] = i.UserName, ["platform"] = i.Platform,
    };

    private void Publish()
    {
        var json = new JsonObject { ["type"] = "event", ["listener"] = Listener, ["event"] = Snapshot() }.ToJsonString();
        _feed.Publish(new OverlayMessage(json));
        Changed?.Invoke();
    }

    // ---- Chat -------------------------------------------------------------------------------------------------------

    private void OnEvent(BotEvent e)
    {
        if (e.Type != ChatHub.MessageEvent || e.Args.GetValueOrDefault("isInternal") is true) return;
        var text = e.Args.GetString("message").Trim();
        var words = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return;
        var platform = e.Args.GetString("platform");
        var user = e.Args.GetString("user");
        var userId = e.Args.GetString("userId") is { Length: > 0 } id ? id : user.ToLowerInvariant();
        if (platform.Length == 0 || userId.Length == 0) return;
        var settings = Settings;
        var command = words[0];
        if (command.Equals(settings.Command, StringComparison.OrdinalIgnoreCase) && words.Length == 2)
        {
            var subscriber = e.Args.GetValueOrDefault("isSubscriber") is true || e.Args.GetValueOrDefault("isBroadcaster") is true;
            _ = RequestFromChatAsync(words[1], platform, userId, user, subscriber, settings.ReplyInChat);
        }
        else if (command.Equals(settings.WrongCommand, StringComparison.OrdinalIgnoreCase) && words.Length == 1)
        {
            if (RemoveLatest(platform, userId) is { } removed && settings.ReplyInChat) Reply(platform, $"@{user} removed \"{removed.Title}\".");
        }
        else if (command.Equals(settings.SkipCommand, StringComparison.OrdinalIgnoreCase) && words.Length == 1
                 && (e.Args.GetValueOrDefault("isModerator") is true || e.Args.GetValueOrDefault("isBroadcaster") is true))
        {
            Skip();
        }
    }

    private async Task RequestFromChatAsync(string text, string platform, string userId, string user, bool subscriber, bool reply)
    {
        try
        {
            var (item, error) = await RequestAsync(text, platform, userId, user, subscriber).ConfigureAwait(false);
            if (!reply) return;
            if (item is null)
            {
                Reply(platform, $"@{user} {error}.");
                return;
            }
            int position;
            lock (_gate) position = Doc.Current?.Id == item.Id ? 0 : Doc.Queue.FindIndex(i => i.Id == item.Id) + 1;
            Reply(platform, position == 0 ? $"@{user} \"{item.Title}\" is playing now." : $"@{user} added \"{item.Title}\" (#{position} in the queue).");
        }
        catch (Exception ex)
        {
            _log.Error("Media requests", "A request failed", ex);
        }
    }

    private void Reply(string platform, string text)
    {
        if (_chat is null) return;
        _ = SendAsync(platform, text);
    }

    private async Task SendAsync(string platform, string text)
    {
        try
        {
            await _chat!.SendAsync(platform, text).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException)
        {
            _log.Warn("Media requests", "Could not answer in chat: " + ex.Message);
        }
    }

    private void Refund(MediaItem item)
    {
        if (item.Cost > 0) _points?.Add(item.Platform, item.UserId, item.UserName, item.Cost, "media request refund", Currency(item.Currency));
    }

    private static Dictionary<string, object?> Args(MediaItem i) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["requestId"] = i.Id, ["title"] = i.Title, ["author"] = i.Author, ["videoId"] = i.VideoId, ["url"] = i.Url, ["user"] = i.UserName,
        ["platform"] = i.Platform, ["userId"] = i.UserId, ["cost"] = i.Cost,
    };

    private static bool Same(MediaItem i, string platform, string userId) =>
        i.UserId == userId && i.Platform.Equals(platform, StringComparison.OrdinalIgnoreCase);

    private static MediaSettings Copy(MediaSettings s) => new()
    {
        Open = s.Open, Command = s.Command, WrongCommand = s.WrongCommand, SkipCommand = s.SkipCommand, Cost = s.Cost, Currency = s.Currency,
        MaxPerViewer = s.MaxPerViewer, MaxSeconds = s.MaxSeconds, MaxQueue = s.MaxQueue, SubscribersOnly = s.SubscribersOnly,
        ReplyInChat = s.ReplyInChat, Volume = s.Volume,
    };

    private static string Word(string value, string fallback) =>
        value.Trim().Split(' ')[0] is { Length: > 0 } word ? word : fallback;

    private static string? Currency(string currency) => currency.Length == 0 ? null : currency;

    private void Save() => _file?.ScheduleSave();

    public void Dispose()
    {
        _subscription.Dispose();
        _file?.Dispose();
    }
}
