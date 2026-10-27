using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Twitch.Tests.Support;
using Cuebot.Twitch.Triggers;

namespace Cuebot.Twitch.Tests;

public sealed class CommandMatcherTests
{
    [Theory]
    [InlineData("!hello", "!hello", "")]
    [InlineData("!hello world  again ", "!hello", "world  again")]
    [InlineData("  !HELLO there", "!hello", "there")]
    public void StartsWith_matches_whole_command(string message, string command, string input)
    {
        var match = CommandMatcher.Match(message, command, CommandMatchMode.StartsWith, caseSensitive: false);

        Assert.NotNull(match);
        Assert.Equal(input, match.Input);
    }

    [Theory]
    [InlineData("!helloworld")]
    [InlineData("say !hello")]
    [InlineData("")]
    public void StartsWith_rejects_other_text(string message) =>
        Assert.Null(CommandMatcher.Match(message, "!hello", CommandMatchMode.StartsWith, caseSensitive: false));

    [Fact]
    public void Exact_requires_nothing_after_the_command()
    {
        Assert.NotNull(CommandMatcher.Match(" !lurk ", "!lurk", CommandMatchMode.Exact, caseSensitive: false));
        Assert.Null(CommandMatcher.Match("!lurk now", "!lurk", CommandMatchMode.Exact, caseSensitive: false));
    }

    [Fact]
    public void Case_sensitivity_is_optional()
    {
        Assert.Null(CommandMatcher.Match("!Hello", "!hello", CommandMatchMode.StartsWith, caseSensitive: true));
        Assert.NotNull(CommandMatcher.Match("!hello", "!hello", CommandMatchMode.StartsWith, caseSensitive: true));
    }

    [Fact]
    public void Words_are_split_on_any_whitespace() =>
        Assert.Equal(["a", "b", "c"], CommandMatcher.Match("!so a\tb   c", "!so", CommandMatchMode.StartsWith, false)!.Words);

    [Fact]
    public void Empty_command_never_matches() =>
        Assert.Null(CommandMatcher.Match("!hello", "  ", CommandMatchMode.StartsWith, false));
}

public sealed class CommandTriggerTests
{
    private readonly FakeTimeProvider _time = new();
    private readonly ITriggerType _trigger;
    private readonly TriggerDefinition _definition = new() { Type = CommandTriggerType.Id };

    public CommandTriggerTests() =>
        _trigger = new TwitchTriggers(_time).GetTriggers().Single(t => t.TypeId == CommandTriggerType.Id);

    private static BotEvent Chat(string message, string userId = "u1", string role = "") =>
        new(TwitchEventTypes.ChatMessage, new Dictionary<string, object?>
        {
            ["message"] = message,
            ["rawInput"] = message,
            ["userId"] = userId,
            ["isBroadcaster"] = role == "broadcaster",
            ["isModerator"] = role == "moderator",
            ["isVip"] = role == "vip",
            ["isSubscriber"] = role == "subscriber",
        });

    private bool Matches(BotEvent e, Dictionary<string, string> settings, out Dictionary<string, object?> args)
    {
        args = new Dictionary<string, object?>(e.Args, StringComparer.OrdinalIgnoreCase);
        return _trigger.Matches(e, _definition, ParameterValues.From(_trigger.Parameters, settings), args);
    }

    private bool Matches(BotEvent e, Dictionary<string, string> settings) => Matches(e, settings, out _);

    [Fact]
    public void Listens_to_chat_messages() => Assert.Equal([TwitchEventTypes.ChatMessage], _trigger.EventTypes);

    [Fact]
    public void Match_adds_command_arguments()
    {
        Assert.True(Matches(Chat("!so  Streamer  hype"), new() { ["command"] = "!so" }, out var args));

        Assert.Equal("!so", args["command"]);
        Assert.Equal("Streamer  hype", args["input"]);
        Assert.Equal("!so  Streamer  hype", args["rawInput"]);
        Assert.Equal("Streamer", args["input0"]);
        Assert.Equal("hype", args["input1"]);
        Assert.Equal(2, args["inputCount"]);
    }

    [Fact]
    public void Exact_mode_from_settings()
    {
        var settings = new Dictionary<string, string> { ["command"] = "!lurk", ["match"] = "Exact" };

        Assert.True(Matches(Chat("!lurk"), settings));
        Assert.False(Matches(Chat("!lurk please"), settings));
    }

    [Fact]
    public void Case_sensitive_setting()
    {
        var settings = new Dictionary<string, string> { ["command"] = "!Hi", ["caseSensitive"] = "true" };

        Assert.False(Matches(Chat("!hi"), settings));
        Assert.True(Matches(Chat("!Hi"), settings));
    }

    [Theory]
    [InlineData("Everyone", "", true)]
    [InlineData("Subscriber", "", false)]
    [InlineData("Subscriber", "subscriber", true)]
    [InlineData("Subscriber", "vip", true)]
    [InlineData("VIP", "subscriber", false)]
    [InlineData("VIP", "vip", true)]
    [InlineData("VIP", "moderator", true)]
    [InlineData("Moderator", "vip", false)]
    [InlineData("Moderator", "broadcaster", true)]
    [InlineData("Broadcaster", "moderator", false)]
    [InlineData("Broadcaster", "broadcaster", true)]
    public void Permission_hierarchy(string permission, string role, bool expected) =>
        Assert.Equal(expected, Matches(Chat("!cmd", role: role), new() { ["command"] = "!cmd", ["permission"] = permission }));

    [Fact]
    public void Global_cooldown_blocks_everyone_until_it_expires()
    {
        var settings = new Dictionary<string, string> { ["command"] = "!cmd", ["globalCooldown"] = "30" };

        Assert.True(Matches(Chat("!cmd", "a"), settings));
        Assert.False(Matches(Chat("!cmd", "b"), settings));
        _time.Advance(TimeSpan.FromSeconds(29));
        Assert.False(Matches(Chat("!cmd", "b"), settings));
        _time.Advance(TimeSpan.FromSeconds(1));
        Assert.True(Matches(Chat("!cmd", "b"), settings));
    }

    [Fact]
    public void User_cooldown_only_blocks_that_user()
    {
        var settings = new Dictionary<string, string> { ["command"] = "!cmd", ["userCooldown"] = "60" };

        Assert.True(Matches(Chat("!cmd", "a"), settings));
        Assert.False(Matches(Chat("!cmd", "a"), settings));
        Assert.True(Matches(Chat("!cmd", "b"), settings));
        _time.Advance(TimeSpan.FromSeconds(60));
        Assert.True(Matches(Chat("!cmd", "a"), settings));
    }

    [Fact]
    public void Cooldowns_are_per_trigger()
    {
        var settings = new Dictionary<string, string> { ["command"] = "!cmd", ["globalCooldown"] = "30" };
        var other = new TriggerDefinition { Type = CommandTriggerType.Id };

        Assert.True(Matches(Chat("!cmd"), settings));
        Assert.True(_trigger.Matches(Chat("!cmd"), other, ParameterValues.From(_trigger.Parameters, settings),
            new Dictionary<string, object?>()));
    }

    [Fact]
    public void Rejected_messages_do_not_start_a_cooldown()
    {
        var settings = new Dictionary<string, string>
        {
            ["command"] = "!cmd", ["globalCooldown"] = "30", ["permission"] = "Moderator",
        };

        Assert.False(Matches(Chat("!cmd", "a"), settings));
        Assert.False(Matches(Chat("!other", "m", "moderator"), settings));
        Assert.True(Matches(Chat("!cmd", "m", "moderator"), settings));
    }
}

public sealed class EventTriggerTests
{
    private readonly IReadOnlyList<ITriggerType> _triggers = new TwitchTriggers(new FakeTimeProvider()).GetTriggers().ToArray();

    private bool Matches(string typeId, Dictionary<string, object?> eventArgs, Dictionary<string, string>? settings = null)
    {
        var trigger = _triggers.Single(t => t.TypeId == typeId);
        var e = new BotEvent(typeId, eventArgs);
        return trigger.Matches(e, new TriggerDefinition { Type = typeId },
            ParameterValues.From(trigger.Parameters, settings ?? []), new Dictionary<string, object?>());
    }

    [Fact]
    public void Every_event_type_has_a_trigger_in_the_Twitch_category()
    {
        string[] expected =
        [
            "twitch.chatMessage", "twitch.command", "twitch.follow", "twitch.sub", "twitch.resub", "twitch.giftSub",
            "twitch.cheer", "twitch.raid", "twitch.rewardRedemption", "twitch.streamOnline", "twitch.streamOffline",
        ];

        Assert.Equal(expected.Order(), _triggers.Select(t => t.TypeId).Order());
        Assert.All(_triggers, t => Assert.Equal("Twitch", t.Category));
    }

    [Fact]
    public void ChatMessage_contains_filter()
    {
        Assert.True(Matches("twitch.chatMessage", new() { ["message"] = "anything" }));
        Assert.True(Matches("twitch.chatMessage", new() { ["message"] = "I love HYPE trains" }, new() { ["contains"] = "hype" }));
        Assert.False(Matches("twitch.chatMessage", new() { ["message"] = "hello" }, new() { ["contains"] = "hype" }));
    }

    [Fact]
    public void GiftSub_minimum_total()
    {
        var settings = new Dictionary<string, string> { ["minTotal"] = "5" };

        Assert.False(Matches("twitch.giftSub", new() { ["total"] = 4 }, settings));
        Assert.True(Matches("twitch.giftSub", new() { ["total"] = 5 }, settings));
        Assert.True(Matches("twitch.giftSub", new() { ["total"] = 1 }));
    }

    [Fact]
    public void Cheer_bits_range()
    {
        var settings = new Dictionary<string, string> { ["minBits"] = "100", ["maxBits"] = "499" };

        Assert.False(Matches("twitch.cheer", new() { ["bits"] = 99 }, settings));
        Assert.True(Matches("twitch.cheer", new() { ["bits"] = 100 }, settings));
        Assert.True(Matches("twitch.cheer", new() { ["bits"] = 499 }, settings));
        Assert.False(Matches("twitch.cheer", new() { ["bits"] = 500 }, settings));
        Assert.True(Matches("twitch.cheer", new() { ["bits"] = 100000 }, new() { ["minBits"] = "100" }));
    }

    [Fact]
    public void Raid_minimum_viewers()
    {
        Assert.False(Matches("twitch.raid", new() { ["viewers"] = 9 }, new() { ["minViewers"] = "10" }));
        Assert.True(Matches("twitch.raid", new() { ["viewers"] = 10 }, new() { ["minViewers"] = "10" }));
    }

    [Fact]
    public void RewardRedemption_title_filter_is_optional_and_case_insensitive()
    {
        Assert.True(Matches("twitch.rewardRedemption", new() { ["rewardTitle"] = "Hydrate" }));
        Assert.True(Matches("twitch.rewardRedemption", new() { ["rewardTitle"] = "Hydrate" }, new() { ["rewardTitle"] = " hydrate " }));
        Assert.False(Matches("twitch.rewardRedemption", new() { ["rewardTitle"] = "Hydrate" }, new() { ["rewardTitle"] = "Stretch" }));
    }

    [Fact]
    public void Unfiltered_triggers_always_match()
    {
        foreach (var type in new[] { "twitch.follow", "twitch.sub", "twitch.resub", "twitch.streamOnline", "twitch.streamOffline" })
            Assert.True(Matches(type, []));
    }
}
