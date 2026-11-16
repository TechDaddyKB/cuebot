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

    [Fact]
    public void Capabilities_give_limits_and_name_the_missing_permission()
    {
        var moderator = Moderator();
        var timeout = moderator.Capability(ModerationAction.Timeout);
        Assert.Equal((ModerationToolState.Works, TimeSpan.FromSeconds(1), TimeSpan.FromDays(14), 500),
            (timeout.State, timeout.MinDuration, timeout.MaxDuration, timeout.MaxReasonLength));
        Assert.Equal(TimeSpan.FromDays(14), timeout.Apply(TimeSpan.FromDays(30)));
        Assert.All(ModerationCapability.AllActions, a => Assert.Equal(ModerationToolState.Works, moderator.Capability(a).State));

        _h.Settings.Value.Scopes = [TwitchScopes.ManageBannedUsers];
        var warn = moderator.Capability(ModerationAction.Warn);
        Assert.Equal(ModerationToolState.NotAvailable, warn.State);
        Assert.Contains("Sign in to Twitch again", warn.Note);
        Assert.Equal(ModerationToolState.NotAvailable, moderator.Capability(ModerationAction.SlowMode).State);
        Assert.Equal(ModerationToolState.Works, moderator.Capability(ModerationAction.Unban).State);

        _connected = false;
        Assert.Equal("Connect Twitch first.", moderator.Capability(ModerationAction.Ban).Note);
    }

    [Fact]
    public async Task Unban_warn_clear_modes_and_pins_go_through_helix()
    {
        _h.On("DELETE /helix/moderation/bans", "");
        _h.On("POST /helix/moderation/warnings", """{"data":[{}]}""");
        _h.On("DELETE /helix/moderation/chat", "");
        _h.On("PATCH /helix/chat/settings", """{"data":[{}]}""");
        _h.On("PUT /helix/chat/pins", "");
        _h.On("POST /helix/moderation/bans", """{"data":[{}]}""");
        var moderator = Moderator();

        Assert.True((await moderator.ExecuteAsync(new ModerationRequest(ModerationAction.Unban) { UserId = "4242" })).Ok);
        Assert.Equal(("DELETE", "/helix/moderation/bans", "4242"), (_h.Last.Method.Method, _h.Last.Path, _h.LastQuery["user_id"]));

        await moderator.ExecuteAsync(new ModerationRequest(ModerationAction.Warn) { UserId = "4242", Reason = "[mod: Sam] language" });
        Assert.Equal("[mod: Sam] language", _h.LastBody.GetProperty("data").GetProperty("reason").GetString());

        await moderator.ExecuteAsync(new ModerationRequest(ModerationAction.Clear));
        Assert.Equal(("DELETE", "/helix/moderation/chat"), (_h.Last.Method.Method, _h.Last.Path));
        Assert.False(_h.LastQuery.ContainsKey("message_id"));

        await moderator.ExecuteAsync(new ModerationRequest(ModerationAction.SlowMode) { ModeValue = 500 });
        Assert.Equal((true, 120), (_h.LastBody.GetProperty("slow_mode").GetBoolean(), _h.LastBody.GetProperty("slow_mode_wait_time").GetInt32()));
        await moderator.ExecuteAsync(new ModerationRequest(ModerationAction.EmoteOnly) { Enabled = false });
        Assert.False(_h.LastBody.GetProperty("emote_mode").GetBoolean());
        await moderator.ExecuteAsync(new ModerationRequest(ModerationAction.FollowersOnly) { ModeValue = 10 });
        Assert.Equal(10, _h.LastBody.GetProperty("follower_mode_duration").GetInt32());
        await moderator.ExecuteAsync(new ModerationRequest(ModerationAction.SubscribersOnly));
        Assert.True(_h.LastBody.GetProperty("subscriber_mode").GetBoolean());

        await moderator.ExecuteAsync(new ModerationRequest(ModerationAction.Pin) { MessageId = "msg-1" });
        Assert.Equal(("PUT", "msg-1"), (_h.Last.Method.Method, _h.LastQuery["message_id"]));

        var outcome = await moderator.ExecuteAsync(new ModerationRequest(ModerationAction.Timeout) { UserId = "4242", Duration = TimeSpan.FromDays(20) });
        Assert.Equal(TimeSpan.FromDays(14), outcome.AppliedDuration);
        Assert.Equal(1_209_600, _h.LastBody.GetProperty("data").GetProperty("duration").GetInt32());
    }

    [Fact]
    public async Task A_missing_scope_fails_with_the_reason_instead_of_calling_twitch()
    {
        _h.Settings.Value.Scopes = [TwitchScopes.ManageChatMessages];
        var outcome = await Moderator().ExecuteAsync(new ModerationRequest(ModerationAction.Ban) { UserId = "4242" });
        Assert.False(outcome.Ok);
        Assert.Contains("Sign in to Twitch again", outcome.Error);
    }

    [Fact]
    public void Broadcaster_and_bot_accounts_are_protected()
    {
        var accounts = new TwitchProtectedAccounts(() => _h.Api);
        var broadcaster = _h.Api.Broadcaster.UserId;
        Assert.Equal("This is your own Twitch account.", accounts.WhyProtected("Twitch", broadcaster, ""));
        Assert.Null(accounts.WhyProtected("Twitch", "someone-else", ""));
        Assert.Null(accounts.WhyProtected("Kick", broadcaster, ""));
    }
}
