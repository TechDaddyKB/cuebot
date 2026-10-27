namespace Cuebot.Rumble.LiveApi;

/// <summary>What changed between two Live Stream API polls, oldest first within each list.</summary>
internal sealed record PollChanges(
    IReadOnlyList<ApiFollower> Followers,
    IReadOnlyList<ApiSubscriber> Subscribers,
    IReadOnlyList<ApiGiftedSub> GiftedSubs,
    IReadOnlyList<ApiChatMessage> ChatMessages,
    IReadOnlyList<ApiRant> Rants,
    ApiLivestream? WentOffline,
    ApiLivestream? WentOnline);

/// <summary>
/// Turns successive Live Stream API snapshots into changes. The API only ever returns "recent" windows, so the
/// first snapshot after connecting is history: <see cref="Seed"/> records it without reporting anything. Chat of a
/// stream that goes live later is reported only from the moment it went live (or the moment of seeding, if that is
/// later), so pre-stream chat is not replayed. Not thread-safe: call from the poll loop only.
/// </summary>
internal sealed class ActivityTracker
{
    private readonly NewItemFilter<(string, DateTimeOffset)> _followers = new();
    private readonly NewItemFilter<ApiSubscriber> _subscribers = new();
    private readonly NewItemFilter<(string, int, string, long)> _gifts = new();
    private readonly NewItemFilter<(string, string, DateTimeOffset)> _chat = new();
    private readonly NewItemFilter<(string, string, DateTimeOffset, long)> _rants = new();
    private DateTimeOffset _seededAt;
    private DateTimeOffset _chatSince;
    private ApiLivestream? _live;

    /// <summary>The live stream as of the last snapshot.</summary>
    public ApiLivestream? LiveStream => _live;

    /// <summary>
    /// Earliest time (Rumble's clock) from which chat of a stream that went live after seeding counts as new.
    /// <see cref="DateTimeOffset.MinValue"/> for a stream already live when seeding: its history was recorded then.
    /// </summary>
    public DateTimeOffset ChatSince => _chatSince;

    public void Seed(LiveStreamSnapshot snapshot)
    {
        _seededAt = snapshot.ServerTime;
        _live = snapshot.LiveStream;
        _chatSince = DateTimeOffset.MinValue;
        Diff(snapshot);
    }

    public PollChanges Update(LiveStreamSnapshot snapshot)
    {
        var previous = _live;
        _live = snapshot.LiveStream;
        var restarted = _live is not null && _live.Id != previous?.Id;
        if (restarted) _chatSince = _live!.CreatedOn > _seededAt ? _live.CreatedOn : _seededAt;

        var changes = Diff(snapshot);
        return changes with
        {
            ChatMessages = changes.ChatMessages.Where(m => m.CreatedOn >= _chatSince).ToArray(),
            Rants = changes.Rants.Where(r => r.CreatedOn >= _chatSince).ToArray(),
            WentOffline = previous is not null && previous.Id != _live?.Id ? previous : null,
            WentOnline = restarted ? _live : null,
        };
    }

    private PollChanges Diff(LiveStreamSnapshot snapshot) => new(
        _followers.TakeNew(snapshot.RecentFollowers.OrderBy(f => f.FollowedOn), f => (f.UserName, f.FollowedOn)),
        _subscribers.TakeNew(snapshot.RecentSubscribers.OrderBy(s => s.SubscribedOn), s => s),
        // Gifts carry no timestamp; Rumble lists them newest first.
        _gifts.TakeNew(snapshot.RecentGiftedSubs.Reverse(), g => (g.PurchasedBy, g.TotalGifts, g.GiftType, g.VideoId)),
        _chat.TakeNew((snapshot.LiveStream?.RecentMessages ?? []).OrderBy(m => m.CreatedOn), m => (m.UserName, m.Text, m.CreatedOn)),
        _rants.TakeNew((snapshot.LiveStream?.RecentRants ?? []).OrderBy(r => r.CreatedOn),
            r => (r.UserName, r.Text, r.CreatedOn, r.AmountCents)),
        null,
        null);
}
