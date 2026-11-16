using Cuebot.YouTube.Api;
using Cuebot.YouTube.Tests.Support;

namespace Cuebot.YouTube.Tests;

/// <summary>The YouTube quota as moderation sees it: what is left today, the reserve, and the reset at midnight Pacific.</summary>
public sealed class QuotaBudgetTests
{
    [Fact]
    public void Moderation_may_not_spend_the_reserve_and_the_count_starts_again_at_midnight_pacific()
    {
        // 2026-11-16 23:30 in Los Angeles (PST, UTC-8).
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 11, 17, 7, 30, 0, TimeSpan.Zero));
        var tracker = new QuotaTracker(time);
        var reserve = 2_000;
        var budget = new YouTubeQuotaBudget(() => tracker.UsedToday, () => 10_000, () => reserve, () => tracker.NextReset, () => tracker.ExhaustedUntil);

        tracker.Spend(7_900);
        Assert.Equal((2_100, 2_000), (budget.Remaining, budget.Reserve));
        Assert.Null(budget.Refuse(50));
        Assert.Contains("kept for reading chat", budget.Refuse(200));
        Assert.Equal(new DateTimeOffset(2026, 11, 17, 8, 0, 0, TimeSpan.Zero), budget.ResetsAt);
        Assert.Contains("2,100 of 10,000", budget.Summary);

        time.Advance(TimeSpan.FromMinutes(31));   // past midnight Pacific
        Assert.Equal(10_000, budget.Remaining);
        Assert.Null(budget.Refuse(200));

        tracker.MarkExhausted();
        Assert.Equal(0, budget.Remaining);
        Assert.Contains("used up", budget.Refuse(1));

        reserve = 50_000;   // a reserve above the daily quota keeps everything
        Assert.Equal(10_000, budget.Reserve);
    }
}
