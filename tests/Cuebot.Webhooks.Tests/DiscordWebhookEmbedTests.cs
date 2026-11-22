using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Storage;
using Cuebot.Webhooks.Outgoing;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Webhooks.Tests;

/// <summary>Webhook JSON bodies, threads, edit and delete. Every request goes to a fake handler; nothing reaches Discord.</summary>
public sealed class DiscordWebhookEmbedTests : IAsyncDisposable
{
    private const string Url = "https://discord.com/api/webhooks/123456/sEcReT-ToKeN";
    private const string Sent = """{"id":"1337117683476336731","channel_id":"42"}""";

    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-webhook-embeds-").FullName;
    private readonly FakeHttpHandler _http = new() { ResponseBody = Sent };
    private readonly ServiceProvider _services;

    public DiscordWebhookEmbedTests()
    {
        var collection = new ServiceCollection().AddCuebotCore(new AppPaths(_dir)).AddWebhooks();
        collection.AddSingleton(new HttpClient(_http));
        _services = collection.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* settings flush */ }
    }

    private GlobalVariables Globals => _services.GetRequiredService<GlobalVariables>();

    private async Task<string?> RunAsync(string type, Dictionary<string, string> parameters)
    {
        var engine = _services.GetRequiredService<ActionEngine>();
        await engine.RunAsync(new ActionDefinition { Name = type, SubActions = [new SubActionDefinition { Type = type, Parameters = parameters }] });
        return engine.History.Entries.FirstOrDefault(e => e.Error is not null)?.Error;
    }

    private static string Same(string text) => text;

    private static Func<string, string> Vars(string name, string value) => text => text.Replace($"%{name}%", value, StringComparison.Ordinal);

    [Fact]
    public void A_quote_in_a_variable_cannot_break_or_add_to_the_json()
    {
        var evil = "\", \"username\":\"admin";
        var body = DiscordWebhookJson.FromJson("""{"embeds":[{"title":"%user% is here"}]}""", Vars("user", evil));

        Assert.Equal(evil + " is here", body.Payload["embeds"]![0]!["title"]!.GetValue<string>());
        Assert.False(body.Payload.ContainsKey("username"));
        Assert.Equal(evil + " is here", JsonNode.Parse(body.Payload.ToJsonString())!["embeds"]![0]!["title"]!.GetValue<string>());
    }

    [Fact]
    public void A_variable_cannot_ping_but_typed_mentions_can()
    {
        var viewer = DiscordWebhookJson.FromJson("""{"content":"hi %user%"}""", Vars("user", "@everyone <@111> <@&222>"));
        var none = viewer.Payload["allowed_mentions"]!.AsObject();
        Assert.Empty(none["parse"]!.AsArray());
        Assert.False(none.ContainsKey("users"));
        Assert.False(none.ContainsKey("roles"));

        var typed = DiscordWebhookJson.FromJson("""{"content":"@everyone <@111> <@&222> <@%id%>"}""", Vars("id", "333"));
        var allowed = typed.Payload["allowed_mentions"]!.AsObject();
        Assert.Equal("everyone", allowed["parse"]![0]!.GetValue<string>());
        Assert.Equal(["111", "333"], allowed["users"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("222", allowed["roles"]![0]!.GetValue<string>());
    }

    private async Task<JsonObject> SendSimpleAsync(string content, string viewerMessage)
    {
        Globals.Set("viewerMessage", viewerMessage, persist: false);
        var error = await RunAsync(OutgoingWebhookSubActions.DiscordBasicWebhook, new() { ["webhookUrl"] = Url, ["content"] = content });
        Assert.Null(error);
        var (_, _, sent) = Assert.Single(_http.Requests);
        return JsonNode.Parse(sent)!.AsObject();
    }

    [Fact]
    public async Task Simple_mode_a_viewer_message_cannot_ping()
    {
        var body = await SendSimpleAsync("New message: ~viewerMessage~", "@everyone @here <@111> <@&222>");

        var mentions = body["allowed_mentions"]!.AsObject();
        Assert.Empty(mentions["parse"]!.AsArray());
        Assert.False(mentions.ContainsKey("users"));
        Assert.False(mentions.ContainsKey("roles"));
        Assert.False(body["tts"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Simple_mode_typed_mentions_still_ping()
    {
        var body = await SendSimpleAsync("@everyone <@111> <@&222> live: ~viewerMessage~", "hello");

        var mentions = body["allowed_mentions"]!.AsObject();
        Assert.Equal("everyone", mentions["parse"]![0]!.GetValue<string>());
        Assert.Equal("111", mentions["users"]![0]!.GetValue<string>());
        Assert.Equal("222", mentions["roles"]![0]!.GetValue<string>());
        Assert.Equal("@everyone <@111> <@&222> live: hello", body["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Simple_mode_a_typed_here_does_not_let_a_viewer_ping_everyone()
    {
        var body = await SendSimpleAsync("@here said: ~viewerMessage~", "@everyone and @here");

        Assert.Equal("everyone", body["allowed_mentions"]!["parse"]![0]!.GetValue<string>());
        Assert.Equal("@here said: @​everyone and @​here", body["content"]!.GetValue<string>());
    }

    [Fact]
    public void A_variable_in_a_mention_must_be_an_id()
    {
        var body = DiscordWebhookJson.FromJson("""{"content":"<@%id%>"}""", Vars("id", "everyone"));
        Assert.False(body.Payload["allowed_mentions"]!.AsObject().ContainsKey("users"));
    }

    [Fact]
    public void The_authors_allowed_mentions_are_kept()
    {
        var body = DiscordWebhookJson.FromJson("""{"content":"hi","allowed_mentions":{"parse":["users"]}}""", Same);
        Assert.Equal("users", body.Payload["allowed_mentions"]!["parse"]![0]!.GetValue<string>());
    }

    [Fact]
    public void A_discohook_export_is_unwrapped()
    {
        var body = DiscordWebhookJson.FromJson("""
            {"version":"d2","messages":[{"thread_id":"777","data":{
              "content":"hello","webhook_id":"9","attachments":[],"author":{"name":"Bot","icon_url":"https://example.test/a.png"}}}]}
            """, Same);

        Assert.Equal("777", body.ThreadId);
        Assert.Equal("hello", body.Payload["content"]!.GetValue<string>());
        Assert.Equal("Bot", body.Payload["username"]!.GetValue<string>());
        Assert.Equal("https://example.test/a.png", body.Payload["avatar_url"]!.GetValue<string>());
        foreach (var gone in new[] { "webhook_id", "attachments", "author", "version", "messages" }) Assert.False(body.Payload.ContainsKey(gone));
    }

    [Fact]
    public void A_discohook_export_with_several_messages_is_refused()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            DiscordWebhookJson.FromJson("""{"messages":[{"data":{"content":"a"}},{"data":{"content":"b"}}]}""", Same));
        Assert.Contains("2 messages", ex.Message);
    }

    [Theory]
    [InlineData("#ff8800", 0xFF8800)]
    [InlineData("0x00ff00", 0x00FF00)]
    [InlineData("255", 255)]
    public void Embed_colours_become_numbers(string text, int expected)
    {
        var body = DiscordWebhookJson.FromJson("""{"embeds":[{"title":"t","color":"%c%"}]}""", Vars("c", text));
        Assert.Equal(expected, body.Payload["embeds"]![0]!["color"]!.GetValue<int>());
    }

    [Theory]
    [InlineData("""{"embeds":[{"title":"t","color":"red"}]}""", "colour")]
    [InlineData("""{"content":"x","components":[{"type":1,"components":[{"type":2,"style":1,"custom_id":"a","label":"b"}]}]}""", "link buttons")]
    [InlineData("""{"content":"x","components":[{"type":1,"components":[{"type":3,"custom_id":"a"}]}]}""", "select menus")]
    [InlineData("""{"username":"only"}""", "needs content")]
    [InlineData("""{"content": %user%}""", "inside quotes")]
    [InlineData("[1]", "must be an object")]
    public void Bad_bodies_get_a_clear_error(string json, string expected)
    {
        var ex = Assert.Throws<ArgumentException>(() => DiscordWebhookJson.FromJson(json, Same));
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void Discord_limits_are_checked_before_sending()
    {
        string Embeds(int count) => "{\"embeds\":[" + string.Join(",", Enumerable.Repeat("{\"title\":\"t\"}", count)) + "]}";
        Assert.Contains("at most 10", Assert.Throws<ArgumentException>(() => DiscordWebhookJson.FromJson(Embeds(11), Same)).Message);

        var title = $$"""{"embeds":[{"title":"{{new string('x', 257)}}"}]}""";
        Assert.Contains("title", Assert.Throws<ArgumentException>(() => DiscordWebhookJson.FromJson(title, Same)).Message);

        var half = new string('x', 3500);
        var total = $$"""{"embeds":[{"description":"{{half}}"},{"description":"{{half}}"}]}""";
        Assert.Contains("6000", Assert.Throws<ArgumentException>(() => DiscordWebhookJson.FromJson(total, Same)).Message);
    }

    [Fact]
    public async Task Json_post_with_a_link_button_asks_for_components_and_remembers_the_message()
    {
        Globals.Set("who", "Ann \"A\" Lee", persist: false);
        var error = await RunAsync(OutgoingWebhookSubActions.DiscordBasicWebhook, new()
        {
            ["webhookUrl"] = Url,
            ["bodyMode"] = "JSON",
            ["json"] = """{"embeds":[{"title":"~who~"}],"components":[{"type":1,"components":[{"type":2,"style":5,"label":"Go","url":"https://example.test"}]}]}""",
            ["rememberAs"] = "live post",
        });

        Assert.Null(error);
        var (method, url, sent) = Assert.Single(_http.Requests);
        Assert.Equal(HttpMethod.Post, method);
        Assert.Contains("wait=true", url.Query);
        Assert.Contains("with_components=true", url.Query);
        Assert.Equal("Ann \"A\" Lee", JsonNode.Parse(sent)!["embeds"]![0]!["title"]!.GetValue<string>());
        Assert.Equal("42/1337117683476336731", Globals.Get("live post"));
        Assert.Null(Globals.Get("live post thread"));
    }

    [Fact]
    public async Task A_new_forum_post_is_titled_and_its_thread_remembered_for_editing()
    {
        await RunAsync(OutgoingWebhookSubActions.DiscordBasicWebhook, new()
        {
            ["webhookUrl"] = Url, ["content"] = "hello", ["threadName"] = "Stream log", ["rememberAs"] = "log",
        });
        var (_, _, posted) = Assert.Single(_http.Requests);
        Assert.Equal("Stream log", JsonNode.Parse(posted)!["thread_name"]!.GetValue<string>());
        Assert.Equal("42", Globals.Get("log thread"));

        _http.Requests.Clear();
        var error = await RunAsync(OutgoingWebhookSubActions.DiscordEditWebhookMessage, new()
        {
            ["webhookUrl"] = Url, ["message"] = "log", ["content"] = "edited",
        });

        Assert.Null(error);
        var (method, url, body) = Assert.Single(_http.Requests);
        Assert.Equal(HttpMethod.Patch, method);
        Assert.Equal("/api/webhooks/123456/sEcReT-ToKeN/messages/1337117683476336731", url.AbsolutePath);
        Assert.Equal("?thread_id=42", url.Query);
        Assert.Equal("edited", JsonNode.Parse(body)!["content"]!.GetValue<string>());
    }

    [Fact]
    public async Task Editing_sends_only_fields_discord_can_edit_and_protects_mentions()
    {
        Globals.Set("msg", "hey @everyone", persist: false);
        await RunAsync(OutgoingWebhookSubActions.DiscordEditWebhookMessage, new()
        {
            ["webhookUrl"] = Url,
            ["message"] = "1337117683476336731",
            ["bodyMode"] = "JSON",
            ["json"] = """{"content":"~msg~","username":"nope","avatar_url":"https://example.test/a.png","tts":true,"thread_name":"x"}""",
        });

        var (_, _, sent) = Assert.Single(_http.Requests);
        var body = JsonNode.Parse(sent)!.AsObject();
        Assert.Equal(["allowed_mentions", "content"], body.Select(p => p.Key).Order(StringComparer.Ordinal));
        Assert.Equal("hey @everyone", body["content"]!.GetValue<string>());
        Assert.Empty(body["allowed_mentions"]!["parse"]!.AsArray());
    }

    [Fact]
    public void A_global_variable_can_hold_a_mention_id()
    {
        var body = DiscordWebhookJson.FromJson("""{"content":"<@~owner~>"}""", text => text.Replace("~owner~", "444", StringComparison.Ordinal));
        Assert.Equal("444", body.Payload["allowed_mentions"]!["users"]![0]!.GetValue<string>());
    }

    [Fact]
    public async Task Delete_uses_the_message_endpoint_and_typed_thread()
    {
        _http.ResponseBody = "";
        _http.Status = HttpStatusCode.NoContent;
        var error = await RunAsync(OutgoingWebhookSubActions.DiscordDeleteWebhookMessage, new()
        {
            ["webhookUrl"] = Url,
            ["message"] = "https://discord.com/channels/1/2/1337117683476336731",
            ["threadId"] = "555",
        });

        Assert.Null(error);
        var (method, url, _) = Assert.Single(_http.Requests);
        Assert.Equal(HttpMethod.Delete, method);
        Assert.EndsWith("/messages/1337117683476336731", url.AbsolutePath);
        Assert.Equal("?thread_id=555", url.Query);
    }

    [Fact]
    public async Task An_unknown_message_name_sends_nothing()
    {
        var error = await RunAsync(OutgoingWebhookSubActions.DiscordDeleteWebhookMessage, new() { ["webhookUrl"] = Url, ["message"] = "never sent" });

        Assert.Contains("does not know a message", error);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task A_thread_id_and_a_forum_title_together_are_refused()
    {
        var error = await RunAsync(OutgoingWebhookSubActions.DiscordBasicWebhook, new()
        {
            ["webhookUrl"] = Url, ["content"] = "x", ["threadId"] = "5", ["threadName"] = "y",
        });

        Assert.Contains("not both", error);
        Assert.Empty(_http.Requests);
    }

    [Fact]
    public async Task A_thread_in_the_webhook_url_still_works_and_counts_as_a_thread()
    {
        var client = new DiscordWebhookClient(new HttpClient(_http));
        var result = await client.PostAsync(Url + "?thread_id=888", DiscordWebhookClient.SimpleBody("x", null, null, false), null, null,
            CancellationToken.None);

        var (_, url, _) = Assert.Single(_http.Requests);
        Assert.Contains("thread_id=888", url.Query);
        Assert.Contains("wait=true", url.Query);
        Assert.True(result.InThread);
    }

    [Fact]
    public async Task A_rate_limit_is_waited_out_then_retried()
    {
        var http = new SequenceHandler(
            (HttpStatusCode.TooManyRequests, """{"retry_after":1.5}"""),
            (HttpStatusCode.OK, Sent));
        var waits = new List<TimeSpan>();
        var client = new DiscordWebhookClient(new HttpClient(http), (wait, _) => { waits.Add(wait); return Task.CompletedTask; });

        var id = await client.PostAsync(Url, "hi", ct: CancellationToken.None);

        Assert.Equal("1337117683476336731", id);
        Assert.Equal([TimeSpan.FromSeconds(1.5)], waits);
        Assert.Equal(2, http.Calls);
    }

    [Fact]
    public async Task A_long_rate_limit_fails_clearly_without_the_token()
    {
        var http = new SequenceHandler((HttpStatusCode.TooManyRequests, """{"retry_after":120}"""));
        var client = new DiscordWebhookClient(new HttpClient(http), (_, _) => throw new InvalidOperationException("should not wait"));

        var ex = await Assert.ThrowsAsync<OutgoingWebhookException>(() => client.PostAsync(Url, "hi", ct: CancellationToken.None));

        Assert.Contains("120 seconds", ex.Message);
        Assert.DoesNotContain("sEcReT", ex.Message);
        Assert.Equal(1, http.Calls);
    }

    [Fact]
    public async Task Discords_reason_is_in_the_error_without_the_token()
    {
        var http = new SequenceHandler((HttpStatusCode.NotFound, """{"message":"Unknown Message","code":10008}"""));
        var client = new DiscordWebhookClient(new HttpClient(http));

        var ex = await Assert.ThrowsAsync<OutgoingWebhookException>(() =>
            client.DeleteAsync(Url, "1337117683476336731", null, CancellationToken.None));

        Assert.Contains("Unknown Message", ex.Message);
        Assert.DoesNotContain("sEcReT", ex.Message);
    }

    [Theory]
    [InlineData("https://discord.com/api/webhooks/1/token/slack")]
    [InlineData("https://discord.com/api/webhooks/1/token/github")]
    public void Editing_needs_a_plain_webhook_url(string url)
    {
        Assert.Throws<ArgumentException>(() => DiscordWebhookClient.MessageEndpoint(new Uri(url), "123", null, false));
    }

    private sealed class SequenceHandler(params (HttpStatusCode Status, string Body)[] replies) : HttpMessageHandler
    {
        private readonly ConcurrentQueue<(HttpStatusCode Status, string Body)> _replies = new(replies);
        private int _calls;

        public int Calls => _calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref _calls);
            if (!_replies.TryDequeue(out var reply)) throw new InvalidOperationException("No reply left.");
            return Task.FromResult(new HttpResponseMessage(reply.Status) { Content = new StringContent(reply.Body, Encoding.UTF8, "application/json") });
        }
    }
}
