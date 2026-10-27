using Cuebot.YouTube.Api;
using Cuebot.YouTube.Monitoring;

namespace Cuebot.YouTube.Tests;

public sealed class BroadcastTrackerTests
{
    private static YouTubeBroadcast B(string id, string title = "Title", string status = "live", DateTimeOffset? started = null, string privacy = "public") =>
        new(id, title, "desc", "chat-" + id, status, privacy, "UCo", "", null, started ?? DateTimeOffset.UnixEpoch, null);

    private static YouTubeVideo V(string id, string title = "Title", DateTimeOffset? ended = null) =>
        new(id, title, "desc", "UCo", "Owner", "20", [], "public", "live", "chat-" + id, null, DateTimeOffset.UnixEpoch, ended,
            new BroadcastStatistics(10, 0, 0, 0), "");

    private static BroadcastChangeKind[] Kinds(IReadOnlyList<BroadcastChange> changes) => changes.Select(c => c.Kind).ToArray();

    [Fact]
    public void Already_live_at_connect_starts_monitoring_without_started()
    {
        var t = new BroadcastTracker();
        Assert.Equal([BroadcastChangeKind.MonitoringStarted], Kinds(t.UpdateActive([B("b1")])));
        Assert.Empty(t.UpdateActive([B("b1")]));
        Assert.Equal("b1", t.Latest!.Id);
    }

    [Fact]
    public void Going_live_after_connect_fires_started_and_ending_fires_ended()
    {
        var t = new BroadcastTracker();
        Assert.Empty(t.UpdateActive([]));
        Assert.Equal([BroadcastChangeKind.MonitoringStarted, BroadcastChangeKind.Started], Kinds(t.UpdateActive([B("b1")])));
        Assert.Equal([BroadcastChangeKind.Ended, BroadcastChangeKind.MonitoringEnded], Kinds(t.UpdateActive([])));
        Assert.Null(t.Latest);
        // A late "active" listing of an ended broadcast does not restart it.
        Assert.Empty(t.UpdateActive([B("b1")]));
    }

    [Fact]
    public void Title_description_or_privacy_changes_fire_updated_with_previous_values()
    {
        var t = new BroadcastTracker();
        t.UpdateActive([B("b1", "Old")]);
        var change = Assert.Single(t.UpdateActive([B("b1", "New")]));
        Assert.Equal(BroadcastChangeKind.Updated, change.Kind);
        Assert.Equal("Old", change.Previous!.Title);
        Assert.Equal("New", change.Broadcast.Title);
        Assert.Equal(BroadcastChangeKind.Updated, Assert.Single(t.UpdateActive([B("b1", "New", privacy: "unlisted")])).Kind);
    }

    [Fact]
    public void Video_reads_update_and_end_monitored_broadcasts()
    {
        var t = new BroadcastTracker();
        t.UpdateActive([B("b1")]);
        Assert.Empty(t.ApplyVideo(V("b1")));
        Assert.Equal(BroadcastChangeKind.Updated, Assert.Single(t.ApplyVideo(V("b1", "Renamed"))).Kind);
        Assert.Equal([BroadcastChangeKind.Ended, BroadcastChangeKind.MonitoringEnded], Kinds(t.ApplyVideo(V("b1", "Renamed", DateTimeOffset.UnixEpoch))));
        Assert.Empty(t.ApplyVideo(V("unknown")));
    }

    [Fact]
    public void Upcoming_broadcasts_fire_added_and_removed_but_not_when_they_go_live()
    {
        var t = new BroadcastTracker();
        Assert.Empty(t.UpdateUpcoming([B("u0", status: "ready")]));   // seeding
        Assert.Equal([BroadcastChangeKind.Added], Kinds(t.UpdateUpcoming([B("u0", status: "ready"), B("u1", status: "ready")])));
        Assert.Equal([BroadcastChangeKind.Removed], Kinds(t.UpdateUpcoming([B("u1", status: "ready")])));

        t.UpdateActive([]);
        t.UpdateActive([B("u1")]);   // went live
        Assert.Empty(t.UpdateUpcoming([]));
        Assert.Empty(t.Upcoming);
    }

    [Fact]
    public void Latest_is_the_most_recently_started_and_stop_monitoring_ends_monitoring_only()
    {
        var t = new BroadcastTracker();
        t.UpdateActive([B("early", started: DateTimeOffset.UnixEpoch), B("late", started: DateTimeOffset.UnixEpoch.AddHours(1))]);
        Assert.Equal("late", t.Latest!.Id);
        Assert.Equal(2, t.Monitored.Count);
        Assert.All(t.StopMonitoring(), c => Assert.Equal(BroadcastChangeKind.MonitoringEnded, c.Kind));
        Assert.Empty(t.Monitored);
    }

    [Fact]
    public void Completed_broadcasts_in_the_active_list_are_ignored()
    {
        var t = new BroadcastTracker();
        Assert.Empty(t.UpdateActive([B("b1", status: "complete")]));
    }
}
