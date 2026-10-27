using Cuebot.Rumble.LiveApi;
using static Cuebot.Rumble.Tests.Support.Snapshots;

namespace Cuebot.Rumble.Tests;

public sealed class ActivityTrackerTests
{
    private readonly ActivityTracker _tracker = new();

    [Fact]
    public void Seeding_reports_nothing_and_the_next_poll_reports_only_new_items()
    {
        var stream = Stream(chat: [ChatLine("Bob", "old", 1)], rants: [Rant("Gina", "old rant", 1, 100)]);
        _tracker.Seed(Channel(followers: [Follower("Alice", 1)], live: stream));

        var changes = _tracker.Update(Channel(
            followers: [Follower("Carol", 3), Follower("Bob", 2), Follower("Alice", 1)],
            live: stream with
            {
                RecentMessages = [ChatLine("Bob", "new", 5), ChatLine("Bob", "old", 1)],
                RecentRants = [Rant("Gina", "old rant", 1, 100), Rant("Dave", "new rant", 6, 500)],
            }));

        Assert.Equal(["Bob", "Carol"], changes.Followers.Select(f => f.UserName));   // oldest first
        Assert.Equal("new", Assert.Single(changes.ChatMessages).Text);
        Assert.Equal("new rant", Assert.Single(changes.Rants).Text);
        Assert.Null(changes.WentOnline);
        Assert.Null(changes.WentOffline);
    }

    [Fact]
    public void Unchanged_polls_report_nothing_even_after_an_item_briefly_disappears()
    {
        var snapshot = Channel(followers: [Follower("Alice", 1)], subscribers: [new ApiSubscriber("Dave", 500, T0)]);
        _tracker.Seed(snapshot);

        var unchanged = _tracker.Update(snapshot);
        Assert.Empty(unchanged.Followers);
        Assert.Empty(unchanged.Subscribers);
        Assert.Empty(_tracker.Update(Channel()).Followers);
        var again = _tracker.Update(snapshot);
        Assert.Empty(again.Followers);
        Assert.Empty(again.Subscribers);
    }

    [Fact]
    public void Identical_gifts_count_separately_and_remaining_count_changes_are_not_new_gifts()
    {
        var gift = new ApiGiftedSub("Frank", 5, 5, "rumble", 42);
        _tracker.Seed(Channel(gifts: [gift]));

        Assert.Empty(_tracker.Update(Channel(gifts: [gift with { RemainingGifts = 3 }])).GiftedSubs);
        var changes = _tracker.Update(Channel(gifts: [gift, gift with { RemainingGifts = 3 }]));

        Assert.Equal(5, Assert.Single(changes.GiftedSubs).TotalGifts);
    }

    [Fact]
    public void Stream_online_and_offline_transitions_are_reported_once()
    {
        _tracker.Seed(Channel());

        var online = _tracker.Update(Channel(live: Stream("abc")));
        Assert.Equal("abc", online.WentOnline?.Id);
        Assert.Null(_tracker.Update(Channel(live: Stream("abc"))).WentOnline);

        var offline = _tracker.Update(Channel());
        Assert.Equal("abc", offline.WentOffline?.Id);
        Assert.Null(offline.WentOnline);
        Assert.Null(_tracker.Update(Channel()).WentOffline);
    }

    [Fact]
    public void A_stream_replaced_by_another_reports_offline_then_online()
    {
        _tracker.Seed(Channel(live: Stream("abc")));

        var changes = _tracker.Update(Channel(live: Stream("def")));

        Assert.Equal("abc", changes.WentOffline?.Id);
        Assert.Equal("def", changes.WentOnline?.Id);
    }

    [Fact]
    public void A_stream_live_at_seeding_is_not_reported_online_and_its_earlier_chat_is_history()
    {
        _tracker.Seed(Channel(live: Stream(chat: [ChatLine("Bob", "old", -5)])));

        var changes = _tracker.Update(Channel(live: Stream(chat: [ChatLine("Bob", "new", -4), ChatLine("Bob", "old", -5)])));

        Assert.Null(changes.WentOnline);
        Assert.Equal("new", Assert.Single(changes.ChatMessages).Text);   // no time cut-off: seeding recorded the history
    }

    [Fact]
    public void Chat_of_a_stream_that_goes_live_later_counts_from_when_it_started()
    {
        _tracker.Seed(Channel(now: T0.AddMinutes(-30)));

        var changes = _tracker.Update(Channel(live: Stream(createdOn: T0,
            chat: [ChatLine("Bob", "before the stream", -10), ChatLine("Bob", "first!", 1)])));

        Assert.Equal(T0, _tracker.ChatSince);
        Assert.Equal("first!", Assert.Single(changes.ChatMessages).Text);
    }

    [Fact]
    public void Chat_since_is_never_before_seeding_even_for_streams_created_earlier()
    {
        _tracker.Seed(Channel(now: T0));

        var changes = _tracker.Update(Channel(live: Stream(createdOn: T0.AddHours(-1),
            chat: [ChatLine("Bob", "scheduled-stream chatter", -5), ChatLine("Bob", "live now", 2)])));

        Assert.Equal(T0, _tracker.ChatSince);
        Assert.Equal("live now", Assert.Single(changes.ChatMessages).Text);
    }
}

public sealed class NewItemFilterTests
{
    [Fact]
    public void Returns_unseen_items_in_order_and_counts_duplicates()
    {
        var filter = new NewItemFilter<string>();

        Assert.Equal(["a", "b", "a"], filter.TakeNew(["a", "b", "a"], x => x));
        Assert.Empty(filter.TakeNew(["a", "b"], x => x));
        Assert.Equal(["a", "c"], filter.TakeNew(["a", "a", "a", "c"], x => x));
    }

    [Fact]
    public void Forgets_the_oldest_keys_beyond_capacity()
    {
        var filter = new NewItemFilter<int>();
        filter.TakeNew(Enumerable.Range(0, NewItemFilter<int>.Capacity + 1), x => x);

        Assert.Equal([0], filter.TakeNew([0], x => x));
        Assert.Empty(filter.TakeNew([NewItemFilter<int>.Capacity], x => x));
    }
}
