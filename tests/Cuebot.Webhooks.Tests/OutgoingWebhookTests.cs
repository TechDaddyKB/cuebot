using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Webhooks.Outgoing;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Webhooks.Tests;

/// <summary>Records requests and answers them; no network.</summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    public ConcurrentQueue<(HttpMethod Method, Uri Url, string Body)> Requests { get; } = new();
    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
    public string ResponseBody { get; set; } = "";
    public bool Unreachable { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Requests.Enqueue((request.Method, request.RequestUri!, body));
        if (Unreachable) throw new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known. (" + request.RequestUri + ")");
        return new HttpResponseMessage(Status) { Content = new StringContent(ResponseBody, Encoding.UTF8, "application/json") };
    }
}

public sealed class OutgoingWebhookTests : IAsyncDisposable
{
    private const string DiscordUrl = "https://discord.com/api/webhooks/123456/sEcReT-ToKeN";
    private const string IftttKey = "ifttt-SeCrEt-key";

    private readonly string _dir = Directory.CreateTempSubdirectory("cuebot-outgoing-webhooks-").FullName;
    private readonly FakeHttpHandler _http = new();
    private readonly ServiceProvider _services;
    private readonly Capture _capture = new();

    public OutgoingWebhookTests()
    {
        var collection = new ServiceCollection().AddCuebotCore(new AppPaths(_dir)).AddWebhooks();
        collection.AddSingleton(new HttpClient(_http));   // later registrations win
        collection.AddSingleton<ISubActionHandler>(_capture);
        _services = collection.BuildServiceProvider();
    }

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* settings flush */ }
    }

    private async Task<(bool Ok, Dictionary<string, object?>? Args, string? Error)> RunAsync(string type, Dictionary<string, string> parameters)
    {
        var engine = _services.GetRequiredService<ActionEngine>();
        var ok = await engine.RunAsync(new ActionDefinition
        {
            Name = type,
            SubActions = [new SubActionDefinition { Type = type, Parameters = parameters }, new SubActionDefinition { Type = Capture.Id }],
        });
        return (ok && _capture.Args is not null, _capture.Args, engine.History.Entries.FirstOrDefault(e => e.Error is not null)?.Error);
    }

    [Fact]
    public void Sub_actions_are_registered_with_streamer_bot_categories()
    {
        var registry = _services.GetRequiredService<Registry>();
        Assert.Equal("Discord", registry.GetSubAction(OutgoingWebhookSubActions.DiscordBasicWebhook)!.Category);
        Assert.Equal("IFTTT", registry.GetSubAction(OutgoingWebhookSubActions.IftttNotificationBasic)!.Category);
        Assert.Equal("IFTTT", registry.GetSubAction(OutgoingWebhookSubActions.IftttNotificationAdvanced)!.Category);
    }

    [Fact]
    public async Task Discord_basic_webhook_posts_like_cph_and_returns_the_message_id()
    {
        _http.ResponseBody = """{"id":"1337117683476336731","channel_id":"42","content":"hi"}""";

        var (ok, args, error) = await RunAsync(OutgoingWebhookSubActions.DiscordBasicWebhook, new()
        {
            ["name"] = "Alerts", ["webhookUrl"] = DiscordUrl, ["content"] = "Live now <@&99>", ["username"] = "Cuebot",
            ["avatarUrl"] = "https://cdn/a.png", ["textToSpeech"] = "true",
        });

        Assert.True(ok, error);
        Assert.Equal("1337117683476336731", args!["messageId"]);
        Assert.Equal("1337117683476336731", args["discordMessageId"]);
        var request = Assert.Single(_http.Requests);
        Assert.Equal(HttpMethod.Post, request.Method);
        Assert.Equal(DiscordUrl + "?wait=true", request.Url.ToString());
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("Live now <@&99>", body.RootElement.GetProperty("content").GetString());
        Assert.True(body.RootElement.GetProperty("tts").GetBoolean());
        Assert.Equal("Cuebot", body.RootElement.GetProperty("username").GetString());
        Assert.Equal("https://cdn/a.png", body.RootElement.GetProperty("avatar_url").GetString());
    }

    [Fact]
    public void Discord_payload_omits_empty_username_and_avatar()
    {
        var payload = DiscordWebhookClient.BuildPayload("hi", " ", null, textToSpeech: false);
        Assert.Equal(["content", "tts"], payload.Keys.Order());
        Assert.Equal("https://discord.com/api/webhooks/1/x?thread_id=5&wait=true",
            DiscordWebhookClient.WithWait(new Uri("https://discord.com/api/webhooks/1/x?thread_id=5")).ToString());
    }

    [Theory]
    [InlineData("http://discord.com/api/webhooks/1/x")]
    [InlineData("https://example.com/api/webhooks/1/x")]
    [InlineData("https://discord.com/channels/1/2")]
    [InlineData("not a url")]
    public void Discord_rejects_urls_that_are_not_discord_webhooks(string url) =>
        Assert.Throws<ArgumentException>(() => DiscordWebhookClient.ParseUrl(url));

    [Fact]
    public async Task Discord_failures_never_reveal_the_webhook_url()
    {
        _http.Status = HttpStatusCode.NotFound;
        var (ok, _, error) = await RunAsync(OutgoingWebhookSubActions.DiscordBasicWebhook, new() { ["webhookUrl"] = DiscordUrl, ["content"] = "hi" });
        Assert.False(ok);
        Assert.Contains("404", error);
        Assert.DoesNotContain("sEcReT", error);

        _http.Unreachable = true;
        var client = new DiscordWebhookClient(new HttpClient(_http));
        var unreachable = await Assert.ThrowsAsync<OutgoingWebhookException>(() => client.PostAsync(DiscordUrl, "hi"));
        Assert.DoesNotContain("sEcReT", unreachable.ToString());
        Assert.DoesNotContain("sEcReT", string.Join("\n", _services.GetRequiredService<AppLog>().Recent().Select(e => e.ToString())));
    }

    [Fact]
    public async Task Ifttt_basic_sends_value1_to_the_webhooks_trigger()
    {
        var (ok, _, error) = await RunAsync(OutgoingWebhookSubActions.IftttNotificationBasic, new()
        {
            ["eventName"] = "stream live", ["text"] = "Going live!", ["key"] = IftttKey,
        });

        Assert.True(ok, error);
        var request = Assert.Single(_http.Requests);
        Assert.Equal("https://maker.ifttt.com/trigger/stream%20live/with/key/" + IftttKey, request.Url.AbsoluteUri);
        using var body = JsonDocument.Parse(request.Body);
        Assert.Equal("Going live!", body.RootElement.GetProperty("value1").GetString());
    }

    [Fact]
    public async Task Ifttt_uses_the_saved_key_when_the_sub_action_has_none()
    {
        _services.GetRequiredService<JsonFile<WebhooksSettings>>().Value.IftttKey = "saved-key";
        var (ok, _, error) = await RunAsync(OutgoingWebhookSubActions.IftttNotificationBasic, new() { ["eventName"] = "e", ["text"] = "t" });
        Assert.True(ok, error);
        Assert.EndsWith("/with/key/saved-key", Assert.Single(_http.Requests).Url.AbsoluteUri);

        _services.GetRequiredService<JsonFile<WebhooksSettings>>().Value.IftttKey = "";
        (ok, _, error) = await RunAsync(OutgoingWebhookSubActions.IftttNotificationBasic, new() { ["eventName"] = "e", ["text"] = "t" });
        Assert.False(ok);
        Assert.Contains("key is required", error);
    }

    [Fact]
    public async Task Ifttt_advanced_sends_named_arguments_and_arguments_json()
    {
        var (ok, _, error) = await RunAsync(OutgoingWebhookSubActions.IftttNotificationAdvanced, new()
        {
            ["eventName"] = "raid", ["key"] = IftttKey,
            ["arguments"] = "user=Alice\nviewers = 42\n\n=ignored\ngame=Celeste\nmood=happy\nfifth=5\nsixth=6",
        });

        Assert.True(ok, error);
        var request = Assert.Single(_http.Requests);
        Assert.Equal("https://maker.ifttt.com/trigger/raid/json/with/key/" + IftttKey, request.Url.AbsoluteUri);
        using var body = JsonDocument.Parse(request.Body);
        var root = body.RootElement;
        Assert.Equal("user", root.GetProperty("Argument1Name").GetString());
        Assert.Equal("Alice", root.GetProperty("Argument1Value").GetString());
        Assert.Equal("viewers", root.GetProperty("Argument2Name").GetString());
        Assert.Equal("42", root.GetProperty("Argument2Value").GetString());
        Assert.Equal("fifth", root.GetProperty("Argument5Name").GetString());
        Assert.False(root.TryGetProperty("Argument6Name", out _));   // only the first five, like Streamer.bot
        Assert.Equal("Alice", root.GetProperty("value1").GetString());
        Assert.Equal("Celeste", root.GetProperty("value3").GetString());
        using var all = JsonDocument.Parse(root.GetProperty("ArgumentsJson").GetString()!);
        Assert.Equal("6", all.RootElement.GetProperty("sixth").GetString());
        Assert.Equal(6, all.RootElement.EnumerateObject().Count());
    }

    [Fact]
    public async Task Ifttt_refusals_never_reveal_the_key()
    {
        _http.Status = HttpStatusCode.Unauthorized;
        var (ok, _, error) = await RunAsync(OutgoingWebhookSubActions.IftttNotificationBasic, new() { ["eventName"] = "e", ["text"] = "t", ["key"] = IftttKey });
        Assert.False(ok);
        Assert.Contains("refused the Webhooks key", error);
        Assert.DoesNotContain(IftttKey, error);
    }

    private sealed class Capture : ISubActionHandler
    {
        public const string Id = "test.capture";
        public Dictionary<string, object?>? Args { get; private set; }
        public string TypeId => Id;
        public string Category => "Test";
        public string DisplayName => "Capture";
        public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

        public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
        {
            Args = new Dictionary<string, object?>(context.Args, StringComparer.OrdinalIgnoreCase);
            return Task.FromResult(SubActionResult.Next);
        }
    }
}
