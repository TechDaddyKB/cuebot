using Cuebot.Twitch.EventSub;

namespace Cuebot.Twitch.Tests;

public sealed class EventSubSubscriptionSpecTests
{
    [Fact]
    public void Ad_break_subscription_names_the_broadcaster_user()
    {
        // Twitch rejects channel.ad_break.begin with 400 unless the condition has broadcaster_user_id.
        var spec = Assert.Single(EventSubSubscriptions.All, s => s.Type == "channel.ad_break.begin");

        Assert.Equal(new Dictionary<string, string> { ["broadcaster_user_id"] = "123" }, spec.BuildCondition("123"));
    }

    [Fact]
    public void No_subscription_uses_a_bare_broadcaster_id_condition()
    {
        Assert.All(EventSubSubscriptions.All.Concat(EventSubSubscriptions.Bot), s =>
            Assert.DoesNotContain("broadcaster_id", s.BuildCondition("123").Keys));
    }
}
