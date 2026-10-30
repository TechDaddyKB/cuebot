using Cuebot.YouTube.Api;
using Cuebot.YouTube.Chat;
using Cuebot.YouTube.Tests.Support;

namespace Cuebot.YouTube.Tests;

public sealed class QuotaAndHelpersTests
{
    [Fact]
    public void Quota_resets_at_midnight_pacific_in_winter_and_summer()
    {
        // 2026-01-15 10:00 UTC = 02:00 PST (UTC-8): next reset 2026-01-16 00:00 PST = 08:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 1, 16, 8, 0, 0, TimeSpan.Zero),
            QuotaTracker.NextResetAfter(new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero)).ToUniversalTime());
        // 2026-07-15 05:00 UTC = 22:00 PDT on the 14th (UTC-7): next reset 2026-07-15 00:00 PDT = 07:00 UTC.
        Assert.Equal(new DateTimeOffset(2026, 7, 15, 7, 0, 0, TimeSpan.Zero),
            QuotaTracker.NextResetAfter(new DateTimeOffset(2026, 7, 15, 5, 0, 0, TimeSpan.Zero)).ToUniversalTime());
        Assert.Equal("2026-07-14", QuotaTracker.DayOf(new DateTimeOffset(2026, 7, 15, 5, 0, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void Quota_counter_rolls_over_with_the_day_and_restores_persisted_usage()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 1, 15, 10, 0, 0, TimeSpan.Zero));
        var quota = new QuotaTracker(time);
        quota.Restore("2026-01-15", 400);
        quota.Restore("2026-01-14", 9999);   // another day: ignored
        quota.Spend(5);
        Assert.Equal(405, quota.UsedToday);

        quota.MarkExhausted();
        Assert.NotNull(quota.ExhaustedUntil);
        time.Advance(TimeSpan.FromHours(23));
        Assert.Null(quota.ExhaustedUntil);
        Assert.Equal(0, quota.UsedToday);
        Assert.Equal("2026-01-16", quota.Day);
    }

    [Fact]
    public void Quota_estimate_counts_chat_and_statistics_only_while_live()
    {
        var (live, idle) = QuotaCosts.EstimatePerHour(chatSeconds: 8, broadcastSeconds: 120, liveSeconds: 60, subscriberSeconds: 300,
            monitorUpcoming: false, pollSubscribers: true);
        // idle: 30 broadcast lists x 5 + 12 subscriber lists x 1 = 162; live adds 450 chat polls x 5 + 60 video reads x 1.
        Assert.Equal(162, idle);
        Assert.Equal(162 + 2250 + 60, live);

        var (_, withUpcoming) = QuotaCosts.EstimatePerHour(8, 120, 60, 300, monitorUpcoming: true, pollSubscribers: false);
        Assert.Equal(300, withUpcoming);
    }

    [Fact]
    public void Streamed_chat_costs_one_call_per_assumed_reconnect_instead_of_per_poll()
    {
        var (live, idle) = QuotaCosts.EstimatePerHour(chatSeconds: 8, broadcastSeconds: 120, liveSeconds: 60, subscriberSeconds: 300,
            monitorUpcoming: false, pollSubscribers: true, streamChat: true);
        // 12 connections x 5 (one every 5 minutes) instead of 450 polls x 5.
        Assert.Equal(162, idle);
        Assert.Equal(162 + 60 + 60, live);
    }

    [Fact]
    public void Timings_clamp_chat_delay_and_back_off()
    {
        var t = YouTubeTimings.Default;
        Assert.Equal(TimeSpan.FromSeconds(8), t.ChatDelay(TimeSpan.FromSeconds(3), 8));
        Assert.Equal(TimeSpan.FromSeconds(15), t.ChatDelay(TimeSpan.FromSeconds(15), 8));
        Assert.Equal(TimeSpan.FromSeconds(2), t.ChatDelay(TimeSpan.Zero, 0));
        Assert.Equal(TimeSpan.FromSeconds(5), t.NextBackoff(TimeSpan.Zero));
        Assert.Equal(TimeSpan.FromSeconds(10), t.NextBackoff(TimeSpan.FromSeconds(5)));
        Assert.Equal(TimeSpan.FromMinutes(5), t.NextBackoff(TimeSpan.FromMinutes(4)));
    }

    [Fact]
    public void Long_messages_split_at_words_within_200_characters()
    {
        Assert.Empty(MessageSplitter.Split("   "));
        Assert.Equal(["short"], MessageSplitter.Split(" short "));

        var words = string.Join(' ', Enumerable.Repeat("word", 100));   // 499 characters
        var parts = MessageSplitter.Split(words);
        Assert.All(parts, p => Assert.InRange(p.Length, 1, 200));
        Assert.Equal(words, string.Join(' ', parts));

        var unbroken = new string('x', 450);
        Assert.Equal([200, 200, 50], MessageSplitter.Split(unbroken).Select(p => p.Length));
    }

    [Fact]
    public void Channel_ids_are_recognized()
    {
        Assert.True(YouTubeService.IsChannelId("UCaliceAliceAliceAlice01"));
        Assert.False(YouTubeService.IsChannelId("@alice"));
        Assert.False(YouTubeService.IsChannelId("Alice"));
    }

    [Fact]
    public void Chatter_directory_resolves_names_and_message_authors()
    {
        var d = new ChatterDirectory();
        d.Remember("UC1", "Alice");
        d.RememberMessage("m1", "UC1", "Alice", "hi");
        Assert.Equal("UC1", d.IdOf("@alice"));
        Assert.Equal("Alice", d.NameOf("UC1"));
        Assert.Equal("hi", d.MessageAuthor("m1")!.Text);
        Assert.Null(d.IdOf("nobody"));
    }
}
