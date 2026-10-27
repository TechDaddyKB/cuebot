using System.Security.Cryptography;
using Cuebot.Kick.Events;
using Cuebot.Kick.Tests.Support;
using Cuebot.Kick.Webhooks;

namespace Cuebot.Kick.Tests;

/// <summary>Webhook signature verification (RSA PKCS#1 v1.5 + SHA-256 over id.timestamp.body) and payload parsing.</summary>
public sealed class WebhookTests : IDisposable
{
    private readonly RSA _kickKey = RSA.Create(2048);
    private readonly RSA _otherKey = RSA.Create(2048);

    public void Dispose()
    {
        _kickKey.Dispose();
        _otherKey.Dispose();
    }

    private string PublicPem(RSA key) => key.ExportSubjectPublicKeyInfoPem();

    private static string Sign(RSA key, string id, string timestamp, string body) =>
        Convert.ToBase64String(key.SignData(KickWebhookVerifier.SignedPayload(id, timestamp, body), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));

    private KickWebhookDelivery Delivery(string type, string body, string id = "01JH0000MSG", RSA? signer = null)
    {
        const string timestamp = "2025-01-14T16:08:06Z";
        return new KickWebhookDelivery(id, timestamp, Sign(signer ?? _kickKey, id, timestamp, body), type, "1", body);
    }

    [Fact]
    public void Valid_signature_verifies()
    {
        const string body = """{"hello":"world"}""";
        Assert.True(KickWebhookVerifier.Verify("id1", "ts", body, Sign(_kickKey, "id1", "ts", body), PublicPem(_kickKey)));
    }

    [Theory]
    [InlineData("tampered-body")]
    [InlineData("other-id")]
    [InlineData("other-timestamp")]
    [InlineData("other-key")]
    [InlineData("bad-base64")]
    [InlineData("bad-pem")]
    public void Anything_off_fails_verification(string what)
    {
        const string body = """{"hello":"world"}""";
        var signature = Sign(what == "other-key" ? _otherKey : _kickKey, "id1", "ts", body);
        var ok = what switch
        {
            "tampered-body" => KickWebhookVerifier.Verify("id1", "ts", body + " ", signature, PublicPem(_kickKey)),
            "other-id" => KickWebhookVerifier.Verify("id2", "ts", body, signature, PublicPem(_kickKey)),
            "other-timestamp" => KickWebhookVerifier.Verify("id1", "ts2", body, signature, PublicPem(_kickKey)),
            "bad-base64" => KickWebhookVerifier.Verify("id1", "ts", body, "%%%", PublicPem(_kickKey)),
            "bad-pem" => KickWebhookVerifier.Verify("id1", "ts", body, signature, "not a key"),
            _ => KickWebhookVerifier.Verify("id1", "ts", body, signature, PublicPem(_kickKey)),
        };
        Assert.False(ok);
    }

    [Fact]
    public void Delivery_is_read_from_headers_case_insensitively()
    {
        var d = KickWebhookDelivery.FromHeaders(new Dictionary<string, string>
        {
            ["kick-event-message-id"] = "m",
            ["Kick-Event-Message-Timestamp"] = "t",
            ["KICK-EVENT-SIGNATURE"] = "s",
            ["Kick-Event-Type"] = "chat.message.sent",
            ["Kick-Event-Version"] = "1",
        }, "{}");
        Assert.Equal(new KickWebhookDelivery("m", "t", "s", "chat.message.sent", "1", "{}"), d);
    }

    [Fact]
    public async Task Source_accepts_signed_events_drops_duplicates_and_rejects_forgeries()
    {
        var fetches = 0;
        var source = new KickWebhookEventSource(_ => { fetches++; return Task.FromResult(PublicPem(_kickKey)); });
        var events = new List<KickEvent>();
        source.EventReceived += events.Add;
        var body = TestData.Read("webhook-chat.json");

        Assert.Equal(KickWebhookOutcome.Accepted, await source.HandleAsync(Delivery("chat.message.sent", body), default));
        Assert.Equal(KickWebhookOutcome.Duplicate, await source.HandleAsync(Delivery("chat.message.sent", body), default));
        Assert.Equal(KickWebhookOutcome.BadSignature, await source.HandleAsync(Delivery("chat.message.sent", body, "other", _otherKey), default));
        Assert.Equal(KickWebhookOutcome.Ignored, await source.HandleAsync(Delivery("channel.unknown.thing", "{}", "id-3"), default));
        Assert.Equal(KickWebhookOutcome.Malformed, await source.HandleAsync(Delivery("chat.message.sent", "{oops", "id-4"), default));

        Assert.Single(events);
        Assert.Equal("webhook", events[0].Source);
        Assert.Equal(2, fetches); // once at start, once more when the forged signature failed (possible key rotation)
    }

    [Fact]
    public async Task Source_refetches_a_rotated_key()
    {
        var current = PublicPem(_otherKey);
        var source = new KickWebhookEventSource(_ => Task.FromResult(current));
        await source.HandleAsync(Delivery("channel.followed", "{}", "warmup", _otherKey), default);
        current = PublicPem(_kickKey); // Kick rotated its key
        var outcome = await source.HandleAsync(Delivery("channel.followed",
            """{"broadcaster":{"user_id":100,"username":"CuebotDev"},"follower":{"user_id":5,"username":"Fan"}}""", "new"), default);
        Assert.Equal(KickWebhookOutcome.Accepted, outcome);
    }

    [Fact]
    public void Chat_message_payload()
    {
        var e = Assert.Single(KickWebhookParser.Parse("chat.message.sent", TestData.Read("webhook-chat.json")));
        var a = e.Args;
        Assert.Equal(KickEventTypes.ChatMessage, e.Type);
        Assert.Equal("chat:a8e1c2b4-5d6f-4e7a-9b0c-1d2e3f4a5b6c", e.DedupeKey);
        Assert.Equal("ModAlice", a["user"]);
        Assert.Equal("333", a["userId"]);
        Assert.Equal("modalice", a["userSlug"]);
        Assert.Equal("hello kkHuh there", a["message"]);
        Assert.Equal(true, a["isModerator"]);
        Assert.Equal(7, a["subscribedMonths"]);
        Assert.Equal("#FF5733", a["color"]);
        Assert.Equal(true, a["isReply"]);
        Assert.Equal("OtherViewer", a["replyToUser"]);
        Assert.Equal("anyone here? KEKW", a["replyToMessage"]);
    }

    [Fact]
    public void Follow_and_subscription_payloads()
    {
        const string user = """{"is_anonymous":false,"user_id":5,"username":"Fan","is_verified":false,"profile_picture":"","channel_slug":"fan","identity":null}""";
        var follow = Assert.Single(KickWebhookParser.Parse("channel.followed", $$"""{"broadcaster":{{user}},"follower":{{user}}}"""));
        Assert.Equal(KickEventTypes.Follow, follow.Type);
        Assert.Equal("follow:fan", follow.DedupeKey);

        var fresh = Assert.Single(KickWebhookParser.Parse("channel.subscription.new",
            $$"""{"broadcaster":{{user}},"subscriber":{{user}},"duration":1,"created_at":"2025-01-13T01:00:00Z","expires_at":"2025-02-13T01:00:00Z"}"""));
        Assert.Equal(KickEventTypes.Subscription, fresh.Type);
        var renewal = Assert.Single(KickWebhookParser.Parse("channel.subscription.renewal",
            $$"""{"broadcaster":{{user}},"subscriber":{{user}},"duration":3,"created_at":"2025-01-13T01:00:00Z","expires_at":"2025-02-13T01:00:00Z"}"""));
        Assert.Equal(KickEventTypes.Resubscription, renewal.Type);
        Assert.Equal(3, renewal.Args["months"]);
    }

    [Fact]
    public void Gift_payload_produces_mass_gift_and_recipients()
    {
        var events = KickWebhookParser.Parse("channel.subscription.gifts", TestData.Read("webhook-gifts.json"));
        Assert.Equal(4, events.Count);
        Assert.Equal(KickEventTypes.MassGiftSubscription, events[0].Type);
        Assert.Equal(3, events[0].Args["gifts"]);
        Assert.Equal("Generous", events[0].Args["user"]);
        Assert.Equal("503", events[3].Args["recipientId"]);
    }

    [Fact]
    public void Ban_payload_with_expiry_is_a_timeout()
    {
        var e = Assert.Single(KickWebhookParser.Parse("moderation.banned", TestData.Read("webhook-timeout.json")));
        Assert.Equal(KickEventTypes.UserTimedOut, e.Type);
        Assert.Equal(600L, e.Args["duration"]);
        Assert.Equal("Spammer", e.Args["user"]);
        Assert.Equal("ModAlice", e.Args["moderator"]);
        Assert.Equal("spam", e.Args["reason"]);
    }

    [Fact]
    public void Ban_payload_without_expiry_is_a_ban()
    {
        var body = TestData.Read("webhook-timeout.json").Replace("\"2025-01-14T16:18:06Z\"", "null");
        Assert.Equal(KickEventTypes.UserBanned, Assert.Single(KickWebhookParser.Parse("moderation.banned", body)).Type);
    }

    [Fact]
    public void Kicks_and_reward_payloads()
    {
        var kicks = Assert.Single(KickWebhookParser.Parse("kicks.gifted", TestData.Read("webhook-kicks.json")));
        Assert.Equal(KickEventTypes.KicksGifted, kicks.Type);
        Assert.Equal(100L, kicks.Args["kicks"]);
        Assert.Equal("KickFan", kicks.Args["user"]);

        var reward = Assert.Single(KickWebhookParser.Parse("channel.reward.redemption.updated", TestData.Read("webhook-reward.json")));
        Assert.Equal("Song request", reward.Args["rewardTitle"]);
        Assert.Equal(500L, reward.Args["rewardCost"]);
        Assert.Equal("play the song", reward.Args["userInput"]);
        Assert.Equal("Requester", reward.Args["user"]);
    }

    [Fact]
    public void Livestream_status_and_metadata_payloads()
    {
        const string b = """{"user_id":100,"username":"CuebotDev"}""";
        var online = Assert.Single(KickWebhookParser.Parse("livestream.status.updated",
            $$"""{"broadcaster":{{b}},"is_live":true,"title":"Going live","started_at":"2025-01-01T11:00:00+11:00","ended_at":null}"""));
        Assert.Equal(KickEventTypes.StreamOnline, online.Type);
        Assert.Equal("Going live", online.Args["title"]);
        var offline = Assert.Single(KickWebhookParser.Parse("livestream.status.updated",
            $$"""{"broadcaster":{{b}},"is_live":false,"title":"x","started_at":"2025-01-01T11:00:00+11:00","ended_at":"2025-01-01T15:00:00+11:00"}"""));
        Assert.Equal(KickEventTypes.StreamOffline, offline.Type);

        var meta = Assert.Single(KickWebhookParser.Parse("livestream.metadata.updated",
            """{"broadcaster":""" + b + ""","metadata":{"title":"New","language":"en","has_mature_content":true,"category":{"id":15,"name":"Just Chatting","thumbnail":""}}}"""));
        Assert.Equal(KickEventTypes.ChannelUpdate, meta.Type);
        Assert.Equal("Just Chatting", meta.Args["category"]);
        Assert.Equal("15", meta.Args["categoryId"]);
        Assert.Equal(true, meta.Args["isMature"]);
    }

    [Fact]
    public void Webhook_and_realtime_produce_the_same_key_for_the_same_occurrence()
    {
        var hook = Assert.Single(KickWebhookParser.Parse("channel.followed",
            """{"broadcaster":{"user_id":100},"follower":{"user_id":5,"username":"Fan"}}"""));
        var realtime = Assert.Single(Pusher.KickPusherDecoder.Decode(new Pusher.PusherFrame("App\\Events\\FollowersUpdated", "channel.1",
            """{"followersCount":5,"username":"Fan","followed":true}"""), 100));
        Assert.Equal(hook.DedupeKey, realtime.DedupeKey);
    }
}
