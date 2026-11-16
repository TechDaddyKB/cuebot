using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Portal.Crypto;
using Cuebot.Portal.Members;
using Cuebot.Portal.Relay;

namespace Cuebot.Portal.Tests;

public sealed class PortalPushTests : IDisposable
{
    /// <summary>A device link that records what Cuebot sent (plaintext) and can be held up to play a slow device.</summary>
    private sealed class FakeLink(PortalLinkContext context) : IPortalPushLink
    {
        public readonly List<JsonObject> Sent = [];
        public TaskCompletionSource? Hold;

        public PortalLinkContext? Context { get; } = context;

        public bool IsOpen { get; set; } = true;

        public async Task SendAsync(byte[] plaintext, CancellationToken ct)
        {
            if (Hold is { } hold) await hold.Task.WaitAsync(ct);
            lock (Sent) Sent.Add((JsonObject)JsonNode.Parse(plaintext)!);
        }

        public List<JsonObject> Take()
        {
            lock (Sent)
            {
                var all = Sent.ToList();
                Sent.Clear();
                return all;
            }
        }
    }

    private static readonly PortalPushLimits Manual = new() { FlushInterval = Timeout.InfiniteTimeSpan };

    private readonly PortalDirectory _dir = new(new PortalSettings { Enabled = true }, () => { });
    private readonly PortalPush _push;
    private readonly PortalRole _chatRole;
    private readonly PortalRole _otherRole;

    public PortalPushTests()
    {
        _push = new PortalPush(_dir, limits: Manual);
        _chatRole = _dir.SaveRole(new PortalRole { Name = "Chat mod", Permissions = [PortalPermission.ViewActivity] });
        _otherRole = _dir.SaveRole(new PortalRole { Name = "Sound tech", Permissions = [PortalPermission.UploadSounds] });
        _push.Register(new PortalTopic("chat") { Permission = PortalPermission.ViewActivity });
    }

    public void Dispose() => _push.Dispose();

    private (PortalMember Member, FakeLink Link) Device(PortalRole? role = null)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (member, device) = _dir.RedeemInvite(_dir.CreateInvite((role ?? _chatRole).Id).Secret,
            new ProviderIdentity(IdentityProvider.Twitch, Guid.NewGuid().ToString(), "mod", "Mod"), PortalKeys.RawPublicKey(key.ExportParameters(false)), "Phone");
        return (member, new FakeLink(new PortalLinkContext(7, new byte[32]) { MemberId = member.Id, DeviceId = device.Id }));
    }

    private JsonObject Ask(FakeLink link, object request, bool answered = true)
    {
        var bytes = _push.TryHandle(link, JsonSerializer.SerializeToUtf8Bytes(request), out var done);
        Assert.NotNull(bytes);
        if (answered) done?.Invoke();
        return (JsonObject)JsonNode.Parse(bytes)!;
    }

    private long Subscribe(FakeLink link, string topic = "chat", object? parameters = null)
    {
        var answer = Ask(link, new { op = "subscribe", req = 3, topic, @params = parameters ?? new { } });
        Assert.True((bool)answer["ok"]!, (string?)answer["error"]);
        Assert.Equal(3, (int)answer["req"]!);
        return (long)answer["sub"]!;
    }

    private static JsonObject Event(int n) => new() { ["n"] = n, ["text"] = "hello " + n };

    [Fact]
    public async Task Delivers_published_events_in_order_after_the_subscribe_answer()
    {
        var (_, link) = Device();
        // Not answered yet: events queue but nothing is sent before the device has its answer.
        var answer = _push.TryHandle(link, """{"op":"subscribe","req":1,"topic":"chat"}"""u8.ToArray(), out var answered);
        var sub = (long)JsonNode.Parse(answer!)!["sub"]!;
        Assert.Equal(1, _push.Publish("chat", Event(1)));
        await _push.FlushAsync();
        Assert.Empty(link.Take());

        answered!();
        _push.Publish("chat", Event(2));
        await _push.FlushAsync();
        var frame = Assert.Single(link.Take());
        Assert.Equal("chat", (string?)frame["push"]);
        Assert.Equal(sub, (long)frame["sub"]!);
        Assert.Equal([1, 2], frame["events"]!.AsArray().Select(e => (int)e!["n"]!));
        Assert.Null(frame["req"]);
    }

    [Fact]
    public void Other_messages_go_to_the_link_handler()
    {
        var (_, link) = Device();
        Assert.Null(_push.TryHandle(link, """{"op":"ping","req":1}"""u8.ToArray(), out _));
        Assert.Null(_push.TryHandle(link, """{"op":"actions.run","name":"subscribe"}"""u8.ToArray(), out _));
        Assert.Null(_push.TryHandle(link, "not json subscribe"u8.ToArray(), out _));
    }

    [Fact]
    public async Task Unsubscribe_stops_delivery()
    {
        var (_, link) = Device();
        var sub = Subscribe(link);
        Assert.Equal(1, _push.Subscribers("chat"));
        Assert.True((bool)Ask(link, new { op = "unsubscribe", req = 4, sub })["ok"]!);
        Assert.Equal(0, _push.Subscribers("chat"));
        Assert.Equal(0, _push.Publish("chat", Event(1)));
        await _push.FlushAsync();
        Assert.Empty(link.Take());
    }

    [Fact]
    public void Refuses_devices_that_are_not_allowed()
    {
        var (_, other) = Device(_otherRole);
        var refused = Ask(other, new { op = "subscribe", topic = "chat" });
        Assert.False((bool)refused["ok"]!);
        Assert.Equal("Your role does not include this.", (string?)refused["error"]);

        var (_, link) = Device();
        Assert.Contains("Unknown topic", (string?)Ask(link, new { op = "subscribe", topic = "secrets" })["error"]);

        var anonymous = new FakeLink(new PortalLinkContext(9, new byte[32]));
        var signIn = Ask(anonymous, new { op = "subscribe", topic = "chat" });
        Assert.False((bool)signIn["ok"]!);
        Assert.True((bool)signIn["signIn"]!);

        _push.Register(new PortalTopic("logs") { Authorize = (m, _) => m.Name == "Nobody" });
        Assert.False((bool)Ask(link, new { op = "subscribe", topic = "logs" })["ok"]!);

        _push.Register(new PortalTopic("filtered") { CheckParams = p => p["platform"] is null ? "Pick a platform." : null });
        Assert.Equal("Pick a platform.", (string?)Ask(link, new { op = "subscribe", topic = "filtered" })["error"]);
        Assert.Equal("The subscription's params must be an object.", (string?)Ask(link, new { op = "subscribe", topic = "chat", @params = 5 })["error"]);
        Assert.Contains("too long", (string?)Ask(link, new { op = "subscribe", topic = "chat", @params = new { x = new string('a', 3000) } })["error"]);
        Assert.Equal(0, _push.Subscribers("chat"));
    }

    [Fact]
    public void Limits_subscriptions_per_device()
    {
        var (_, link) = Device();
        for (var i = 0; i < _push.Limits.MaxSubscriptionsPerDevice; i++) Subscribe(link);
        Assert.Contains("Too many", (string?)Ask(link, new { op = "subscribe", topic = "chat" })["error"]);
    }

    [Fact]
    public async Task Batches_within_the_event_and_byte_caps()
    {
        var (_, link) = Device();
        Subscribe(link);
        for (var i = 0; i < 250; i++) _push.Publish("chat", Event(i));
        await _push.FlushAsync();
        var frames = link.Take();
        Assert.Equal([100, 100, 50], frames.Select(f => f["events"]!.AsArray().Count));
        Assert.Equal(Enumerable.Range(0, 250), frames.SelectMany(f => f["events"]!.AsArray()).Select(e => (int)e!["n"]!));

        // Big events: frames stay under the byte cap.
        var big = new string('x', 10_000);
        for (var i = 0; i < 7; i++) _push.Publish("chat", new JsonObject { ["text"] = big });
        await _push.FlushAsync();
        frames = link.Take();
        Assert.All(frames, f => Assert.True(Encoding.UTF8.GetByteCount(f.ToJsonString()) <= _push.Limits.MaxFrameBytes));
        Assert.Equal(7, frames.Sum(f => f["events"]!.AsArray().Count));

        Assert.Throws<ArgumentException>(() => _push.Publish("chat", new JsonObject { ["text"] = new string('x', 20_000) }));
    }

    [Fact]
    public async Task Sends_at_most_a_few_frames_per_flush_and_keeps_the_rest()
    {
        var (_, link) = Device();
        Subscribe(link);
        for (var i = 0; i < 450; i++) _push.Publish("chat", Event(i));
        await _push.FlushAsync();
        Assert.Equal(_push.Limits.MaxFramesPerFlush, link.Take().Count);
        await _push.FlushAsync();
        Assert.Equal(50, Assert.Single(link.Take())["events"]!.AsArray().Count);
    }

    [Fact]
    public async Task A_device_that_falls_behind_gets_a_resync_instead_of_a_growing_queue()
    {
        var (_, slow) = Device();
        var (_, fast) = Device();
        var slowSub = Subscribe(slow);
        Subscribe(fast);

        // The slow device's send hangs; the fast one keeps up.
        slow.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _push.Publish("chat", Event(0));
        var stuck = _push.FlushAsync();
        await Task.Delay(20);
        Assert.False(stuck.IsCompleted);
        Assert.Single(fast.Take());

        var total = _push.Limits.MaxQueuedEvents + 20;
        for (var i = 1; i <= total; i++)
        {
            _push.Publish("chat", Event(i));
            if (i % 50 == 0)
            {
                await _push.FlushAsync();   // the slow device is skipped while its last send hangs
                fast.Take();
            }
        }

        slow.Hold.SetResult();
        await stuck;
        await _push.FlushAsync();
        var frames = slow.Take();
        Assert.Equal(0, (int)frames[0]["events"]![0]!["n"]!);
        var resync = frames[1];
        Assert.True((bool)resync["resync"]!);
        Assert.Equal(slowSub, (long)resync["sub"]!);
        Assert.Equal(_push.Limits.MaxQueuedEvents + 1, (long)resync["missed"]!);
        // What came after the drop still arrives, after the resync.
        var after = frames.Skip(2).SelectMany(f => f["events"]!.AsArray()).Select(e => (int)e!["n"]!).ToList();
        Assert.Equal(Enumerable.Range(_push.Limits.MaxQueuedEvents + 2, total - _push.Limits.MaxQueuedEvents - 1), after);
    }

    [Fact]
    public async Task Ends_subscriptions_when_the_member_is_removed_paused_or_signed_out()
    {
        var (removed, a) = Device();
        var (paused, b) = Device();
        var (signedOut, c) = Device();
        var (_, d) = Device();
        foreach (var link in new[] { a, b, c, d }) Subscribe(link);
        Assert.Equal(4, _push.Subscribers("chat"));

        _dir.RemoveMember(removed.Id);
        _dir.SetSuspended(paused.Id, true);
        _dir.RemoveDevice(signedOut.Id, c.Context!.DeviceId!.Value);
        Assert.Equal(1, _push.Subscribers("chat"));

        _push.Publish("chat", Event(1));
        await _push.FlushAsync();
        foreach (var link in new[] { a, b, c })
        {
            var ended = Assert.Single(link.Take());
            Assert.True((bool)ended["ended"]!);
            Assert.Null(ended["events"]);
        }
        Assert.Single(d.Take()[0]["events"]!.AsArray());
    }

    [Fact]
    public async Task Ends_subscriptions_when_the_role_loses_the_permission_or_the_link_signs_out()
    {
        var (_, link) = Device();
        var (_, other) = Device();
        Subscribe(link);
        Subscribe(other);
        _chatRole.Permissions = [];
        _dir.SaveRole(_chatRole);
        Assert.Equal(0, _push.Subscribers("chat"));
        await _push.FlushAsync();
        Assert.Equal("Your role does not include this.", (string?)Assert.Single(link.Take())["error"]);

        _chatRole.Permissions = [PortalPermission.ViewActivity];
        _dir.SaveRole(_chatRole);
        other.Take();
        Subscribe(other);
        other.Context!.MemberId = null;   // "signout" on the link
        _push.Publish("chat", Event(1));
        await _push.FlushAsync();
        Assert.True((bool)Assert.Single(other.Take())["ended"]!);
    }

    [Fact]
    public async Task Forgets_a_closed_link_and_a_withdrawn_topic()
    {
        var (_, link) = Device();
        var (_, other) = Device();
        Subscribe(link);
        Subscribe(other);
        link.IsOpen = false;
        _push.RemoveLink(link);
        Assert.Equal(1, _push.Subscribers("chat"));
        Assert.Equal("The connection is closing.", (string?)Ask(link, new { op = "subscribe", topic = "chat" })["error"]);
        Assert.True(_push.Unregister("chat"));
        Assert.Equal(0, _push.Subscribers("chat"));
        Assert.Equal(0, _push.Publish("chat", Event(1)));
        await _push.FlushAsync();
        Assert.Empty(link.Take());
        Assert.Equal("Cuebot no longer offers this.", (string?)Assert.Single(other.Take())["error"]);
    }

    [Fact]
    public async Task Publishers_can_pick_subscriptions_by_their_params()
    {
        var (_, twitch) = Device();
        var (_, kick) = Device();
        Subscribe(twitch, parameters: new { platform = "twitch" });
        Subscribe(kick, parameters: new { platform = "kick" });
        Assert.Equal(1, _push.Publish("chat", Event(1), s => (string?)s.Params["platform"] == "kick"));
        await _push.FlushAsync();
        Assert.Empty(twitch.Take());
        Assert.Single(kick.Take());
    }
}
