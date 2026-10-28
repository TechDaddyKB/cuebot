using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Scripting.Compat;
using Cuebot.Server.StreamerBot;

namespace Cuebot.Server.Tests;

/// <summary>
/// Every documented event payload and request response, checked field by field (names, nesting, JSON kinds) against
/// <c>Fixtures/streamerbot-documented-shapes.json</c>: the official client's types, the request reference and the
/// wiki's event pages, with made-up values. Runs without node; <see cref="StreamerBotClientNodeTests"/> drives the
/// same server with the real client when it is available.
/// </summary>
public sealed class StreamerBotDocumentedShapeTests
{
    private static readonly JsonObject Shapes = JsonNode.Parse(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "Fixtures", "streamerbot-documented-shapes.json")))!.AsObject();

    /// <summary>Compares <paramref name="actual"/> with a documented shape; returns the differences.</summary>
    internal static List<string> Compare(JsonNode? expected, JsonNode? actual, string path = "data")
    {
        var errors = new List<string>();
        Match(expected, actual, path, errors);
        return errors;
    }

    private static void Match(JsonNode? expected, JsonNode? actual, string path, List<string> errors)
    {
        if (expected is JsonValue any && any.GetValueKind() == JsonValueKind.String && any.GetValue<string>() == "*") return;
        var want = expected?.GetValueKind() ?? JsonValueKind.Null;
        var got = actual?.GetValueKind() ?? JsonValueKind.Null;
        static JsonValueKind Kind(JsonValueKind k) => k == JsonValueKind.False ? JsonValueKind.True : k;
        if (Kind(want) != Kind(got))
        {
            errors.Add($"{path}: expected {want}, got {got}");
            return;
        }
        switch (expected)
        {
            case JsonObject shape:
                foreach (var (key, value) in shape)
                {
                    if (!actual!.AsObject().TryGetPropertyValue(key, out var field)) errors.Add($"{path}.{key}: missing");
                    else Match(value, field, $"{path}.{key}", errors);
                }
                break;
            case JsonArray { Count: > 0 } items:
                var list = actual!.AsArray();
                if (list.Count == 0) errors.Add($"{path}: empty (the sample should produce elements to check)");
                for (var i = 0; i < list.Count; i++) Match(items[0], list[i], $"{path}[{i}]", errors);
                break;
        }
    }

    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs) args[key] = value;
        return args;
    }

    private static (string, object?)[] User(string display = "Ann", string login = "ann", string id = "42") =>
        [("user", display), ("userName", login), ("userId", id)];

    private static readonly string WatchedFile = Path.Combine(Path.GetTempPath(), $"cuebot-sb-shape-{Guid.NewGuid():N}.txt");

    private const string PollJson =
        """{"id":"p1","title":"Best?","choices":[{"id":"c1","title":"A","votes":3,"channel_points_votes":1}],"channel_points_voting":{"is_enabled":true,"amount_per_vote":10}}""";

    private const string PredictionJson =
        """{"id":"pr1","outcomes":[{"id":"o1","title":"Yes","top_predictors":[{"user_id":"7","user_login":"bob","user_name":"Bob","channel_points_used":100,"channel_points_won":200}]}]}""";

    /// <summary>Documented event → the Cuebot event (type and representative arguments) that produces it.</summary>
    public static readonly Dictionary<string, (string Type, Dictionary<string, object?> Args)> Samples = new()
    {
        ["Twitch.ChatMessage"] = ("twitch.chatMessage", Args([.. User(), ("message", "hi Kappa"), ("messageId", "m1"), ("color", "#FF0000"),
            ("badges", "moderator/1"), ("emotes", "25:3-7"), ("isModerator", true)])),
        ["Twitch.FirstWord"] = ("users.firstWords", Args([.. User(), ("platform", "Twitch"), ("message", "first!"), ("messageId", "m2")])),
        ["Twitch.Whisper"] = ("twitch.whisper", Args([.. User(), ("message", "psst"), ("messageId", "w1")])),
        ["Twitch.BotWhisper"] = ("twitch.botWhisper", Args([.. User(), ("message", "psst bot"), ("messageId", "w2")])),
        ["Twitch.Follow"] = ("twitch.follow", Args([.. User(), ("followedAt", "2026-01-01T00:00:00Z")])),
        ["Twitch.Cheer"] = ("twitch.cheer", Args([.. User(), ("bits", 150), ("message", "Cheer100 Cheer50 go"), ("messageId", "c1")])),
        ["Twitch.Sub"] = ("twitch.sub", Args([.. User(), ("tier", "1"), ("message", "")])),
        ["Twitch.ReSub"] = ("twitch.resub", Args([.. User(), ("tier", "1"), ("cumulativeMonths", 6), ("streakMonths", 3), ("message", "yay")])),
        ["Twitch.GiftSub"] = ("twitch.subGift", Args([.. User(), ("recipientUser", "Bob"), ("recipientUserName", "bob"), ("recipientUserId", "7"),
            ("tier", "1"), ("durationMonths", 1)])),
        ["Twitch.GiftBomb"] = ("twitch.giftBomb", Args([.. User(), ("total", 5), ("cumulativeTotal", 50), ("tier", "1")])),
        ["Twitch.Raid"] = ("twitch.raid", Args([.. User(), ("viewers", 12), ("broadcasterId", "1"), ("broadcasterUserName", "me")])),
        ["Twitch.StreamUpdate"] = ("twitch.streamUpdate", Args([.. User(), ("title", "New"), ("previousTitle", "Old"), ("gameId", "509658"),
            ("gameName", "Just Chatting")])),
        ["Twitch.StreamOffline"] = ("twitch.streamOffline", Args(User())),
        ["Twitch.AdRun"] = ("twitch.adRun", Args([.. User(), ("duration", 90), ("isAutomatic", true), ("startedAt", "2026-01-01T00:00:00Z")])),
        ["Twitch.HypeTrainStart"] = ("twitch.hypeTrainStart", HypeTrain()),
        ["Twitch.HypeTrainUpdate"] = ("twitch.hypeTrainUpdate", HypeTrain()),
        ["Twitch.HypeTrainLevelUp"] = ("twitch.hypeTrainLevelUp", HypeTrain()),
        ["Twitch.HypeTrainEnd"] = ("twitch.hypeTrainEnd", HypeTrain()),
        ["Twitch.RewardRedemption"] = ("twitch.rewardRedemption", Args([.. User(), ("rewardId", "r1"), ("rewardTitle", "Hydrate"), ("rewardCost", 100),
            ("rewardPrompt", "drink"), ("userInput", "now"), ("redemptionId", "x1")])),
        ["Twitch.RewardCreated"] = ("twitch.rewardCreated", Reward()),
        ["Twitch.RewardUpdated"] = ("twitch.rewardUpdated", Reward()),
        ["Twitch.RewardDeleted"] = ("twitch.rewardDeleted", Reward()),
        ["Twitch.PollCreated"] = ("twitch.pollCreated", Poll()),
        ["Twitch.PollUpdated"] = ("twitch.pollUpdated", Poll()),
        ["Twitch.PollCompleted"] = ("twitch.pollCompleted", Poll()),
        ["Twitch.PredictionCreated"] = ("twitch.predictionCreated", Prediction()),
        ["Twitch.PredictionUpdated"] = ("twitch.predictionUpdated", Prediction()),
        ["Twitch.PredictionLocked"] = ("twitch.predictionLocked", Prediction()),
        ["Twitch.PredictionCompleted"] = ("twitch.predictionCompleted", Prediction()),
        ["Twitch.PredictionCanceled"] = ("twitch.predictionCanceled", Prediction()),
        ["Twitch.ViewerCountUpdate"] = ("twitch.viewerCountUpdate", Args(("viewers", 33))),
        ["Twitch.CharityStarted"] = ("twitch.charityStarted", Charity()),
        ["Twitch.CharityProgress"] = ("twitch.charityProgress", Charity()),
        ["Twitch.CharityCompleted"] = ("twitch.charityCompleted", Charity()),
        ["Twitch.CharityDonation"] = ("twitch.charityDonation", Charity()),
        ["Twitch.Announcement"] = ("twitch.announcement", Args([.. User(), ("message", "hello Kappa"), ("messageId", "a1"), ("emotes", "25:6-10"),
            ("badges", "broadcaster/1"), ("announcementColor", "PURPLE")])),
        ["Twitch.PyramidSuccess"] = ("twitch.pyramid", Args([.. User(), ("width", 3), ("emote", "Kappa")])),
        ["Twitch.PyramidBroken"] = ("twitch.pyramidBroken", Args([.. User(), ("width", 2), ("emote", "Kappa"), ("brokenBy", "Bob"), ("brokenByUserId", "7")])),
        ["Twitch.ChatCleared"] = ("twitch.chatCleared", Args(User("Me", "me", "1"))),
        ["Twitch.ChatMessageDeleted"] = ("twitch.chatMessageDeleted", Args([.. User(), ("messageId", "m1"), ("message", "oops")])),
        ["Twitch.ShieldModeBegin"] = ("twitch.shieldModeBegin", Args([.. User("Mod", "mod", "9"), ("startedAt", "2026-01-01T00:00:00Z")])),
        ["Twitch.ShieldModeEnd"] = ("twitch.shieldModeEnd", Args([.. User("Mod", "mod", "9"), ("endedAt", "2026-01-01T00:00:00Z")])),
        ["Twitch.ShoutoutCreated"] = ("twitch.shoutoutCreated", Args([.. User(), ("moderator", "Mod"), ("moderatorUserName", "mod"),
            ("moderatorUserId", "9"), ("viewers", 5)])),
        ["Twitch.UserTimedOut"] = ("twitch.userTimedOut", Ban(60)),
        ["Twitch.UserBanned"] = ("twitch.userBanned", Ban(0)),
        ["Twitch.UserUntimedOut"] = ("twitch.userUntimedOut", Ban(0)),
        ["YouTube.Message"] = ("youtube.chatMessage", Args(("platform", "youtube"), ("user", "Ann"), ("userName", "Ann"), ("userId", "UC1"),
            ("message", "hello"), ("messageId", "e1"), ("publishedAt", "2026-01-01T00:00:00Z"))),
        ["YouTube.FirstWords"] = ("users.firstWords", Args(("platform", "YouTube"), ("user", "Ann"), ("userId", "UC1"), ("message", "hi"))),
        ["YouTube.BroadcastStarted"] = ("youtube.broadcastStarted", Args(("broadcastId", "v1"), ("userId", "UC1"), ("broadcastTitle", "Live"),
            ("broadcastDescription", "d"), ("broadcastPrivacy", "public"), ("broadcastActualStart", "2026-01-01T00:00:00Z"))),
        ["YouTube.BroadcastUpdated"] = ("youtube.broadcastUpdated", Args(("broadcastId", "v1"), ("broadcastTitle", "Live"), ("broadcastPrivacy", "public"))),
        ["YouTube.StatisticsUpdated"] = ("youtube.broadcastStatisticsUpdated", Args(("likeCount", 1), ("viewCount", 10))),
        ["Command.Triggered"] = ("command.triggered", Args([.. User(), ("platform", "twitch"), ("command", "!hi"), ("counter", 1), ("userCounter", 1)])),
        ["Command.Cooldown"] = ("command.cooldown", Args([.. User(), ("platform", "twitch"), ("command", "!hi"), ("remaining", 5), ("cooldownType", "Global")])),
        ["Misc.TimedAction"] = ("core.timer", Args(("timerId", "t1"), ("timerName", "Ads"), ("timerCron", ""), ("counter", 2))),
        ["Pulsoid.HeartRatePulse"] = ("pulsoid.heartRate", Args(("heartRate", 72))),
        ["StreamElements.Tip"] = ("streamelements.tip", Args(("tipId", "t1"), ("tipUsername", "ann"), ("tipAmount", 5.0), ("tipCurrency", "USD"),
            ("tipMessage", "gg"))),
        ["Streamlabs.Donation"] = ("streamlabs.donation", Args(("donationFrom", "Ann"), ("donationAmount", 5.0), ("donationFormattedAmount", "$5.00"),
            ("donationCurrency", "USD"), ("donationMessage", "gg"))),
        ["Streamlabs.Merchandise"] = ("streamlabs.merch", Args(("merchandiseFrom", "Ann"), ("merchandiseProduct", "Shirt"),
            ("merchandiseMessage", "nice"), ("merchandiseImageUrl", "https://example.com/shirt.png"))),
        ["DonorDrive.Donation"] = ("donordrive.donation", Args(("donorName", "Ann"), ("donorId", "d1"), ("donorAmount", 10.0), ("donationId", "x"),
            ("participantId", "5"), ("teamId", "6"), ("createdAt", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero)))),
        ["DonorDrive.ProfileUpdated"] = ("donordrive.profileUpdated", Args(("goal", 100.0), ("raised", 50.0), ("eventName", "Kids"), ("numDonations", 4))),
        ["SpeechToText.Dictation"] = ("speechToText.dictation", Args(("text", "hello"), ("confidence", 0.9))),
        ["SpeechToText.Command"] = ("speechToText.command", Args(("text", "scene one"), ("confidence", 0.8))),
        ["FileWatcher.Changed"] = ("core.fileChanged", Args(("fullPath", WatchedFile), ("fileName", Path.GetFileName(WatchedFile)))),
        ["FileWatcher.Created"] = ("core.fileCreated", Args(("fullPath", WatchedFile), ("fileName", Path.GetFileName(WatchedFile)))),
        ["FileWatcher.Deleted"] = ("core.fileDeleted", Args(("fullPath", WatchedFile), ("fileName", Path.GetFileName(WatchedFile)))),
        ["FileWatcher.Renamed"] = ("core.fileRenamed", Args(("fullPath", WatchedFile), ("fileName", "new.txt"), ("oldFullPath", "old.txt"),
            ("oldFileName", "old.txt"))),
        ["Obs.Connected"] = ("obs.connected", Args(("studioVersion", "31.0.0"), ("websocketVersion", "5.5.0"), ("host", "127.0.0.1"), ("obsConnection", "OBS"))),
        ["Obs.Disconnected"] = ("obs.disconnected", Args(("reason", "closed"))),
        ["Shopify.OrderCreated"] = ("shopify.orderCreated", Args(("shopify.event", "orders/create"), ("_json",
            """{"id":1,"created_at":"2026-01-01T00:00:00Z","email":"a@example.com","number":1,"total_price":"5.00","subtotal_price":"5.00","currency":"USD","financial_status":"paid","name":"#1","order_number":1001,"line_items":[{"title":"Mug","quantity":1,"price":"5.00"}]}"""))),
        ["Custom.Event"] = (CodeTriggerRegistry.EventType, Args(("eventName", "myEvent"), ("score", 3))),
    };

    private static Dictionary<string, object?> HypeTrain() => Args(("hypeTrainId", "h1"), ("level", 2), ("total", 500), ("progress", 100), ("goal", 1000),
        ("topBitsUser", "Ann"), ("topBitsUserId", "42"), ("topBitsTotal", 300), ("startedAt", "2026-01-01T00:00:00Z"));

    private static Dictionary<string, object?> Reward() => Args([.. User("Me", "me", "1"), ("rewardId", "r1"), ("rewardTitle", "Hydrate"),
        ("rewardCost", 100), ("rewardPrompt", "drink"), ("isEnabled", true), ("maxPerStream", 3), ("globalCooldown", 60)]);

    private static Dictionary<string, object?> Poll() => Args([.. User("Me", "me", "1"), ("pollId", "p1"), ("title", "Best?"), ("status", "completed"),
        ("choiceCount", 1), ("choice0Id", "c1"), ("choice0Title", "A"), ("choice0Votes", 3), ("choice0ChannelPointsVotes", 1), ("winningChoice", "A"),
        ("poll._json", PollJson)]);

    private static Dictionary<string, object?> Prediction() => Args([.. User("Me", "me", "1"), ("predictionId", "pr1"), ("title", "Win?"),
        ("status", "resolved"), ("outcomeCount", 1), ("outcome0Id", "o1"), ("outcome0Title", "Yes"), ("outcome0Color", "blue"), ("outcome0Users", 1),
        ("outcome0ChannelPoints", 100), ("winningOutcomeId", "o1"), ("prediction._json", PredictionJson)]);

    private static Dictionary<string, object?> Charity() => Args([.. User(), ("campaignId", "ch1"), ("charityName", "Kids"), ("currency", "USD"),
        ("amount", "5.00"), ("currentAmount", "50.00"), ("targetAmount", "100.00"), ("charity.amount.valueMinor", 500L),
        ("charity.startedAt", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero))]);

    private static Dictionary<string, object?> Ban(int duration) => Args([.. User(), ("moderator", "Mod"), ("moderatorUserName", "mod"),
        ("moderatorUserId", "9"), ("duration", duration), ("reason", "spam"), ("bannedAt", "2026-01-01T00:00:00Z")]);

    public static TheoryData<string> DocumentedEvents()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in Shapes["events"]!.AsObject()) data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(DocumentedEvents))]
    public async Task Event_payload_has_the_documented_shape(string documented)
    {
        Assert.True(Samples.TryGetValue(documented, out var sample), $"No sample event for {documented}");
        await File.WriteAllTextAsync(WatchedFile, "first line\nsecond line");
        await using var bot = new TestBot(options: StreamerBotFixtures.Options());
        var e = new BotEvent(sample.Type, sample.Args);
        var services = new StreamerBotPayloadServices(bot.Services.Pronouns, bot.Services.Emotes);
        JsonObject message;
        if (documented == "Custom.Event")
        {
            message = StreamerBotEnvelope.CustomEvent(e)!;
        }
        else
        {
            var (name, built) = StreamerBotEnvelope.FromBotEvent(e, services);
            Assert.Equal(documented, name.ToString());
            message = built;
        }
        Assert.True(DateTimeOffset.TryParse(message["timeStamp"]!.GetValue<string>(), out _));
        var errors = Compare(Shapes["events"]![documented], message["data"]);
        Assert.True(errors.Count == 0, $"{documented}:\n" + string.Join("\n", errors));
    }

    [Fact]
    public void Every_shaped_Twitch_event_of_the_client_has_a_fixture()
    {
        // The client's TwitchEvents types with a payload shape (not UnknownEventData/undefined) and Cuebot emits.
        string[] clientTyped =
        [
            "AdRun", "Announcement", "BotWhisper", "CharityCompleted", "CharityDonation", "CharityProgress", "CharityStarted",
            "ChatCleared", "ChatMessage", "ChatMessageDeleted", "Cheer", "FirstWord", "Follow", "GiftSub", "HypeTrainEnd",
            "HypeTrainLevelUp", "HypeTrainStart", "HypeTrainUpdate", "PollCompleted", "PollCreated", "PollUpdated",
            "PredictionCanceled", "PredictionCompleted", "PredictionCreated", "PredictionLocked", "PredictionUpdated",
            "PyramidBroken", "PyramidSuccess", "Raid", "ReSub", "RewardCreated", "RewardDeleted", "RewardRedemption",
            "RewardUpdated", "ShieldModeBegin", "ShieldModeEnd", "ShoutoutCreated", "StreamUpdate", "Sub", "UserBanned",
            "UserTimedOut", "ViewerCountUpdate", "Whisper",
        ];
        var events = Shapes["events"]!.AsObject();
        foreach (var type in clientTyped) Assert.True(events.ContainsKey("Twitch." + type), $"Twitch.{type} has no fixture");
    }

    [Fact]
    public async Task Request_responses_have_the_documented_shapes()
    {
        await using var bot = await PreparedBotAsync();
        var requests = new StreamerBotRequests(bot.Services);
        async Task Check(string fixture, string request, object? body = null)
        {
            var response = await requests.HandleAsync(request, JsonSerializer.SerializeToElement(body ?? new { }));
            var errors = Compare(Shapes["requests"]![fixture], response, request);
            Assert.True(errors.Count == 0, $"{request}:\n" + string.Join("\n", errors));
        }

        await Check("GetInfo", "GetInfo");
        await Check("GetActions", "GetActions");
        await Check("GetBroadcaster", "GetBroadcaster");
        await Check("GetMonitoredYouTubeBroadcasts", "GetMonitoredYouTubeBroadcasts");
        await Check("GetCredits", "GetCredits");
        await Check("GetCredits", "TestCredits");
        await Check("GetActiveViewers", "GetActiveViewers");
        await Check("GetCodeTriggers", "GetCodeTriggers");
        await Check("GetCommands", "GetCommands");
        await Check("TwitchGetEmotes", "TwitchGetEmotes");
        await Check("YouTubeGetEmotes", "YouTubeGetEmotes");
        await Check("GetGlobals", "GetGlobals", new { persisted = true });
        await Check("GetGlobal", "GetGlobal", new { variable = "score", persisted = true });
        await Check("TwitchGetUserGlobals", "TwitchGetUserGlobals", new { variable = "tuna", persisted = true });
        await Check("TwitchGetUserGlobal", "TwitchGetUserGlobal", new { userId = "42", persisted = true });
        await Check("TwitchGetUserGlobal+variable", "TwitchGetUserGlobal", new { userId = "42", variable = "tuna", persisted = true });
        await Check("GetUserPronouns", "GetUserPronouns", new { platform = "twitch", userLogin = "ann" });
    }

    /// <summary>A bot with an action, a command, a code trigger, variables, a present viewer and the platform fakes.</summary>
    internal static async Task<TestBot> PreparedBotAsync(ServerSettings? settings = null, TestBotOptions? options = null)
    {
        var bot = new TestBot(settings, options ?? StreamerBotFixtures.Options());
        bot.AddAction("Shape test");
        bot.Commands.Save(new CommandDefinition { Name = "Hello", Commands = ["!hello"], Group = "Chat" });
        bot.CodeTriggers.Register("Channel created", "channel_created", ["Custom", "Discord"]);
        bot.Globals.SetValue("score", 4L, persist: true);
        bot.UserVariables.Set("twitch", "42", "Ann", "tuna", 3L, persist: true);
        bot.Users.Handle(new BotEvent(Cuebot.Core.Chat.ChatHub.MessageEvent, new Dictionary<string, object?>
        {
            ["platform"] = "Twitch", ["userId"] = "42", ["user"] = "Ann", ["userName"] = "ann", ["message"] = "hi",
        }));
        // Native emotes the chat has shown are what TwitchGetEmotes lists as userEmotes.
        bot.Services.Emotes?.FindEmotes("Twitch", "Kappa", new Dictionary<string, object?> { ["emotes"] = "25:0-4" });
        bot.Services.Emotes?.FindEmotes("YouTube", ":wave:", new Dictionary<string, object?>
        {
            ["emoteList"] = new[] { new Cuebot.Core.Chat.Emotes.PlatformEmote(0, 6, ":wave:", "w1", "https://example.com/wave.png") },
        });
        await Task.CompletedTask;
        return bot;
    }
}
