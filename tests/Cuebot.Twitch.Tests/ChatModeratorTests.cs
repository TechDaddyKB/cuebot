using Cuebot.Core.Chat.Moderation;
using Cuebot.Twitch.Tests.Support;

namespace Cuebot.Twitch.Tests;

/// <summary>Delete, timeout and ban from the combined chat (the Chatter pane and the message menu).</summary>
public sealed class ChatModeratorTests : IDisposable
{
    private readonly ApiHarness _h = new();
    private bool _connected = true;

    public void Dispose() => _h.Dispose();

    private TwitchChatModerator Moderator() => new(() => _h.Api, () => _connected);

    [Fact]
    public void Offers_what_the_granted_scopes_allow_while_connected()
    {
        var moderator = Moderator();
        Assert.Equal("Twitch", moderator.Platform);
        Assert.Equal(ChatModerationActions.DeleteMessage | ChatModerationActions.Timeout | ChatModerationActions.Ban, moderator.Available);

        _h.Settings.Value.Scopes = [TwitchScopes.ManageChatMessages];
        Assert.Equal(ChatModerationActions.DeleteMessage, moderator.Available);
        _h.Settings.Value.Scopes = [TwitchScopes.ManageBannedUsers];
        Assert.Equal(ChatModerationActions.Timeout | ChatModerationActions.Ban, moderator.Available);

        _connected = false;
        Assert.Equal(ChatModerationActions.None, moderator.Available);
    }

    [Fact]
    public async Task Deletes_times_out_and_bans_through_helix_as_the_broadcaster()
    {
        _h.On("DELETE /helix/moderation/chat", "");
        _h.On("POST /helix/moderation/bans", """{"data":[{}]}""");
        var moderator = Moderator();

        await moderator.DeleteMessageAsync("msg-9");
        Assert.Equal(("DELETE", "/helix/moderation/chat"), (_h.Last.Method.Method, _h.Last.Path));
        Assert.Equal("msg-9", _h.LastQuery["message_id"]);

        await moderator.TimeoutAsync("4242", "bob", TimeSpan.FromMinutes(10), null);
        Assert.Equal(("POST", "/helix/moderation/bans"), (_h.Last.Method.Method, _h.Last.Path));
        Assert.Equal("4242", _h.LastBody.GetProperty("data").GetProperty("user_id").GetString());
        Assert.Equal(600, _h.LastBody.GetProperty("data").GetProperty("duration").GetInt32());

        await moderator.BanAsync("4242", "bob", "spam");
        Assert.False(_h.LastBody.GetProperty("data").TryGetProperty("duration", out _));
        Assert.Equal("spam", _h.LastBody.GetProperty("data").GetProperty("reason").GetString());
    }
}
