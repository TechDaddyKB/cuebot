using Cuebot.Core.Commands;

namespace Cuebot.Tests.Commands;

public class CronExpressionTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 5, 12, 0, 30, TimeSpan.Zero); // Monday

    private static DateTimeOffset? Next(string cron, DateTimeOffset after) => CronExpression.Parse(cron).Next(after, TimeZoneInfo.Utc);

    [Theory]
    [InlineData("* * * * *", "2026-01-05T12:01:00")]
    [InlineData("*/15 * * * *", "2026-01-05T12:15:00")]
    [InlineData("5 * * * *", "2026-01-05T12:05:00")]
    [InlineData("0 * * * *", "2026-01-05T13:00:00")]
    [InlineData("30 9 * * *", "2026-01-06T09:30:00")]
    [InlineData("0 0 1 * *", "2026-02-01T00:00:00")]
    [InlineData("0 12 * * 0", "2026-01-11T12:00:00")]
    [InlineData("0 12 * * 7", "2026-01-11T12:00:00")]
    [InlineData("0 12 * * SUN", "2026-01-11T12:00:00")]
    [InlineData("0 8 * JAN-MAR FRI", "2026-01-09T08:00:00")]
    [InlineData("10,20,40 12 * * *", "2026-01-05T12:10:00")]
    [InlineData("10-12 13 * * *", "2026-01-05T13:10:00")]
    [InlineData("0 10-20/5 * * *", "2026-01-05T15:00:00")]
    [InlineData("7/20 * * * *", "2026-01-05T12:07:00")]
    [InlineData("@hourly", "2026-01-05T13:00:00")]
    [InlineData("@daily", "2026-01-06T00:00:00")]
    [InlineData("@weekly", "2026-01-11T00:00:00")]
    [InlineData("@yearly", "2027-01-01T00:00:00")]
    [InlineData("0 0 29 2 *", "2028-02-29T00:00:00")]
    public void Next_occurrence(string cron, string expected) =>
        Assert.Equal(DateTimeOffset.Parse(expected + "Z", System.Globalization.CultureInfo.InvariantCulture), Next(cron, Start));

    [Fact]
    public void Day_of_month_and_day_of_week_are_ored_when_both_restricted()
    {
        // The 15th, or any Monday: 2026-01-05 is a Monday, so the next is the following Monday (12th) before the 15th.
        var cron = CronExpression.Parse("0 0 15 * MON");
        Assert.Equal(new DateTimeOffset(2026, 1, 12, 0, 0, 0, TimeSpan.Zero), cron.Next(Start, TimeZoneInfo.Utc));
        Assert.Equal(new DateTimeOffset(2026, 1, 15, 0, 0, 0, TimeSpan.Zero), cron.Next(new DateTimeOffset(2026, 1, 12, 1, 0, 0, TimeSpan.Zero), TimeZoneInfo.Utc));
    }

    [Fact]
    public void Next_is_strictly_after_the_given_time()
    {
        var at = new DateTimeOffset(2026, 1, 5, 12, 15, 0, TimeSpan.Zero);
        Assert.Equal(at.AddMinutes(15), Next("*/15 * * * *", at));
    }

    [Fact]
    public void Matches_checks_a_wall_clock_minute()
    {
        var cron = CronExpression.Parse("*/10 9-17 * * MON-FRI");
        Assert.True(cron.Matches(new DateTime(2026, 1, 5, 9, 20, 0)));
        Assert.False(cron.Matches(new DateTime(2026, 1, 5, 9, 25, 0)));
        Assert.False(cron.Matches(new DateTime(2026, 1, 10, 9, 20, 0))); // Saturday
    }

    [Fact]
    public void Impossible_dates_return_null()
    {
        Assert.Null(Next("0 0 31 2 *", Start));
    }

    [Fact]
    public void Evaluates_in_the_given_time_zone()
    {
        var zone = TimeZoneInfo.CreateCustomTimeZone("Plus2", TimeSpan.FromHours(2), "Plus2", "Plus2");
        var next = CronExpression.Parse("0 15 * * *").Next(Start, zone); // 12:00:30Z is 14:00:30 local
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 15, 0, 0, TimeSpan.FromHours(2)), next);
        Assert.Equal(new DateTimeOffset(2026, 1, 5, 13, 0, 0, TimeSpan.Zero), next!.Value.ToUniversalTime());
    }

    [Theory]
    [InlineData("")]
    [InlineData("* * * *")]
    [InlineData("* * * * * *")]
    [InlineData("60 * * * *")]
    [InlineData("* 24 * * *")]
    [InlineData("* * 0 * *")]
    [InlineData("* * * 13 *")]
    [InlineData("* * * * 8")]
    [InlineData("*/0 * * * *")]
    [InlineData("5-1 * * * *")]
    [InlineData("a * * * *")]
    [InlineData("1,,2 * * * *")]
    public void Invalid_expressions_are_rejected(string text)
    {
        Assert.False(CronExpression.TryParse(text, out _, out var error));
        Assert.NotEmpty(error);
        Assert.Throws<FormatException>(() => CronExpression.Parse(text));
    }
}
