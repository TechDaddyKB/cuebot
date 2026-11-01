using System.Diagnostics;
using System.Net;
using Cuebot.Discord.Tests.Support;

namespace Cuebot.Discord.Tests;

public sealed class RestTests
{
    private readonly FakeDiscordHttp _http = new();

    private DiscordApi Api(TimeSpan? maxWait = null) =>
        new(new DiscordRestClient(new HttpClient(_http), () => TestToken.Value, maxRateLimitWait: maxWait));

    [Fact]
    public async Task Messages_send_the_bot_token_user_agent_and_embed_json()
    {
        var id = await Api().CreateMessageAsync(Ids.General, "I'm live!",
            new DiscordEmbed("Live now", "Playing a game", "https://twitch.tv/me", "#9146FF", "https://example.com/thumb.png"), CancellationToken.None);
        Assert.Equal("600000000000000001", id);
        var r = Assert.Single(_http.Requests);
        Assert.Equal(HttpMethod.Post, r.Method);
        Assert.Equal($"channels/{Ids.General}/messages", r.Path);
        Assert.Equal("Bot " + TestToken.Value, r.Authorization);
        Assert.StartsWith("DiscordBot (", r.Headers["User-Agent"]);
        var json = r.Json!;
        Assert.Equal("I'm live!", json["content"]!.GetValue<string>());
        var embed = json["embeds"]![0]!;
        Assert.Equal("Live now", embed["title"]!.GetValue<string>());
        Assert.Equal("Playing a game", embed["description"]!.GetValue<string>());
        Assert.Equal("https://twitch.tv/me", embed["url"]!.GetValue<string>());
        Assert.Equal(0x9146FF, embed["color"]!.GetValue<int>());
        Assert.Equal("https://example.com/thumb.png", embed["image"]!["url"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_429_is_retried_after_retry_after()
    {
        var calls = 0;
        _http.Handler = _ => Interlocked.Increment(ref calls) == 1
            ? FakeDiscordHttp.Json(new { message = "You are being rate limited.", retry_after = 0.2, global = false }, (HttpStatusCode)429)
            : null;
        var watch = Stopwatch.StartNew();
        await Api().CreateMessageAsync(Ids.General, "hi", null, CancellationToken.None);
        Assert.Equal(2, _http.Requests.Count);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(150), $"waited only {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task An_empty_bucket_waits_for_its_reset_before_the_next_call_on_that_route()
    {
        _http.Handler = _ => FakeDiscordHttp.Json(new { id = "1" }, headers: new Dictionary<string, string>
        {
            ["X-RateLimit-Limit"] = "5", ["X-RateLimit-Remaining"] = "0", ["X-RateLimit-Reset-After"] = "0.3", ["X-RateLimit-Bucket"] = "abc",
        });
        var api = Api();
        await api.CreateMessageAsync(Ids.General, "one", null, CancellationToken.None);
        var watch = Stopwatch.StartNew();
        await api.CreateMessageAsync(Ids.General, "two", null, CancellationToken.None);
        Assert.True(watch.Elapsed >= TimeSpan.FromMilliseconds(200), $"waited only {watch.ElapsedMilliseconds} ms");
        // Another channel is another route: no wait.
        watch.Restart();
        await api.CreateMessageAsync(Ids.Announcements, "three", null, CancellationToken.None);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(200));
    }

    [Fact]
    public async Task A_long_rate_limit_fails_with_a_plain_message_instead_of_hanging()
    {
        _http.Handler = _ => FakeDiscordHttp.Json(new { message = "limited", retry_after = 120.0, global = true }, (HttpStatusCode)429);
        var ex = await Assert.ThrowsAsync<DiscordApiException>(() => Api(TimeSpan.FromSeconds(5)).CreateMessageAsync(Ids.General, "hi", null, CancellationToken.None));
        Assert.Contains("rate limiting", ex.Message);
        Assert.Single(_http.Requests);
    }

    [Theory]
    [InlineData(50013, HttpStatusCode.Forbidden, "Send Messages")]
    [InlineData(50001, HttpStatusCode.Forbidden, "View Channel")]
    [InlineData(10003, HttpStatusCode.NotFound, "channel")]
    [InlineData(0, HttpStatusCode.Unauthorized, "Reset Token")]
    public async Task Errors_become_plain_messages(int code, HttpStatusCode status, string expected)
    {
        _http.Handler = _ => FakeDiscordHttp.Json(new { message = "x", code }, status);
        var ex = await Assert.ThrowsAsync<DiscordApiException>(() => Api().CreateMessageAsync(Ids.General, "hi", null, CancellationToken.None));
        Assert.Contains(expected, ex.Message);
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public async Task Invalid_form_body_names_the_field()
    {
        _http.Handler = _ => FakeDiscordHttp.Json(new
        {
            code = 50035, message = "Invalid Form Body",
            errors = new { embeds = new Dictionary<string, object> { ["0"] = new { url = new { _errors = new[] { new { code = "URL_TYPE_INVALID_URL", message = "Not a well formed URL." } } } } } },
        }, HttpStatusCode.BadRequest);
        var ex = await Assert.ThrowsAsync<DiscordApiException>(() => Api().CreateMessageAsync(Ids.General, "hi", null, CancellationToken.None));
        Assert.Contains("embeds.0.url: Not a well formed URL.", ex.Message);
    }

    [Fact]
    public async Task Interaction_calls_need_no_bot_token()
    {
        var api = Api();
        await api.DeferInteractionAsync("910000000000000001", "tok", ephemeral: false, CancellationToken.None);
        await api.EditOriginalResponseAsync("100000000000000001", "tok", "hi", null, CancellationToken.None);
        Assert.All(_http.Requests, r => Assert.Null(r.Authorization));
        Assert.Equal("webhooks/100000000000000001/tok/messages/@original", _http.Requests[1].Path);
    }

    [Fact]
    public void Payloads_validate_like_discord()
    {
        Assert.Throws<ArgumentException>(() => DiscordPayloads.Message(new string('x', 2001), null));
        Assert.Equal(0x9146FF, DiscordPayloads.ParseColor("#9146ff"));
        Assert.Equal(0x9146FF, DiscordPayloads.ParseColor("twitch"));
        Assert.Equal(255, DiscordPayloads.ParseColor("255"));
        Assert.Null(DiscordPayloads.ParseColor(""));
        Assert.Throws<ArgumentException>(() => DiscordPayloads.ParseColor("not a colour"));
        Assert.Throws<ArgumentException>(() => DiscordPayloads.Commands([new DiscordSlashCommand { Name = "my command" }]));
        Assert.Throws<ArgumentException>(() => DiscordPayloads.Commands([new DiscordSlashCommand { Name = "a" }, new DiscordSlashCommand { Name = "A" }]));
        Assert.Equal("notify", DiscordPayloads.Commands([new DiscordSlashCommand { Name = "/Notify" }])[0]!["name"]!.GetValue<string>());
        Assert.Equal("notify", DiscordPayloads.Commands([new DiscordSlashCommand { Name = "notify" }])[0]!["description"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("POST", "channels/1234567890123/messages", "POST channels/1234567890123/messages")]
    [InlineData("PATCH", "channels/1234567890123/messages/9876543210987", "PATCH channels/1234567890123/messages/:id")]
    [InlineData("PUT", "guilds/111111111111/members/222222222222/roles/333333333333", "PUT guilds/111111111111/members/:id/roles/:id")]
    [InlineData("POST", "interactions/111111111111/tok/callback", "POST interactions/:id/:id/callback")]
    [InlineData("PATCH", "webhooks/111111111111/tok/messages/@original", "PATCH webhooks/111111111111/tok/messages/@original")]
    public void Route_keys_keep_major_parameters(string method, string path, string expected) =>
        Assert.Equal(expected, DiscordRestClient.RouteKey(new HttpMethod(method), path));

    [Fact]
    public void The_invite_link_asks_for_bot_and_commands_with_the_documented_permissions()
    {
        Assert.Equal(2416012288L, DiscordBotService.InvitePermissions);
        Assert.Equal("https://discord.com/oauth2/authorize?client_id=123&scope=bot%20applications.commands&permissions=2416012288",
            DiscordBotService.InviteUrl("123"));
    }

    [Fact]
    public void Targets_read_ids_mentions_suggestions_and_links()
    {
        Assert.Equal(Ids.General, DiscordTargets.Id($"#general · My Server · {Ids.General}"));
        Assert.Equal(Ids.User, DiscordTargets.Id($"<@!{Ids.User}>"));
        Assert.Equal(Ids.LiveRole, DiscordTargets.Id($"<@&{Ids.LiveRole}>"));
        Assert.Null(DiscordTargets.Id("general"));
        var globals = new Cuebot.Core.Actions.GlobalVariables(null, new Cuebot.Core.Logging.AppLog());
        globals.Set("live post", $"https://discord.com/channels/{Ids.Guild}/{Ids.General}/600000000000000001", persist: false);
        Assert.Equal((Ids.General, "600000000000000001"), DiscordTargets.Message("live post", globals));
        Assert.Equal((Ids.General, "600000000000000001"), DiscordTargets.Message($"{Ids.General}/600000000000000001", globals));
        Assert.Throws<ArgumentException>(() => DiscordTargets.Message("unknown name", globals));
    }
}
