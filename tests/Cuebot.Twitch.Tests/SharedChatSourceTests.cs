using System.Text.Json;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Twitch.EventSub;
using Cuebot.Twitch.Helix;
using Cuebot.Twitch.Tests.Support;

namespace Cuebot.Twitch.Tests;

/// <summary>Which channel a Shared Chat message came from: the variables and the per-session lookup of names and pictures.</summary>
public sealed class SharedChatSourceTests
{
    private const string Partner = "https://example.test/partner.png";
    private readonly TwitchEventRouter _router = new(new FakeTimeProvider(), () => "1337", () => "", new SentMessageTracker());

    private static string Message(string id, string source) => $$"""
        {"broadcaster_user_id":"1337","broadcaster_user_login":"streamer","broadcaster_user_name":"Streamer",
         "chatter_user_id":"9","chatter_user_login":"viewer","chatter_user_name":"Viewer","message_id":"{{id}}",
         "message":{"text":"hi","fragments":[]},"color":"","badges":[],"message_type":"text",
         "source_broadcaster_user_id":{{source}},"source_broadcaster_user_login":"partner","source_broadcaster_user_name":"Partner",
         "source_message_id":"src","source_badges":[{"set_id":"moderator","id":"1","info":""},{"set_id":"subscriber","id":"12","info":"12"}],
         "is_source_only":true}
        """;

    private BotEvent Chat(string id, string source = "\"2002\"") =>
        _router.Route("channel.chat.message", JsonSerializer.Deserialize<JsonElement>(Message(id, source))).First(e => e.Type == TwitchEventTypes.ChatMessage);

    private static TwitchUser User(string id, string avatar) => new(id, "partner", "Partner", "", "", "", avatar, "", null);

    [Fact]
    public void A_shared_message_has_its_source_badges_and_source_only_flag()
    {
        var e = Chat("m1");

        Assert.Equal("moderator/1,subscriber/12", e.Args["sourceBadges"]);
        Assert.Equal(true, e.Args["isSourceOnly"]);
        Assert.Equal("Partner", e.Args["sourceBroadcaster"]);
    }

    [Fact]
    public void The_picture_appears_once_the_channel_was_looked_up()
    {
        Assert.Equal("", Chat("m1").Args["sourceBroadcasterProfileUrl"]);

        _router.SeedSharedChatUsers([User("2002", Partner)]);

        Assert.Equal(Partner, Chat("m2").Args["sourceBroadcasterProfileUrl"]);
    }

    [Fact]
    public void A_message_from_the_own_channel_gets_no_source_picture()
    {
        _router.SeedSharedChatUsers([User("1337", Partner)]);

        var e = Chat("m1", "\"1337\"");

        Assert.Equal(false, e.Args["isSharedChat"]);
        Assert.Equal("", e.Args["sourceBroadcasterProfileUrl"]);
    }

    [Fact]
    public void Each_channel_is_looked_up_once_per_session()
    {
        Assert.Equal(["2002", "3003"], _router.ClaimSharedChatLookups(["2002", "3003", ""]));
        Assert.Empty(_router.ClaimSharedChatLookups(["2002", "3003"]));

        _router.SeedSharedChatUsers([User("4004", Partner)]);
        Assert.Empty(_router.ClaimSharedChatLookups(["4004"]));
    }

    [Fact]
    public void The_end_of_a_session_forgets_its_channels()
    {
        _router.SeedSharedChatUsers([User("2002", Partner)]);
        _router.ClaimSharedChatLookups(["3003"]);

        _router.Route(new BotEvent(TwitchEventTypes.SharedChatEnd, new Dictionary<string, object?>()));

        Assert.Equal("", Chat("m1").Args["sourceBroadcasterProfileUrl"]);
        Assert.Equal(["3003"], _router.ClaimSharedChatLookups(["3003"]));
    }

    [Fact]
    public void A_session_event_carries_the_participant_ids()
    {
        var e = EventSubMapper.Map("channel.shared_chat.begin", JsonSerializer.Deserialize<JsonElement>("""
            {"session_id":"s","broadcaster_user_id":"1337","host_broadcaster_user_id":"2002","host_broadcaster_user_login":"partner","host_broadcaster_user_name":"Partner",
             "participants":[{"broadcaster_user_id":"2002","broadcaster_user_login":"partner"},{"broadcaster_user_id":"1337","broadcaster_user_login":"streamer"}]}
            """), "1337")!;

        Assert.Equal("2002,1337", e.Args["participantIds"]);
    }
}
