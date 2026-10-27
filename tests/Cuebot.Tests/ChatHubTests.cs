using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Tests;

public class ChatHubTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly FakeSender _alpha = new("Alpha");
    private readonly FakeSender _beta = new("Beta");
    private readonly ChatHub _hub;

    public ChatHubTests()
    {
        _bus = new EventBus(_log);
        _hub = new ChatHub(_bus, _log, [new FakeAdapter("Alpha"), new FakeAdapter("Beta")], [_alpha, _beta]);
    }

    private static BotEvent Chat(string platform, string text) =>
        new($"{platform.ToLowerInvariant()}.chatMessage", new Dictionary<string, object?> { ["user"] = "ann", ["message"] = text });

    [Fact]
    public void Collects_messages_from_every_platform_and_republishes_them_neutrally()
    {
        var neutral = new List<BotEvent>();
        _bus.Subscribe(e => { if (e.Type == ChatHub.MessageEvent) neutral.Add(e); });

        _bus.Publish(Chat("Alpha", "hi"));
        _bus.Publish(Chat("Beta", "hello"));

        Assert.Equal(["Alpha", "Beta"], _hub.RecentMessages().Select(m => m.Platform));
        Assert.Equal(["Alpha", "Beta"], neutral.Select(e => e.Args["platform"]));
        Assert.Equal("hello", neutral[1].Args["message"]);
    }

    [Fact]
    public void Neutral_message_carries_platform_flags_but_not_arbitrary_args()
    {
        BotEvent? neutral = null;
        _bus.Subscribe(e => { if (e.Type == ChatHub.MessageEvent) neutral = e; });

        _bus.Publish(new BotEvent("alpha.chatMessage", new Dictionary<string, object?>
        {
            ["user"] = "ann", ["message"] = "hi", ["messageKind"] = "Whisper", ["isBot"] = true,
            ["emotes"] = "25:0-1", ["isFirstMessage"] = true, ["secretThing"] = "x",
            ["platform"] = "spoofed",
        }));

        Assert.Equal("Whisper", neutral!.Args["messageKind"]);
        Assert.Equal(true, neutral.Args["isBot"]);
        Assert.Equal("25:0-1", neutral.Args["emotes"]);
        Assert.Equal(true, neutral.Args["isFirstMessage"]);
        Assert.False(neutral.Args.ContainsKey("secretThing"));
        Assert.Equal("Alpha", neutral.Args["platform"]); // the hub's own fields win
    }

    [Fact]
    public void Activity_goes_to_the_feed()
    {
        FeedItem? added = null;
        _hub.FeedItemAdded += item => added = item;

        _bus.Publish(new BotEvent("beta.follow", new Dictionary<string, object?> { ["user"] = "bob" }));

        Assert.Equal("bob followed", added!.Title);
        Assert.Single(_hub.RecentFeed());
    }

    [Fact]
    public async Task Send_to_all_reaches_every_connected_platform_and_skips_disconnected_ones()
    {
        _beta.CanSend = false;
        var sent = await _hub.SendAsync(ChatHub.AllPlatforms, "yo");

        Assert.Equal(["Alpha"], sent);
        Assert.Equal(["yo"], _alpha.Sent);
        Assert.Empty(_beta.Sent);
    }

    [Fact]
    public async Task Send_reports_why_nothing_could_be_sent()
    {
        _alpha.Fail = true;
        _beta.CanSend = false;
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => _hub.SendAsync(ChatHub.AllPlatforms, "yo"));
        Assert.Contains("Alpha: boom", ex.Message);
    }

    [Fact]
    public void Any_platform_trigger_filters_by_platform_and_text()
    {
        var trigger = new ChatTriggers(_hub).GetTriggers().Single(t => t.TypeId == "chat.message");
        var e = new BotEvent(ChatHub.MessageEvent, new Dictionary<string, object?> { ["platform"] = "Beta", ["message"] = "Hello World" });
        bool Matches(string platform, string contains) => trigger.Matches(e, new TriggerDefinition(),
            new ParameterValues(new Dictionary<string, string> { ["platform"] = platform, ["contains"] = contains }), new Dictionary<string, object?>());

        Assert.True(Matches(ChatHub.AllPlatforms, ""));
        Assert.True(Matches("beta", "world"));
        Assert.False(Matches("Alpha", ""));
        Assert.False(Matches(ChatHub.AllPlatforms, "bye"));
    }

    public void Dispose()
    {
        _hub.Dispose();
        _log.Dispose();
    }

    private sealed class FakeAdapter(string platform) : IChatEventAdapter
    {
        public string Platform => platform;

        public ChatMessage? ToChatMessage(BotEvent e) => e.Type == $"{platform.ToLowerInvariant()}.chatMessage"
            ? new ChatMessage(platform, Guid.NewGuid().ToString(), (string)e.Args["user"]!, "1", (string)e.Args["message"]!, e.Time)
            : null;

        public FeedItem? ToFeedItem(BotEvent e) => e.Type == $"{platform.ToLowerInvariant()}.follow"
            ? new FeedItem(platform, FeedKind.Follow, (string)e.Args["user"]!, $"{e.Args["user"]} followed", e.Time)
            : null;
    }

    private sealed class FakeSender(string platform) : IChatSender
    {
        public List<string> Sent { get; } = [];
        public bool Fail { get; set; }
        public string Platform => platform;
        public bool CanSend { get; set; } = true;

        public Task SendAsync(string text, CancellationToken ct = default)
        {
            if (Fail) throw new InvalidOperationException("boom");
            Sent.Add(text);
            return Task.CompletedTask;
        }
    }
}
