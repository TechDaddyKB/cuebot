using Cuebot.YouTube.Api;

namespace Cuebot.YouTube.Monitoring;

internal enum BroadcastChangeKind { Added, Removed, MonitoringStarted, Started, Updated, Ended, MonitoringEnded }

internal sealed record BroadcastChange(BroadcastChangeKind Kind, YouTubeBroadcast Broadcast, YouTubeBroadcast? Previous = null);

/// <summary>
/// Turns successive broadcast lists into changes. Active broadcasts are "monitored" (their chat is read). The first
/// list after connecting only starts monitoring: broadcasts that were already live do not fire Started (and existing
/// upcoming ones do not fire Added). Thread-safe.
/// </summary>
internal sealed class BroadcastTracker
{
    private readonly object _gate = new();
    private readonly Dictionary<string, YouTubeBroadcast> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, YouTubeBroadcast> _upcoming = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ended = new(StringComparer.Ordinal);
    private bool _activeSeeded;
    private bool _upcomingSeeded;

    /// <summary>Monitored broadcasts, most recently started first.</summary>
    public IReadOnlyList<YouTubeBroadcast> Monitored
    {
        get
        {
            lock (_gate)
                return _active.Values.OrderByDescending(b => b.ActualStart ?? b.ScheduledStart ?? DateTimeOffset.MinValue).ToArray();
        }
    }

    public IReadOnlyList<YouTubeBroadcast> Upcoming
    {
        get { lock (_gate) return _upcoming.Values.OrderBy(b => b.ScheduledStart ?? DateTimeOffset.MaxValue).ToArray(); }
    }

    /// <summary>The broadcast actions use by default: the latest one that started.</summary>
    public YouTubeBroadcast? Latest => Monitored.FirstOrDefault();

    public YouTubeBroadcast? Find(string broadcastId)
    {
        lock (_gate) return _active.GetValueOrDefault(broadcastId);
    }

    public IReadOnlyList<BroadcastChange> UpdateActive(IReadOnlyList<YouTubeBroadcast> active)
    {
        var changes = new List<BroadcastChange>();
        lock (_gate)
        {
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in active)
            {
                if (b.Id.Length == 0 || _ended.Contains(b.Id) || b.LifeCycleStatus is "complete" or "revoked") continue;
                listed.Add(b.Id);
                _upcoming.Remove(b.Id);
                if (!_active.TryGetValue(b.Id, out var old))
                {
                    _active[b.Id] = b;
                    changes.Add(new BroadcastChange(BroadcastChangeKind.MonitoringStarted, b));
                    if (_activeSeeded) changes.Add(new BroadcastChange(BroadcastChangeKind.Started, b));
                }
                else
                {
                    _active[b.Id] = b;
                    if (Differs(old, b)) changes.Add(new BroadcastChange(BroadcastChangeKind.Updated, b, old));
                }
            }
            foreach (var id in _active.Keys.Where(id => !listed.Contains(id)).ToArray()) EndLocked(id, changes);
            _activeSeeded = true;
        }
        return changes;
    }

    public IReadOnlyList<BroadcastChange> UpdateUpcoming(IReadOnlyList<YouTubeBroadcast> upcoming)
    {
        var changes = new List<BroadcastChange>();
        lock (_gate)
        {
            var listed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var b in upcoming)
            {
                if (b.Id.Length == 0 || _active.ContainsKey(b.Id) || _ended.Contains(b.Id)) continue;
                listed.Add(b.Id);
                if (!_upcoming.TryGetValue(b.Id, out var old))
                {
                    _upcoming[b.Id] = b;
                    if (_upcomingSeeded) changes.Add(new BroadcastChange(BroadcastChangeKind.Added, b));
                }
                else
                {
                    _upcoming[b.Id] = b;
                    if (Differs(old, b)) changes.Add(new BroadcastChange(BroadcastChangeKind.Updated, b, old));
                }
            }
            foreach (var (id, b) in _upcoming.Where(p => !listed.Contains(p.Key)).ToArray())
            {
                _upcoming.Remove(id);
                // Gone because it went live: the active list reports it. Otherwise it was deleted or cancelled.
                if (!_active.ContainsKey(id)) changes.Add(new BroadcastChange(BroadcastChangeKind.Removed, b));
            }
            _upcomingSeeded = true;
        }
        return changes;
    }

    /// <summary>Applies a fresh videos.list read of a monitored broadcast: end, or title/description/privacy changes.</summary>
    public IReadOnlyList<BroadcastChange> ApplyVideo(YouTubeVideo video)
    {
        var changes = new List<BroadcastChange>();
        lock (_gate)
        {
            if (!_active.TryGetValue(video.Id, out var old)) return changes;
            var updated = old with
            {
                Title = video.Title,
                Description = video.Description,
                PrivacyStatus = video.PrivacyStatus.Length > 0 ? video.PrivacyStatus : old.PrivacyStatus,
                ActualStart = video.ActualStart ?? old.ActualStart,
                ActualEnd = video.ActualEnd,
            };
            _active[video.Id] = updated;
            if (video.ActualEnd is not null) EndLocked(video.Id, changes);
            else if (Differs(old, updated)) changes.Add(new BroadcastChange(BroadcastChangeKind.Updated, updated, old));
        }
        return changes;
    }

    /// <summary>The broadcast ended (chat ended, or the video reports an end time).</summary>
    public IReadOnlyList<BroadcastChange> End(string broadcastId)
    {
        var changes = new List<BroadcastChange>();
        lock (_gate) EndLocked(broadcastId, changes);
        return changes;
    }

    /// <summary>Stops monitoring everything (disconnect): MonitoringEnded without Ended.</summary>
    public IReadOnlyList<BroadcastChange> StopMonitoring()
    {
        lock (_gate)
        {
            var changes = _active.Values.Select(b => new BroadcastChange(BroadcastChangeKind.MonitoringEnded, b)).ToArray();
            _active.Clear();
            return changes;
        }
    }

    /// <summary>Replaces a monitored broadcast's details (after Cuebot itself changed its title, for example).</summary>
    public void Replace(YouTubeBroadcast broadcast)
    {
        lock (_gate)
            if (_active.ContainsKey(broadcast.Id)) _active[broadcast.Id] = broadcast;
    }

    private void EndLocked(string id, List<BroadcastChange> changes)
    {
        if (!_active.Remove(id, out var b)) return;
        _ended.Add(id);
        changes.Add(new BroadcastChange(BroadcastChangeKind.Ended, b));
        changes.Add(new BroadcastChange(BroadcastChangeKind.MonitoringEnded, b));
    }

    private static bool Differs(YouTubeBroadcast a, YouTubeBroadcast b) =>
        a.Title != b.Title || a.Description != b.Description || a.PrivacyStatus != b.PrivacyStatus;
}
