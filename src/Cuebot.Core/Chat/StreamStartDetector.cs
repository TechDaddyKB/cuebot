using Cuebot.Core.Events;
using Cuebot.Core.Commands;

namespace Cuebot.Core.Chat;

/// <summary>
/// Tells a real stream start from one platform going live again while the stream is already running (a platform's
/// API dropping the stream for a minute, a platform added late, a reconnect). Feed it every chat.activity event:
/// <see cref="IsNewStream"/> is true only for a StreamOnline that arrives while no platform is live and none was live
/// in the last <see cref="Grace"/>. Things that start over "when the stream goes live" (credits, the merch counter)
/// use it so a mid-stream blip does not wipe them (a Rumble blip did, on 2026-11-14). Thread-safe.
/// </summary>
public sealed class StreamStartDetector
{
    /// <summary>How long every platform must have been offline before going live counts as a new stream.</summary>
    public static readonly TimeSpan DefaultGrace = TimeSpan.FromMinutes(10);

    /// <summary>
    /// A platform that never reported going offline is not trusted to still be live after this long: the next
    /// StreamOnline then starts a new stream anyway.
    /// </summary>
    public static readonly TimeSpan MaxStreamLength = TimeSpan.FromHours(12);

    private readonly TimeProvider _time;
    private readonly HashSet<string> _live = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private DateTimeOffset? _lastLive;
    private DateTimeOffset? _lastOnline;

    /// <param name="lastLive">When the stream was last known to be running (e.g. saved data updated mid-stream), so a
    /// Cuebot restarted mid-stream does not take the platforms reconnecting for a new stream. Null when unknown.</param>
    public StreamStartDetector(TimeProvider? time = null, TimeSpan? grace = null, DateTimeOffset? lastLive = null)
    {
        _time = time ?? TimeProvider.System;
        Grace = grace ?? DefaultGrace;
        _lastLive = lastLive;
    }

    public TimeSpan Grace { get; }

    /// <summary>Records the event; true when it is a StreamOnline that starts a new stream.</summary>
    public bool IsNewStream(BotEvent e) =>
        e.Type == ChatHub.ActivityEvent && IsNewStream(e.Args.GetString("kind"), e.Args.GetString("platform"));

    /// <summary>Records a feed kind for a platform; true when it is a StreamOnline that starts a new stream.</summary>
    public bool IsNewStream(string kind, string platform)
    {
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            if (string.Equals(kind, nameof(FeedKind.StreamOffline), StringComparison.OrdinalIgnoreCase))
            {
                _live.Remove(platform);
                _lastLive = now;
                return false;
            }
            if (!string.Equals(kind, nameof(FeedKind.StreamOnline), StringComparison.OrdinalIgnoreCase)) return false;
            if (_lastOnline is { } previous && now - previous >= MaxStreamLength) _live.Clear();
            var isNew = _live.Count == 0 && (_lastLive is not { } last || now - last >= Grace);
            _live.Add(platform);
            _lastLive = now;
            _lastOnline = now;
            return isNew;
        }
    }
}
