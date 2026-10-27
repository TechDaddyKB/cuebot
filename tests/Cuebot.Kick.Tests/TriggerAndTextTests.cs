using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Users;
using Cuebot.Kick.Triggers;

namespace Cuebot.Kick.Tests;

public sealed class TriggerTests
{
    private readonly IReadOnlyList<ITriggerType> _triggers = new KickTriggers().GetTriggers().ToArray();

    private ITriggerType Get(string id) => _triggers.Single(t => t.TypeId == id);

    private static bool Matches(ITriggerType t, BotEvent e, Dictionary<string, string>? settings = null) =>
        t.EventTypes.Contains(e.Type) && t.Matches(e, new TriggerDefinition(), ParameterValues.From(t.Parameters, settings ?? []),
            new Dictionary<string, object?>());

    [Fact]
    public void Every_streamerbot_kick_trigger_except_7tv_has_a_counterpart()
    {
        string[] expected =
        [
            "kick.rewardRedemption", "kick.follow", "kick.streamOffline", "kick.streamOnline", "kick.viewerCountUpdate",
            "kick.firstWords", "kick.chatMessage", "kick.channelUpdate", "kick.presentViewers", "kick.kicksGifted",
            "kick.userBanned", "kick.userTimedOut", "kick.giftSubscription", "kick.massGiftSubscription", "kick.resubscription",
            "kick.subscription", "kick.broadcasterAuthenticated", "kick.chatConnected", "kick.chatDisconnected",
        ];
        Assert.All(expected, id => Assert.Contains(_triggers, t => t.TypeId == id));
        Assert.All(_triggers, t => Assert.Equal("Kick", t.Category));
        Assert.Equal(_triggers.Count, _triggers.Select(t => t.TypeId).Distinct().Count());
    }

    [Fact]
    public void First_words_and_present_viewers_only_fire_for_kick()
    {
        var first = Get("kick.firstWords");
        Assert.True(Matches(first, new BotEvent(UserEvents.FirstWords, new Dictionary<string, object?> { ["platform"] = "Kick" })));
        Assert.False(Matches(first, new BotEvent(UserEvents.FirstWords, new Dictionary<string, object?> { ["platform"] = "Twitch" })));
        Assert.True(Matches(Get("kick.presentViewers"), new BotEvent(UserEvents.PresentViewers, new Dictionary<string, object?> { ["platform"] = "kick" })));
    }

    [Fact]
    public void Filters_apply()
    {
        var kicks = Get(KickEventTypes.KicksGifted);
        var small = new BotEvent(KickEventTypes.KicksGifted, new Dictionary<string, object?> { ["kicks"] = 50L });
        Assert.False(Matches(kicks, small, new() { ["minKicks"] = "100" }));
        Assert.True(Matches(kicks, small, new() { ["minKicks"] = "10" }));

        var reward = Get(KickEventTypes.RewardRedemption);
        var hydrate = new BotEvent(KickEventTypes.RewardRedemption, new Dictionary<string, object?> { ["rewardTitle"] = "Hydrate", ["status"] = "pending" });
        Assert.True(Matches(reward, hydrate, new() { ["reward"] = "hydrate" }));
        Assert.False(Matches(reward, hydrate, new() { ["reward"] = "Other" }));
        Assert.False(Matches(reward, hydrate, new() { ["status"] = "accepted" }));

        var gift = Get(KickEventTypes.GiftSubscription);
        var fromMass = new BotEvent(KickEventTypes.GiftSubscription, new Dictionary<string, object?> { ["fromMassGift"] = true });
        Assert.True(Matches(gift, fromMass));
        Assert.False(Matches(gift, fromMass, new() { ["ignoreMass"] = "true" }));

        var chat = Get(KickEventTypes.ChatMessage);
        var msg = new BotEvent(KickEventTypes.ChatMessage, new Dictionary<string, object?> { ["message"] = "Hello World" });
        Assert.True(Matches(chat, msg, new() { ["contains"] = "world" }));
        Assert.False(Matches(chat, msg, new() { ["contains"] = "bye" }));
    }
}

public sealed class ChatTextTests
{
    [Fact]
    public void Short_text_is_one_trimmed_message() => Assert.Equal(["hello"], ChatText.Split("  hello "));

    [Fact]
    public void Blank_text_is_nothing() => Assert.Empty(ChatText.Split("   "));

    [Fact]
    public void Long_text_splits_at_word_boundaries_within_500_graphemes()
    {
        var words = string.Join(' ', Enumerable.Range(0, 200).Select(i => "word" + i.ToString(CultureInfo.InvariantCulture)));
        var parts = ChatText.Split(words);
        Assert.True(parts.Count > 1);
        Assert.All(parts, p => Assert.InRange(new StringInfo(p).LengthInTextElements, 1, 500));
        Assert.Equal(words, string.Join(' ', parts));
    }

    [Fact]
    public void Emoji_count_as_one_grapheme_and_bytes_are_limited()
    {
        var emoji = string.Concat(Enumerable.Repeat("👍🏽", 600)); // 600 graphemes, 8 UTF-8 bytes each
        var parts = ChatText.Split(emoji);
        Assert.All(parts, p => Assert.True(System.Text.Encoding.UTF8.GetByteCount(p) <= 2048));
        Assert.Equal(600, parts.Sum(p => new StringInfo(p).LengthInTextElements));
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(60, 1)]
    [InlineData(61, 2)]
    [InlineData(600, 10)]
    [InlineData(10_000_000, 10080)]
    public void Timeout_seconds_round_up_to_kick_minutes(int seconds, int minutes) => Assert.Equal(minutes, KickService.TimeoutMinutes(seconds));
}
