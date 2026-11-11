using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Donations.StreamElements;
using Cuebot.Donations.Tests.Support;
using Cuebot.Donations.Transport;

namespace Cuebot.Donations.Tests;

/// <summary>
/// The Astro topics beyond activities (tip moderation, stream status, session data, overlays, chatbot, contests,
/// giveaways, song requests, chat), the subscribe room, connection error handling and the reconnect back-fill.
/// Payloads are the examples of docs.streamelements.com/websockets/topics/*.
/// </summary>
public sealed class StreamElementsFeatureTests : IAsyncLifetime
{
    private const string Jwt = "eyJhbGciOiJIUzI1NiJ9.SE-SECRET-jwt-payload.signature456";
    private const string ChannelId = "5ad23dcc18fff500d78c5348";

    private readonly string _dataDir = Path.Combine(Path.GetTempPath(), "cuebot-se-feature-" + Guid.NewGuid().ToString("N"));
    private readonly AppLog _log = new();
    private readonly ConcurrentQueue<BotEvent> _published = new();
    private readonly FakeServer _server = new();
    private readonly ConcurrentDictionary<string, string> _responses = new(StringComparer.Ordinal);
    private JsonFile<DonationsSettings> _settings = null!;
    private FakeHttpHandler _http = null!;
    private GlobalVariables _globals = null!;
    private StreamElementsService _service = null!;

    private static readonly DonationsTimings Timings = TestTimings.Fast with { TipStateWait = TimeSpan.FromMilliseconds(300) };

    public Task InitializeAsync()
    {
        Directory.CreateDirectory(_dataDir);
        _settings = new JsonFile<DonationsSettings>(Path.Combine(_dataDir, "donations.json"), _log);
        _settings.Value.StreamElements.JwtToken = Jwt;
        var bus = new EventBus(_log);
        bus.Subscribe(_published.Enqueue);
        _globals = new GlobalVariables(null, _log);
        _responses["GET /kappa/v2/channels/me"] = $$"""{"_id":"{{ChannelId}}","username":"techdaddy","displayName":"TechDaddy","provider":"twitch"}""";
        _http = new FakeHttpHandler(r => _responses.TryGetValue($"{r.Method.Method} {r.Uri.AbsolutePath}", out var json)
            ? FakeHttpHandler.Json(json)
            : new HttpResponseMessage(HttpStatusCode.NotFound));
        _service = new StreamElementsService(_settings, bus, _log, _server.Create, new Uri("wss://astro.test"), new Uri("https://realtime.test"),
            Timings, new StreamElementsApi(new HttpClient(_http), new Uri("https://api.test/kappa/v2/")), _globals);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _service.DisposeAsync();
        _settings.Dispose();
        Directory.Delete(_dataDir, recursive: true);
    }

    private BotEvent[] Events(string type) => _published.Where(e => e.Type == type).ToArray();

    private const string Welcome = """{"id":"01J5KXYZ","ts":"2026-03-19T12:00:00Z","type":"welcome","data":{"message":"hi","client_id":"01J5KXYZ"}}""";

    private static string Message(string topic, string data) =>
        $$"""{"id":"01HPPM2TQ6CCMFQGHEN8MM1NN0","ts":"2024-02-15T14:52:42Z","type":"message","topic":"{{topic}}","room":"{{ChannelId}}","data":{{data.ReplaceLineEndings("")}}}""";

    /// <summary>Connects, answers every subscribe (room echoed as the canonical one) and returns the socket and the subscribes.</summary>
    private async Task<(FakeSocket Socket, List<JsonElement> Subscribes)> ConnectAsync(Func<string, string?>? error = null)
    {
        await _service.ConnectAsync();
        var socket = await _server.NextAsync();
        socket.Push(Welcome);
        var subscribes = new List<JsonElement>();
        var count = StreamElementsTopics.For(_service.Settings.ReceiveChat).Count;
        for (var i = 0; i < count; i++)
        {
            var sub = JsonDocument.Parse(await socket.NextSentAsync()).RootElement.Clone();
            subscribes.Add(sub);
            var topic = sub.GetProperty("data").GetProperty("topic").GetString()!;
            var nonce = sub.GetProperty("nonce").GetString();
            socket.Push(error?.Invoke(topic) is { } code
                ? $$$"""{"id":"e","type":"response","nonce":"{{{nonce}}}","error":"{{{code}}}","data":{"message":"no"}}"""
                : $$$"""{"id":"r","type":"response","nonce":"{{{nonce}}}","data":{"message":"successfully subscribed to topic","topic":"{{{topic}}}","room":"canonical-{{{ChannelId}}}"}}""");
        }
        await Wait.UntilAsync(() => _service.SubscribedTopics.Count + (error is null ? 0 : 1) >= 1 && _service.State == ConnectionState.Connected, "connected");
        return (socket, subscribes);
    }

    // ---- C1: room ----

    [Fact]
    public async Task Subscribes_name_the_channel_room_and_keep_the_canonical_room()
    {
        var (_, subscribes) = await ConnectAsync();
        Assert.All(subscribes, s => Assert.Equal(ChannelId, s.GetProperty("data").GetProperty("room").GetString()));
        Assert.Equal(StreamElementsTopics.For(false), subscribes.Select(s => s.GetProperty("data").GetProperty("topic").GetString()));
        await Wait.UntilAsync(() => _service.SubscribedTopics.Count == subscribes.Count, "all topics");
        Assert.Equal("canonical-" + ChannelId, _service.Room);
        Assert.Contains("(Twitch)", _service.StatusMessage);
    }

    [Fact]
    public void Without_a_known_channel_the_room_is_left_out()
    {
        var plain = JsonDocument.Parse(AstroClient.SubscribeMessage("channel.activities", "t", "n")).RootElement.GetProperty("data");
        Assert.False(plain.TryGetProperty("room", out _));
        var withRoom = JsonDocument.Parse(AstroClient.SubscribeMessage("channel.activities", "t", "n", "abc", "apikey")).RootElement.GetProperty("data");
        Assert.Equal(("abc", "apikey"), (withRoom.GetProperty("room").GetString(), withRoom.GetProperty("token_type").GetString()));
    }

    [Fact]
    public async Task A_refused_optional_topic_does_not_turn_the_connection_red()
    {
        await ConnectAsync(topic => topic == StreamElementsTopics.TipsModeration ? "err_unauthorized" : null);
        await Wait.UntilAsync(() => _service.SubscribedTopics.Count == StreamElementsTopics.For(false).Count - 1, "other topics");
        await Task.Delay(50);
        Assert.Equal(ConnectionState.Connected, _service.State);
    }

    // ---- C3 / F2: tip moderation ----

    private const string TipActivity = """
        {"type":"tip","provider":"twitch","channel":"5ad23dcc18fff500d78c5348","data":{"tipId":"67b5f39d07ecd4c594e60f73","username":"styler",
        "displayName":"Styler","amount":4.2,"currency":"USD","message":"Great stream","avatar":""},
        "_id":"65ce2539b1585c5f909a91e1","createdAt":"2025-02-19T15:07:09.302Z","activityId":"65ce2539b1585c5f909a91e1"}
        """;

    private static string TipDoc(string approved, string approvedBy = "") => $$"""
        {"donation":{"user":{"username":"Styler","geo":"ZZ","email":"styler@streamelements.com","channel":"5ad23dcc18fff500d78c5348"},
        "message":"Great stream","amount":4.2,"currency":"USD","paymentMethod":"scheme"},"_id":"67b5f39d07ecd4c594e60f73",
        "channel":"5ad23dcc18fff500d78c5348","provider":"paypal","approved":"{{approved}}","status":"success"{{(approvedBy.Length > 0 ? $",\"approvedBy\":\"{approvedBy}\"" : "")}},
        "createdAt":"2025-02-19T15:07:09.302Z","updatedAt":"2025-02-19T15:07:17.099Z","transactionId":"2YH79902JR1691017"}
        """;

    [Fact]
    public async Task A_tip_waits_for_its_allowed_document_before_running()
    {
        var (socket, _) = await ConnectAsync();
        socket.Push(Message("channel.activities", TipActivity));
        await Task.Delay(100);
        Assert.Empty(Events(StreamElementsEventTypes.Tip));
        socket.Push(Message("channel.tips", TipDoc("allowed")));
        await Wait.UntilAsync(() => Events(StreamElementsEventTypes.Tip).Length == 1, "tip");
        Assert.Equal("allowed", Events(StreamElementsEventTypes.Tip)[0].Args["tipApproved"]);
    }

    [Fact]
    public async Task A_pending_tip_runs_tip_pending_then_tip_once_a_moderator_allows_it()
    {
        var (socket, _) = await ConnectAsync();
        socket.Push(Message("channel.tips.moderation", TipDoc("pending")));
        socket.Push(Message("channel.activities", TipActivity));
        await Wait.UntilAsync(() => Events(StreamElementsEventTypes.TipPending).Length == 1, "pending");
        await Task.Delay(Timings.TipStateTimeout + TimeSpan.FromMilliseconds(100));   // a known pending tip has no timeout
        Assert.Empty(Events(StreamElementsEventTypes.Tip));

        socket.Push(Message("channel.tips.moderation", TipDoc("allowed", "styler")));
        await Wait.UntilAsync(() => Events(StreamElementsEventTypes.Tip).Length == 1, "tip");
        var tip = Events(StreamElementsEventTypes.Tip)[0].Args;
        Assert.Equal(("allowed", "styler", 4.2m), (tip["tipApproved"], tip["approvedBy"], tip["tipAmount"]));
        Assert.Single(Events(StreamElementsEventTypes.TipPending));
    }

    [Fact]
    public async Task A_rejected_tip_never_runs_the_tip_trigger()
    {
        var (socket, _) = await ConnectAsync();
        socket.Push(Message("channel.activities", TipActivity));
        socket.Push(Message("channel.tips.moderation", TipDoc("rejected", "mod1")));
        await Wait.UntilAsync(() => Events(StreamElementsEventTypes.TipRejected).Length == 1, "rejected");
        socket.Push(Message("channel.activities", TipActivity));   // re-sent activity
        await Task.Delay(Timings.TipStateTimeout + TimeSpan.FromMilliseconds(100));
        Assert.Empty(Events(StreamElementsEventTypes.Tip));
        var rejected = Events(StreamElementsEventTypes.TipRejected)[0].Args;
        Assert.Equal(("Styler", "rejected", "mod1", "$4.20"), (rejected["user"], rejected["tipApproved"], rejected["approvedBy"], rejected["formattedAmount"]));
    }

    [Fact]
    public async Task A_tip_without_any_document_is_published_after_the_wait()
    {
        var (socket, _) = await ConnectAsync();
        socket.Push(Message("channel.activities", TipActivity));
        await Wait.UntilAsync(() => Events(StreamElementsEventTypes.Tip).Length == 1, "tip after the wait");
        Assert.Equal("unknown", Events(StreamElementsEventTypes.Tip)[0].Args["tipApproved"]);
    }

    [Fact]
    public async Task Without_waiting_tips_run_at_once_unless_already_rejected()
    {
        _service.Settings.WaitForTipApproval = false;
        var (socket, _) = await ConnectAsync();
        socket.Push(Message("channel.activities", TipActivity));
        await Wait.UntilAsync(() => Events(StreamElementsEventTypes.Tip).Length == 1, "tip");

        var other = TipActivity.Replace("67b5f39d", "77b5f39d", StringComparison.Ordinal).Replace("65ce2539", "75ce2539", StringComparison.Ordinal);
        socket.Push(Message("channel.tips", TipDoc("rejected").Replace("67b5f39d", "77b5f39d", StringComparison.Ordinal)));
        socket.Push(Message("channel.activities", other));
        await Wait.UntilAsync(() => Events(StreamElementsEventTypes.TipRejected).Length == 1, "rejected");
        await Task.Delay(100);
        Assert.Single(Events(StreamElementsEventTypes.Tip));
    }

    [Fact]
    public async Task Tip_documents_never_leak_the_donor_email_or_location()
    {
        var (socket, _) = await ConnectAsync();
        socket.Push(Message("channel.tips.moderation", TipDoc("pending")));
        socket.Push(Message("channel.activities", TipActivity.Replace("\"avatar\":\"\"", "\"avatar\":\"\",\"email\":\"x@y.z\"", StringComparison.Ordinal)));
        socket.Push(Message("channel.tips.moderation", TipDoc("allowed")));
        await Wait.UntilAsync(() => Events(StreamElementsEventTypes.Tip).Length == 1, "tip");
        foreach (var e in _published)
        {
            Assert.DoesNotContain(e.Args, a => a.Key.Contains("email", StringComparison.OrdinalIgnoreCase) || a.Key.Contains("geo", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(e.Args.Values, v => v is string s && (s.Contains("@streamelements.com", StringComparison.Ordinal) || s == "ZZ" || s == "x@y.z"));
        }
    }

    [Fact]
    public async Task Test_tips_are_never_held()
    {
        Assert.True(_service.SendTestEvent("tip"));
        Assert.Single(Events(StreamElementsEventTypes.Tip));
        await Task.CompletedTask;
    }

    // ---- F1, F5: stream status and session data ----

    [Fact]
    public async Task Stream_status_runs_online_and_offline_once_and_sets_seIsLive()
    {
        var (socket, _) = await ConnectAsync();
        socket.Push(Message("channel.stream.status", """{"isLive":true}"""));
        socket.Push(Message("channel.stream.status", """{"isLive":true}"""));
        socket.Push(Message("channel.stream.status", """{"isLive":false}"""));
        await Wait.UntilAsync(() => Events(StreamElementsEventTypes.StreamOffline).Length == 1, "offline");
        Assert.Single(Events(StreamElementsEventTypes.StreamOnline));
        Assert.Equal(false, _globals.GetValue("seIsLive"));
        Assert.False(_service.IsLive);
    }

    [Fact]
    public async Task Session_updates_and_resets_fill_se_globals()
    {
        var (socket, _) = await ConnectAsync();
        socket.Push(Message("channel.session.update",
            """{"key":"follower-latest","name":"follower-latest","data":{"name":"Styler","activityId":"651016c07b356e73c9a3d928"},"provider":"twitch","channel":"c"}"""));
        await Wait.UntilAsync(() => Events(StreamElementsEventTypes.SessionUpdated).Length == 1, "update");
        Assert.Equal("Styler", _globals.Get("se.followerLatest.name"));
        Assert.Null(_globals.Get("se.followerLatest.activityId"));
        var update = Events(StreamElementsEventTypes.SessionUpdated)[0].Args;
        Assert.Equal(("follower-latest", "Styler", "se.followerLatest"), (update["key"], update["name"], update["variable"]));

        socket.Push(Message("channel.session.reset", """{"follower-latest":{"name":"Styler"},"follower-session":{"count":0},"tip-session":{"amount":0}}"""));
        await Wait.UntilAsync(() => Events(StreamElementsEventTypes.SessionReset).Length == 1, "reset");
        Assert.Equal("0", _globals.Get("se.tipSession.amount"));
        Assert.Equal("0", _globals.Get("se.followerSession.count"));
    }

    [Fact]
    public void Session_variable_names_have_no_dashes()
    {
        Assert.Equal("se.tipSession.amount", StreamElementsService.SessionVariable("tip-session", "amount"));
        Assert.Equal("se.cheerSessionTopDonation.name", StreamElementsService.SessionVariable("cheer-session-top-donation", "name"));
    }

    // ---- the other topics ----

    [Fact]
    public async Task Topic_messages_become_their_triggers()
    {
        var (socket, _) = await ConnectAsync();
        (string Topic, string Data)[] messages =
        [
            ("channel.overlay.action", """{"action":"pause"}"""),
            ("channel.overlay.update", """{"settings":{"width":1920,"height":1080,"name":"1080p"},"_id":"6920838231b91e5bdbdd473f","name":"xddd","type":"regular","widgets":[{"id":"w1"}]}"""),
            ("channel.overlay.broadcast", """{"target":"global","event":"emotesplosion-kappagen","data":{"key":"value"}}"""),
            ("channel.chatbot.counters", """{"counter":"deaths","value":42}"""),
            ("channel.contests", """{"event":"update","payload":{"optionId":"507f1f77bcf86cd799439011","amount":100,"userId":"12345678"}}"""),
            ("channel.contests", """{"event":"state","payload":{"state":"running","contestId":"507f1f77bcf86cd799439011"}}"""),
            ("channel.contests", """{"event":"winner","payload":{"winnerId":"507f1f77bcf86cd799439011"}}"""),
            ("channel.giveaways", """{"channelId":"c","giveawayId":"g1","event":"enter","state":"running","data":{"totalUsers":150,"totalTickets":3200,"entry":{"providerId":"twitch:12345","username":"cooluser","subscriber":true,"tickets":10,"new":true}}}"""),
            ("channel.giveaways", """{"channelId":"c","giveawayId":"g1","event":"winner","state":"running","data":{"totalUsers":200,"totalTickets":5000,"winner":{"providerId":"twitch:67890","username":"luckyuser","subscriber":false,"tickets":5}}}"""),
            ("channel.giveaways", """{"channelId":"c","giveawayId":"g1","event":"close","state":"closed","data":null}"""),
            ("channel.songrequest", """{"event":"queue.add","payload":{"song":{"_id":"s1","videoId":"dQw4w9WgXcQ","title":"Example Song Title","duration":212,"user":{"providerId":"1","username":"viewer123"},"url":"https://www.youtube.com/watch?v=dQw4w9WgXcQ","source":"bot","message":"Great song!"},"position":3,"queue":"queue"}}"""),
            ("channel.songrequest", """{"event":"volume","payload":{"volume":75}}"""),
            ("channel.chatbot.audiencequeue", """{"event":"member.enqueued","payload":{"id":"12345678","username":"viewer123","displayName":"Viewer123","watchTime":340,"audienceGroups":["subscriber"],"position":4}}"""),
            ("channel.chatbot.modules.emotecombo", """{"msg_id":"m","user_id":"94055225","user_name":"Styler","emote":"mhm","count":3}"""),
            ("channel.chatbot.modules.pyramid", """{"msg_id":"m","user_id":"94055225","user_name":"Styler","word":"mhm","width":3}"""),
            ("channel.chatbot.status", """{"mod":true}"""),
        ];
        string? updatedOverlay = null;
        _service.OverlayUpdated += (_, name) => updatedOverlay = name;
        foreach (var (topic, data) in messages) socket.Push(Message(topic, data));
        await Wait.UntilAsync(() => Events(StreamElementsEventTypes.BotStatus).Length == 1, "all handled");

        Assert.Equal("pause", Events(StreamElementsEventTypes.OverlayAction)[0].Args["action"]);
        Assert.Equal(("xddd", 1), (Events(StreamElementsEventTypes.OverlayUpdated)[0].Args["overlayName"], Events(StreamElementsEventTypes.OverlayUpdated)[0].Args["widgetCount"]));
        Assert.Equal("xddd", updatedOverlay);
        var broadcast = Events(StreamElementsEventTypes.OverlayBroadcast)[0].Args;
        Assert.Equal(("emotesplosion-kappagen", "global", "value"), (broadcast["event"], broadcast["target"], broadcast["data.key"]));
        Assert.Equal(("deaths", 42L), (Events(StreamElementsEventTypes.CounterChanged)[0].Args["counter"], Events(StreamElementsEventTypes.CounterChanged)[0].Args["value"]));
        Assert.Equal(100.0, Events(StreamElementsEventTypes.ContestBet)[0].Args["amount"]);
        Assert.Equal("running", Events(StreamElementsEventTypes.ContestState)[0].Args["state"]);
        Assert.Equal("507f1f77bcf86cd799439011", Events(StreamElementsEventTypes.ContestWinner)[0].Args["winnerId"]);
        var entry = Events(StreamElementsEventTypes.GiveawayEntered)[0].Args;
        Assert.Equal(("cooluser", 10L, 150, true), (entry["user"], entry["tickets"], entry["totalUsers"], entry["isNew"]));
        Assert.Equal("luckyuser", Events(StreamElementsEventTypes.GiveawayWinner)[0].Args["user"]);
        Assert.Equal(("close", "closed"), (Events(StreamElementsEventTypes.GiveawayState)[0].Args["event"], Events(StreamElementsEventTypes.GiveawayState)[0].Args["state"]));
        var songs = Events(StreamElementsEventTypes.SongRequest);
        Assert.Equal(("queue.add", "Example Song Title", "viewer123", 3), (songs[0].Args["event"], songs[0].Args["songTitle"], songs[0].Args["user"], songs[0].Args["position"]));
        Assert.Equal(75, songs[1].Args["volume"]);
        var member = Events(StreamElementsEventTypes.ViewerQueue)[0].Args;
        Assert.Equal(("member.enqueued", "Viewer123", 4, "subscriber"), (member["event"], member["user"], member["position"], member["groups"]));
        Assert.Equal(("mhm", 3), (Events(StreamElementsEventTypes.EmoteCombo)[0].Args["emote"], Events(StreamElementsEventTypes.EmoteCombo)[0].Args["count"]));
        Assert.Equal(3, Events(StreamElementsEventTypes.Pyramid)[0].Args["width"]);
        Assert.Equal(true, Events(StreamElementsEventTypes.BotStatus)[0].Args["isMod"]);
        Assert.Empty(_service.RecentEvents());   // topic events do not fill the donations list
    }

    [Fact]
    public async Task Chat_is_received_only_when_switched_on_and_deleted_messages_are_dropped()
    {
        _service.Settings.ReceiveChat = true;
        var (socket, subscribes) = await ConnectAsync();
        Assert.Contains(subscribes, s => s.GetProperty("data").GetProperty("topic").GetString() == "channel.chat");
        const string channel = "\"channel\":{\"id\":\"c\",\"provider_id\":\"1\",\"login\":\"techdaddy\"},\"sent_at\":\"2026-01-01T00:00:00Z\"";
        socket.Push(Message("channel.chat", "{\"v\":1,\"event\":\"message_deleted\",\"provider\":\"x\"," + channel + ",\"message_id\":\"m2\"}"));
        socket.Push(Message("channel.chat", "{\"v\":1,\"event\":\"message\",\"provider\":\"x\"," + channel
            + ",\"id\":\"m2\",\"author\":{\"id\":\"9\",\"login\":\"late\",\"name\":\"Late\"},\"text\":\"deleted first\",\"kind\":\"text\"}"));
        socket.Push(Message("channel.chat", "{\"v\":1,\"event\":\"message\",\"provider\":\"x\"," + channel
            + ",\"id\":\"m1\",\"author\":{\"id\":\"7\",\"login\":\"anna\",\"name\":\"Anna\",\"roles\":[\"moderator\"]},\"text\":\"hi Cheer100\",\"kind\":\"text\","
            + "\"fragments\":[{\"type\":\"text\",\"text\":\"hi \"},{\"type\":\"cheer\",\"text\":\"Cheer100\",\"cheer\":{\"prefix\":\"Cheer\",\"bits\":100,\"tier\":100}}]}"));
        socket.Push(Message("channel.chat", "{\"v\":1,\"event\":\"user_cleared\",\"provider\":\"kick\"," + channel
            + ",\"user\":{\"id\":\"5\",\"login\":\"spam\",\"name\":\"Spam\"},\"duration_seconds\":600}"));
        await Wait.UntilAsync(() => Events(StreamElementsEventTypes.ChatUserCleared).Length == 1, "cleared");
        var message = Assert.Single(Events(StreamElementsEventTypes.ChatMessage)).Args;
        Assert.Equal(("Anna", "hi Cheer100", "X", true, 100), (message["user"], message["message"], message["platform"], message["isModerator"], message["bits"]));
        Assert.Single(Events(StreamElementsEventTypes.ChatMessageDeleted));
        Assert.Equal((600, true), (Events(StreamElementsEventTypes.ChatUserCleared)[0].Args["duration"], Events(StreamElementsEventTypes.ChatUserCleared)[0].Args["isTimeout"]));
    }

    // ---- C6, C7: connection errors ----

    [Fact]
    public async Task A_draining_gateway_502_is_retried_quickly_without_growing_the_backoff()
    {
        var failures = 3;
        _server.Fail = _ => Interlocked.Decrement(ref failures) >= 0 ? new WebSocketUpgradeException(502, null) : null;
        await _service.ConnectAsync();
        var socket = await _server.NextAsync();
        Assert.Equal(4, _server.Attempts.Count);
        socket.Push(Welcome);
        Assert.Contains("\"subscribe\"", await socket.NextSentAsync());
        Assert.Contains(_log.Recent(), e => e.Message.Contains("HTTP 502", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_429_waits_for_retry_after()
    {
        var first = true;
        _server.Fail = _ =>
        {
            if (!first) return null;
            first = false;
            return new WebSocketUpgradeException(429, TimeSpan.FromSeconds(2));
        };
        await _service.ConnectAsync();
        await _server.NextAsync();
        var attempts = _server.Attempts;
        Assert.Equal(2, attempts.Count);
        Assert.True(attempts[1].At - attempts[0].At >= TimeSpan.FromSeconds(1.9), $"waited {(attempts[1].At - attempts[0].At).TotalSeconds}s");
    }

    [Fact]
    public void Retry_after_is_read_as_seconds_or_a_date()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), ClientWebSocketTransport.RetryAfter(new Dictionary<string, IEnumerable<string>> { ["retry-after"] = ["2"] }));
        Assert.Null(ClientWebSocketTransport.RetryAfter(new Dictionary<string, IEnumerable<string>> { ["Other"] = ["2"] }));
        Assert.Null(ClientWebSocketTransport.RetryAfter(null));
        var date = DateTimeOffset.UtcNow.AddSeconds(30).ToString("r", System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(ClientWebSocketTransport.RetryAfter(new Dictionary<string, IEnumerable<string>> { ["Retry-After"] = [date] })!.Value.TotalSeconds, 25, 31);
    }

    [Fact]
    public async Task Rate_limited_subscriptions_back_off_longer_each_time()
    {
        var timings = TestTimings.Fast with { RateLimitDelay = TimeSpan.FromMilliseconds(150), MaxReconnectDelay = TimeSpan.FromSeconds(5) };
        var client = new AstroClient(_server.Create, new Uri("wss://astro.test"), timings, _log);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var run = client.RunAsync("token-abcdef", ["channel.activities"], cts.Token);
        var socket = await _server.NextAsync();
        socket.Push(Welcome);
        await socket.NextSentAsync();
        var times = new List<DateTime>();
        for (var i = 0; i < 2; i++)
        {
            var sent = DateTime.UtcNow;
            socket.Push("""{"id":"e","type":"response","error":"rate_limit_exceeded","data":{"message":"rate limit exceeded"}}""");
            await socket.NextSentAsync();
            times.Add(sent);
            var waited = DateTime.UtcNow - sent;
            Assert.True(waited >= TimeSpan.FromMilliseconds(140 * (1 << i)), $"retry {i} waited {waited.TotalMilliseconds} ms");
        }
        await cts.CancelAsync();
        await run;
    }

    // ---- C2: legacy fallback ----

    [Fact]
    public async Task A_legacy_socket_that_keeps_failing_gives_way_to_astro()
    {
        _service.Settings.Transport = StreamElementsTransport.LegacyRealtime;
        _server.Reject = url => url.Host == "realtime.test";
        await _service.ConnectAsync();
        var socket = await _server.NextAsync();
        Assert.Equal("astro.test", socket.Url!.Host);
        Assert.Equal(StreamElementsService.LegacyFailuresBeforeFallback, _server.Attempts.Count(a => a.Url.Host == "realtime.test"));
        Assert.Contains(_log.Recent(), e => e.Message.Contains("deprecated", StringComparison.Ordinal));
    }

    // ---- T3: back-fill ----

    [Fact]
    public async Task A_fresh_reconnect_publishes_activities_missed_meanwhile()
    {
        _service.Settings.WaitForTipApproval = false;
        var (socket, _) = await ConnectAsync();
        socket.Push(Message("channel.activities", TipActivity));
        await Wait.UntilAsync(() => Events(StreamElementsEventTypes.Tip).Length == 1, "first tip");
        _responses[$"GET /kappa/v2/activities/{ChannelId}"] = "[" + TipActivity.ReplaceLineEndings("") + ","
            + TipActivity.Replace("67b5f39d", "87b5f39d", StringComparison.Ordinal).Replace("65ce2539", "85ce2539", StringComparison.Ordinal)
                .Replace("2025-02-19T15:07:09", "2025-02-19T15:09:00", StringComparison.Ordinal).ReplaceLineEndings("") + "]";

        socket.Close();
        var again = await _server.NextAsync();
        again.Push(Welcome);
        await Wait.UntilAsync(() => Events(StreamElementsEventTypes.Tip).Length == 2, "missed tip");
        var request = _http.Requests.Last(r => r.Uri.AbsolutePath.Contains("/activities/", StringComparison.Ordinal));
        Assert.Contains("after=2025-02-19T15%3A07%3A09", request.Uri.Query);
        Assert.Contains("types=tip", request.Uri.Query);
        Assert.Contains("types=merch", request.Uri.Query);
        await Task.Delay(100);
        Assert.Equal(2, Events(StreamElementsEventTypes.Tip).Length);   // the one already seen is not published again
    }

    // ---- F19: Overlay Token ----

    [Fact]
    public async Task The_overlay_token_subscribes_as_apikey_without_rest_calls()
    {
        _service.Settings.JwtToken = "";
        _service.Settings.OverlayToken = "overlay-token-0123456789";
        await _service.ConnectAsync();
        var socket = await _server.NextAsync();
        socket.Push(Welcome);
        var data = JsonDocument.Parse(await socket.NextSentAsync()).RootElement.GetProperty("data");
        Assert.Equal(("overlay-token-0123456789", "apikey"), (data.GetProperty("token").GetString(), data.GetProperty("token_type").GetString()));
        Assert.False(data.TryGetProperty("room", out _));
        Assert.Empty(_http.Requests);
        await Assert.ThrowsAsync<StreamElementsApiException>(() => _service.GetUserPointsAsync("anna"));
    }

    // ---- C5, T1: activity args ----

    [Fact]
    public void Activities_carry_every_data_field_session_count_and_redemption_input()
    {
        var hype = StreamElementsEventMapper.Map(JsonDocument.Parse("""
            {"type":"hypetrainEnd","provider":"twitch","channel":"c","data":{"username":"u","amount":5,"level":3,"email":"hidden@x.y","nested":{"a":1}},
            "_id":"h1","activityId":"h1","sessionEventsCount":7,"updatedAt":"2025-01-01T00:00:00Z"}
            """).RootElement)!;
        Assert.Equal(StreamElementsEventTypes.Activity, hype.Type);
        Assert.Equal(3L, hype.Args["data.level"]);
        Assert.Equal(1L, hype.Args["data.nested.a"]);
        Assert.Equal(7, hype.Args["sessionEventsCount"]);
        Assert.Equal("2025-01-01T00:00:00Z", hype.Args["updatedAt"]);
        Assert.False(hype.Args.ContainsKey("data.email"));

        var redemption = StreamElementsEventMapper.Map(JsonDocument.Parse("""
            {"type":"redemption","provider":"twitch","channel":"c","data":{"username":"v","itemName":"Song","amount":100,"redemptionId":"r9","input":["Never Gonna"]},"_id":"r1"}
            """).RootElement)!;
        Assert.Equal(("r9", "Never Gonna"), (redemption.Args["redemptionId"], redemption.Args["userInput"]));
    }

    [Fact]
    public void Every_listed_step_type_is_created()
    {
        var steps = StreamElementsMoreSubActions.Create(_service);
        Assert.Equal(StreamElementsMoreSubActions.TypeIds, steps.Select(s => s.TypeId));
        Assert.All(steps, s => Assert.StartsWith("streamelements.", s.TypeId, StringComparison.Ordinal));
        Assert.All(steps, s => Assert.NotEqual(StepRisk.Never, StepRiskCatalog.ForStep(s.TypeId)));
    }
}
