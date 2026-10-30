using System.Net;
using System.Text.Json;
using Cuebot.Twitch.Tests.Support;

namespace Cuebot.Twitch.Tests;

public sealed class TwitchApiTests : IDisposable
{
    private readonly ApiHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Calls_need_a_sign_in()
    {
        _h.Settings.Value.AccessToken = "";

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => _h.Api.GetChannelAsync());

        Assert.Contains("Sign in", error.Message);
        Assert.Empty(_h.Http.Requests);
    }

    [Theory]
    [InlineData(TwitchScopes.ManageBroadcast)]
    [InlineData(TwitchScopes.ManagePolls)]
    [InlineData(TwitchScopes.EditClips)]
    public async Task Missing_scope_names_the_scope_and_where_to_enable_it(string scope)
    {
        _h.Settings.Value.Scopes = [.. TwitchScopes.All.Where(s => s != scope)];

        var error = await Assert.ThrowsAsync<TwitchApiException>(() => scope switch
        {
            TwitchScopes.ManageBroadcast => _h.Api.UpdateChannelAsync(new ChannelUpdate { Title = "x" }),
            TwitchScopes.ManagePolls => _h.Api.CreatePollAsync("q", ["a", "b"], 60),
            _ => _h.Api.CreateClipAsync(),
        });

        Assert.Contains(scope, error.Message);
        Assert.Contains("Settings", error.Message);
        Assert.Equal(403, error.StatusCode);
        Assert.Empty(_h.Http.Requests);
    }

    [Fact]
    public async Task Rewards_are_readable_with_either_redemption_scope()
    {
        _h.Settings.Value.Scopes = [TwitchScopes.ReadRedemptions];
        _h.On("GET /helix/channel_points/custom_rewards", """{"data":[{"id":"r1","title":"T","cost":1}]}""");

        var rewards = await _h.Api.GetRewardsAsync();

        Assert.False(Assert.Single(rewards).IsManageable);
        Assert.Single(_h.Http.Requests); // no "only manageable" query without the manage scope
    }

    [Fact]
    public async Task Forbidden_from_Twitch_explains_the_scope_settings()
    {
        _h.On("POST /helix/raids", """{"error":"Forbidden","status":403,"message":"missing scope"}""", HttpStatusCode.Forbidden);

        var error = await Assert.ThrowsAsync<TwitchApiException>(() => _h.Api.StartRaidAsync("target"));

        Assert.Contains("Starting raid failed (403): missing scope", error.Message);
        Assert.Contains("Settings tab", error.Message);
        Assert.Empty(_h.Published);
    }

    [Fact]
    public async Task Chat_badges_merge_global_and_channel_sets()
    {
        _h.On("GET /helix/chat/badges/global", """
            {"data":[
              {"set_id":"moderator","versions":[{"id":"1","image_url_1x":"https://g/mod1x","image_url_4x":"https://g/mod4x","title":"Moderator"}]},
              {"set_id":"subscriber","versions":[{"id":"0","image_url_4x":"https://g/sub0","title":"Subscriber"}]}
            ]}
            """);
        _h.On("GET /helix/chat/badges", """
            {"data":[{"set_id":"subscriber","versions":[
              {"id":"0","image_url_4x":"https://c/sub0","title":"Channel sub"},
              {"id":"12","image_url_2x":"https://c/sub12-2x","title":"1-Year"}]}]}
            """);

        var badges = await _h.Api.GetChatBadgesAsync();

        Assert.Equal(("https://g/mod4x", "Moderator"), badges["moderator/1"]);
        Assert.Equal(("https://c/sub0", "Channel sub"), badges["subscriber/0"]);   // the channel's own badge wins
        Assert.Equal("https://c/sub12-2x", badges["subscriber/12"].Url);          // largest image there is
        Assert.Contains(_h.Http.Requests, r => r.Query.StartsWith("broadcaster_id=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Starting_a_raid_publishes_raid_start()
    {
        _h.On("POST /helix/raids", """{"data":[{"created_at":"2026-01-01T12:00:00Z","is_mature":false}]}""");

        await _h.Api.StartRaidAsync("@Target");

        var e = Assert.Single(_h.Published);
        Assert.Equal(TwitchEventTypes.RaidStart, e.Type);
        Assert.Equal("Target", e.Args["user"]);
        Assert.Equal("4242", e.Args["userId"]);
        Assert.Equal("login=target", _h.Http.Requests[0].Query);
    }

    [Theory]
    [InlineData(TwitchSender.Default, true, true, TwitchSender.Bot)]
    [InlineData(TwitchSender.Default, true, false, TwitchSender.Broadcaster)]
    [InlineData(TwitchSender.Default, false, true, TwitchSender.Broadcaster)]
    [InlineData(TwitchSender.Broadcaster, true, true, TwitchSender.Broadcaster)]
    [InlineData(TwitchSender.Bot, true, false, TwitchSender.Bot)]
    public void Sender_selection(TwitchSender requested, bool botReady, bool preferBot, TwitchSender expected) =>
        Assert.Equal(expected, TwitchApi.ChooseSender(requested, botReady, preferBot));

    [Fact]
    public void Asking_for_the_bot_without_one_is_an_error() =>
        Assert.Throws<InvalidOperationException>(() => TwitchApi.ChooseSender(TwitchSender.Bot, botReady: false, preferBot: true));

    [Fact]
    public async Task Chat_and_whispers_go_out_from_the_bot_when_it_is_signed_in()
    {
        _h.SignInBot();

        Assert.Equal(TwitchSender.Bot, await _h.Api.SendChatMessageAsync("hi"));
        var chat = _h.Last;
        Assert.Equal("Bearer bot-token", chat.Authorization);
        Assert.Equal("777", JsonSerializer.Deserialize<JsonElement>(chat.Body).GetProperty("sender_id").GetString());
        Assert.True(_h.Sent.Contains("sent-1"));

        await _h.Api.SendWhisperAsync("target", "psst");
        Assert.Equal("Bearer bot-token", _h.Last.Authorization);
        Assert.Equal("from_user_id=777&to_user_id=4242", _h.Last.Query);

        Assert.Equal(TwitchSender.Broadcaster, await _h.Api.SendChatMessageAsync("as me", TwitchSender.Broadcaster));
        Assert.Equal("Bearer broadcaster-token", _h.Last.Authorization);
    }

    [Fact]
    public async Task A_bot_without_the_whisper_scope_leaves_whispers_to_the_broadcaster()
    {
        _h.SignInBot(TwitchScopes.WriteChat);

        await _h.Api.SendWhisperAsync("target", "psst");
        Assert.Equal("from_user_id=1337&to_user_id=4242", _h.Last.Query);

        var error = await Assert.ThrowsAsync<TwitchApiException>(() => _h.Api.SendWhisperAsync("target", "psst", TwitchSender.Bot));
        Assert.Contains("the bot account", error.Message);
        Assert.Contains(TwitchScopes.ManageWhispers, error.Message);
    }

    [Fact]
    public async Task Preferences_can_keep_chat_on_the_broadcaster()
    {
        _h.SignInBot();
        _h.Settings.Value.SendChatAsBot = false;

        Assert.Equal(TwitchSender.Broadcaster, await _h.Api.SendChatMessageAsync("hi"));
    }

    [Fact]
    public async Task Chat_needs_the_connection()
    {
        using var offline = new ApiHarness(connected: false);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => offline.Api.SendChatMessageAsync("hi"));
        Assert.Contains("not connected", error.Message);
    }

    [Theory]
    [InlineData("@SomeOne", "login", "someone")]
    [InlineData(" viewer ", "login", "viewer")]
    [InlineData("id:12345", "id", "12345")]
    public void User_queries(string input, string key, string value) =>
        Assert.Equal((key, value), TwitchApi.UserQuery(input));

    [Fact]
    public async Task Unknown_user_is_reported()
    {
        _h.On("GET /helix/users", """{"data":[]}""");
        var error = await Assert.ThrowsAsync<TwitchApiException>(() => _h.Api.SendShoutoutAsync("ghost"));
        Assert.Contains("'ghost' was not found", error.Message);
    }

    [Fact]
    public void Tags_are_cleaned_and_limited()
    {
        Assert.Equal(["English", "Cozy"], TwitchApi.NormalizeTags(["#English", " Co zy ", "english"]));
        Assert.Throws<ArgumentException>(() => TwitchApi.NormalizeTags(Enumerable.Range(0, 11).Select(i => "t" + i)));
        Assert.Throws<ArgumentException>(() => TwitchApi.NormalizeTags([new string('a', 26)]));
    }

    [Fact]
    public async Task Poll_and_prediction_option_counts_are_validated()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => _h.Api.CreatePollAsync("q", ["only one"], 60));
        await Assert.ThrowsAsync<ArgumentException>(() => _h.Api.CreatePollAsync("q", ["1", "2", "3", "4", "5", "6"], 60));
        await Assert.ThrowsAsync<ArgumentException>(() => _h.Api.CreatePredictionAsync("q", ["yes"], 60));
        Assert.Empty(_h.Http.Requests);
    }

    [Fact]
    public async Task Durations_are_clamped_to_Twitch_limits()
    {
        await _h.Api.TimeoutAsync("1", 99_999_999);
        Assert.Equal(1_209_600, JsonSerializer.Deserialize<JsonElement>(_h.Last.Body).GetProperty("data").GetProperty("duration").GetInt32());
        await _h.Api.RunCommercialAsync(5);
        Assert.Equal(30, JsonSerializer.Deserialize<JsonElement>(_h.Last.Body).GetProperty("length").GetInt32());
    }

    [Fact]
    public async Task Ad_schedule_reads_unix_times()
    {
        _h.On("GET /helix/channels/ads",
            """{"data":[{"next_ad_at":"1767268800","last_ad_at":"","duration":"60","preroll_free_time":90,"snooze_count":1,"snooze_refresh_at":1767268800}]}""");

        var schedule = await _h.Api.GetAdScheduleAsync();

        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767268800), schedule.NextAdAt);
        Assert.Null(schedule.LastAdAt);
        Assert.Equal(90, schedule.PrerollFreeTime);
        Assert.Equal(1, schedule.SnoozeCount);
    }

    [Fact]
    public void Money_uses_decimal_places() =>
        Assert.Equal("15.50", TwitchApi.Money(JsonSerializer.Deserialize<JsonElement>("""{"value":1550,"decimal_places":2,"currency":"USD"}""")));

    [Fact]
    public void Reward_body_turns_limits_into_enable_flags()
    {
        var body = TwitchApi.RewardBody(new RewardUpdate { MaxPerStream = 0, GlobalCooldownSeconds = 30, BackgroundColor = "112233" });

        Assert.Equal(false, body["is_max_per_stream_enabled"]);
        Assert.False(body.ContainsKey("max_per_stream"));
        Assert.Equal(true, body["is_global_cooldown_enabled"]);
        Assert.Equal(30, body["global_cooldown_seconds"]);
        Assert.Equal("#112233", body["background_color"]);
        Assert.False(body.ContainsKey("title"));
    }

    [Fact]
    public async Task Chat_settings_come_back_parsed()
    {
        _h.On("GET /helix/chat/settings",
            """{"data":[{"broadcaster_id":"1337","slow_mode":true,"slow_mode_wait_time":10,"follower_mode":true,"follower_mode_duration":0,"subscriber_mode":false,"emote_mode":true,"unique_chat_mode":false}]}""");

        var settings = await _h.Api.GetChatSettingsAsync();

        Assert.Equal(new TwitchChatSettings(true, true, 0, true, 10, false, false), settings);
    }
}
