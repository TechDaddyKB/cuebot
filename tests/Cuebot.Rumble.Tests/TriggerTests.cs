using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Rumble.Triggers;

namespace Cuebot.Rumble.Tests;

public sealed class CommandTriggerTests
{
    private readonly ManualTime _time = new();
    private readonly ITriggerType _trigger;
    private readonly TriggerDefinition _definition = new() { Type = CommandTriggerType.Id };

    public CommandTriggerTests() =>
        _trigger = new RumbleTriggers(_time).GetTriggers().Single(t => t.TypeId == CommandTriggerType.Id);

    private static BotEvent Chat(string message, string userId = "u1", string role = "", string user = "Bob") =>
        new(RumbleEventTypes.ChatMessage, new Dictionary<string, object?>
        {
            ["message"] = message,
            ["user"] = user,
            ["userId"] = userId,
            ["isBroadcaster"] = role == "broadcaster",
            ["isModerator"] = role == "moderator",
            ["isSubscriber"] = role == "subscriber",
        });

    private bool Matches(BotEvent e, Dictionary<string, string> settings, out Dictionary<string, object?> args)
    {
        args = new Dictionary<string, object?>(e.Args, StringComparer.OrdinalIgnoreCase);
        return _trigger.Matches(e, _definition, ParameterValues.From(_trigger.Parameters, settings), args);
    }

    private bool Matches(BotEvent e, Dictionary<string, string> settings) => Matches(e, settings, out _);

    [Fact]
    public void Listens_to_rumble_chat_and_adds_command_arguments()
    {
        Assert.Equal([RumbleEventTypes.ChatMessage], _trigger.EventTypes);
        Assert.Equal("Rumble", _trigger.Category);

        Assert.True(Matches(Chat("!so  alice  now"), new() { ["command"] = "!so" }, out var args));

        Assert.Equal("!so", args["command"]);
        Assert.Equal("alice  now", args["input"]);
        Assert.Equal("!so  alice  now", args["rawInput"]);
        Assert.Equal("alice", args["input0"]);
        Assert.Equal("now", args["input1"]);
        Assert.Equal(2, args["inputCount"]);
    }

    [Fact]
    public void Exact_mode_and_case_sensitivity_work_like_twitch()
    {
        Assert.False(Matches(Chat("!lurk now"), new() { ["command"] = "!lurk", ["match"] = "Exact" }));
        Assert.True(Matches(Chat(" !LURK "), new() { ["command"] = "!lurk", ["match"] = "Exact" }));
        Assert.False(Matches(Chat("!LURK"), new() { ["command"] = "!lurk", ["caseSensitive"] = "true" }));
        Assert.False(Matches(Chat("!lurker"), new() { ["command"] = "!lurk" }));
    }

    [Theory]
    [InlineData("Everyone", "", true)]
    [InlineData("Subscriber", "", false)]
    [InlineData("Subscriber", "subscriber", true)]
    [InlineData("Subscriber", "moderator", true)]
    [InlineData("Moderator", "subscriber", false)]
    [InlineData("Moderator", "broadcaster", true)]
    [InlineData("Broadcaster", "moderator", false)]
    public void Permission_levels_follow_rumble_roles(string permission, string role, bool expected) =>
        Assert.Equal(expected, Matches(Chat("!go", role: role), new() { ["command"] = "!go", ["permission"] = permission }));

    [Fact]
    public void Offers_the_rumble_permission_levels() =>
        Assert.Equal(["Everyone", "Subscriber", "Moderator", "Broadcaster"],
            _trigger.Parameters.Single(p => p.Key == "permission").Choices);

    [Fact]
    public void Per_user_cooldown_uses_the_username_when_the_api_gave_no_user_id()
    {
        var settings = new Dictionary<string, string> { ["command"] = "!go", ["userCooldown"] = "30" };

        Assert.True(Matches(Chat("!go", userId: "", user: "Bob"), settings));
        Assert.False(Matches(Chat("!go", userId: "", user: "bob"), settings));
        Assert.True(Matches(Chat("!go", userId: "", user: "Alice"), settings));

        _time.Advance(TimeSpan.FromSeconds(31));
        Assert.True(Matches(Chat("!go", userId: "", user: "Bob"), settings));
    }

    [Fact]
    public void Global_cooldown_blocks_everyone()
    {
        var settings = new Dictionary<string, string> { ["command"] = "!go", ["globalCooldown"] = "10" };

        Assert.True(Matches(Chat("!go", userId: "1"), settings));
        Assert.False(Matches(Chat("!go", userId: "2"), settings));
    }

    private sealed class ManualTime : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
    }
}

public sealed class EventTriggerTests
{
    private readonly IReadOnlyList<ITriggerType> _triggers = new RumbleTriggers().GetTriggers().ToArray();

    private bool Matches(string typeId, BotEvent e, Dictionary<string, string>? settings = null)
    {
        var trigger = _triggers.Single(t => t.TypeId == typeId);
        return trigger.Matches(e, new TriggerDefinition { Type = typeId },
            ParameterValues.From(trigger.Parameters, settings ?? []), new Dictionary<string, object?>());
    }

    [Fact]
    public void Every_published_event_has_a_trigger_in_the_rumble_category()
    {
        string[] events =
        [
            RumbleEventTypes.ChatMessage, RumbleEventTypes.Rant, RumbleEventTypes.Follow, RumbleEventTypes.Subscribe,
            RumbleEventTypes.GiftSub, RumbleEventTypes.StreamOnline, RumbleEventTypes.StreamOffline,
        ];

        Assert.All(events, type => Assert.Contains(_triggers, t => t.TypeId == type && t.EventTypes.Contains(type)));
        Assert.All(_triggers, t => Assert.Equal("Rumble", t.Category));
    }

    [Fact]
    public void Chat_trigger_filters_by_contained_text()
    {
        var chat = new BotEvent(RumbleEventTypes.ChatMessage, new Dictionary<string, object?> { ["message"] = "Hello World" });

        Assert.True(Matches(RumbleEventTypes.ChatMessage, chat));
        Assert.True(Matches(RumbleEventTypes.ChatMessage, chat, new() { ["contains"] = "world" }));
        Assert.False(Matches(RumbleEventTypes.ChatMessage, chat, new() { ["contains"] = "bye" }));
    }

    [Theory]
    [InlineData(500, "5", true)]
    [InlineData(499, "5", false)]
    [InlineData(100, "0", true)]
    [InlineData(250, "2.5", true)]
    public void Rant_trigger_has_a_minimum_amount_in_dollars(long cents, string minimum, bool expected) =>
        Assert.Equal(expected, Matches(RumbleEventTypes.Rant, RumbleEvents.Rant("Bob", "1", "hi", cents), new() { ["minAmount"] = minimum }));

    [Fact]
    public void Gift_trigger_has_a_minimum_total()
    {
        var gift = RumbleEvents.GiftSub(new LiveApi.ApiGiftedSub("Frank", 3, 3, "rumble", 1));

        Assert.True(Matches(RumbleEventTypes.GiftSub, gift, new() { ["minTotal"] = "3" }));
        Assert.False(Matches(RumbleEventTypes.GiftSub, gift, new() { ["minTotal"] = "4" }));
    }
}
