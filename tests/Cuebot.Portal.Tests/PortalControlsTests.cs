using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Portal.Crypto;
using Cuebot.Portal.Members;

namespace Cuebot.Portal.Tests;

/// <summary>Feeds ChatHub's activity list from "test.feed" events.</summary>
internal sealed class TestFeedAdapter : IChatEventAdapter
{
    public string Platform => "Twitch";
    public ChatMessage? ToChatMessage(BotEvent e) => null;

    public FeedItem? ToFeedItem(BotEvent e) => e.Type == "test.feed"
        ? new FeedItem("Twitch", Enum.Parse<FeedKind>((string)e.Args["kind"]!), (string)e.Args["user"]!, (string)e.Args["title"]!, DateTimeOffset.UtcNow)
        : null;

    public static void Publish(EventBus bus, FeedKind kind, string user, string title) =>
        bus.Publish(new BotEvent("test.feed", new Dictionary<string, object?> { ["kind"] = kind.ToString(), ["user"] = user, ["title"] = title }));
}

internal sealed class TestConnection(string name, ConnectionState state, string message) : IConnectionStatus
{
    public string Name => name;
    public ConnectionState State => state;
    public string StatusMessage => message;
    public event Action? StatusChanged { add { } remove { } }
}

public sealed class PortalControlsTests : IDisposable
{
    private sealed class ManualTime(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private readonly AppLog _log = new();
    private readonly EventBus _bus;
    private readonly ProfileService _profile;
    private readonly QueueService _queues;
    private readonly ChatHub _chat;
    private readonly PortalDirectory _dir = new(new PortalSettings(), () => { });
    private readonly ManualTime _time = new(new DateTimeOffset(2026, 11, 5, 20, 0, 0, TimeSpan.Zero));
    private readonly PortalControls _controls;
    private readonly string _sound = Path.Combine(Path.GetTempPath(), $"cuebot-portal-{Guid.NewGuid():N}.wav");

    public PortalControlsTests()
    {
        _bus = new EventBus(_log);
        _profile = new ProfileService(null, _log);
        _queues = new QueueService(_profile);
        _chat = new ChatHub(_bus, _log, [new TestFeedAdapter()], []);
        File.WriteAllBytes(_sound, [0]);
        _controls = new PortalControls(_profile, _chat, [new TestConnection("OBS", ConnectionState.Connected, "Connected")],
            obs: null, _queues, speech: null, audio: null, _log, _time);
    }

    public void Dispose()
    {
        File.Delete(_sound);
        _log.Dispose();
    }

    private (PortalMember Member, PortalRole Role) Member(Guid role)
    {
        using var k = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var m = _dir.RedeemInvite(_dir.CreateInvite(role).Secret, new ProviderIdentity(IdentityProvider.Twitch, Guid.NewGuid().ToString(), "mod", "Mod"),
            PortalKeys.RawPublicKey(k.ExportParameters(false)), "phone").Member;
        return (m, _dir.RoleOf(m)!);
    }

    private Task<JsonObject> Call((PortalMember Member, PortalRole Role) m, string op, JsonObject? body = null) =>
        _controls.HandleAsync(m.Member, m.Role, op, body ?? []);

    private ActionDefinition AddAction(string name, string queue = "", params SubActionDefinition[] steps)
    {
        var a = new ActionDefinition { Name = name, Queue = queue, SubActions = [.. steps] };
        _profile.Profile.Actions.Add(a);
        _profile.NotifyChanged();
        return a;
    }

    [Fact]
    public async Task The_dashboard_shows_only_what_the_role_may_see()
    {
        TestFeedAdapter.Publish(_bus, FeedKind.Follow, "Viewer1", "followed");
        AddAction("Hype", steps: new SubActionDefinition { Type = "chat.send" });

        var standard = await Call(Member(DefaultRoles.StandardModId), "dashboard");
        Assert.True((bool)standard["ok"]!);
        Assert.Equal("Viewer1", (string?)standard["feed"]![0]!["user"]);
        Assert.Null(standard["connections"]);
        Assert.Equal("Hype", (string?)standard["quickActions"]![0]!["name"]);

        var tech = await Call(Member(DefaultRoles.TechLeadId), "dashboard");
        Assert.Equal("OBS", (string?)tech["connections"]![0]!["name"]);
    }

    [Fact]
    public async Task Stream_controls_need_the_permission()
    {
        _profile.Profile.Queues.Add(new QueueDefinition { Name = "Alerts" });
        _profile.NotifyChanged();
        var standard = await Call(Member(DefaultRoles.StandardModId), "queues.list");
        Assert.False((bool)standard["ok"]!);

        var lead = Member(DefaultRoles.LeadModId);
        Assert.True((bool)(await Call(lead, "queues.control", new JsonObject { ["queue"] = "Alerts", ["command"] = "pause" }))["ok"]!);
        Assert.True(_queues.GetState("Alerts").Paused);
        var listed = await Call(lead, "queues.list");
        Assert.Contains(listed["queues"]!.AsArray(), q => (string?)q!["name"] == "Alerts" && (bool)q["paused"]!);
        Assert.False((bool)(await Call(lead, "queues.control", new JsonObject { ["queue"] = "Nope", ["command"] = "pause" }))["ok"]!);
    }

    [Fact]
    public async Task Sounds_are_the_streamers_own_play_sound_steps()
    {
        var step = new SubActionDefinition { Type = "audio.playSound", Parameters = { ["file"] = _sound } };
        AddAction("Airhorn", steps: [step, new SubActionDefinition { Type = "audio.playSound", Parameters = { ["file"] = "%sound%" } }]);
        var m = Member(DefaultRoles.StandardModId);

        var sounds = (await Call(m, "sounds.list"))["sounds"]!.AsArray();
        var sound = Assert.Single(sounds);
        Assert.Equal(step.Id.ToString(), (string?)sound!["id"]);
        Assert.Equal("Airhorn", (string?)sound["action"]);
        Assert.Null(sound["file"]);

        // Without an audio service nothing plays, and a path is never accepted in place of a step.
        Assert.False((bool)(await Call(m, "sounds.play", new JsonObject { ["id"] = _sound }))["ok"]!);
    }

    [Fact]
    public async Task Controls_are_limited_per_member()
    {
        _profile.Profile.Queues.Add(new QueueDefinition { Name = "Alerts" });
        _profile.NotifyChanged();
        var lead = Member(DefaultRoles.LeadModId);
        var pause = new JsonObject { ["queue"] = "Alerts", ["command"] = "pause" };
        for (var i = 0; i < PortalControls.ControlsPerMinute; i++) Assert.True((bool)(await Call(lead, "queues.control", pause.DeepClone().AsObject()))["ok"]!);
        var limited = await Call(lead, "queues.control", pause.DeepClone().AsObject());
        Assert.Contains("Wait a moment", (string?)limited["error"]);
        Assert.True((bool)(await Call(lead, "queues.list"))["ok"]!);

        _time.Now += TimeSpan.FromMinutes(1.1);
        Assert.True((bool)(await Call(lead, "queues.control", pause.DeepClone().AsObject()))["ok"]!);
    }

    [Fact]
    public async Task Missing_modules_answer_in_plain_words()
    {
        var lead = Member(DefaultRoles.LeadModId);
        Assert.Contains("OBS", (string?)(await Call(lead, "scenes.list"))["error"]);
        Assert.Contains("not available", (string?)(await Call(lead, "tts.state"))["error"]);
    }
}
