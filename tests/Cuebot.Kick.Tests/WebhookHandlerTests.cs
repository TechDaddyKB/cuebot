using System.Security.Cryptography;
using System.Text;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Webhooks;
using Cuebot.Kick.Events;
using Cuebot.Kick.Tests.Support;
using Cuebot.Kick.Webhooks;

namespace Cuebot.Kick.Tests;

/// <summary>The relay-facing <see cref="IWebhookHandler"/> for route "kick".</summary>
public sealed class WebhookHandlerTests : IDisposable
{
    private readonly RSA _kickKey = RSA.Create(2048);
    private readonly RSA _otherKey = RSA.Create(2048);

    public void Dispose()
    {
        _kickKey.Dispose();
        _otherKey.Dispose();
    }

    private WebhookDelivery Delivery(string type, string body, string id = "01JH0000MSG", RSA? signer = null, bool omitSignature = false)
    {
        const string timestamp = "2025-01-14T16:08:06Z";
        var signature = Convert.ToBase64String((signer ?? _kickKey).SignData(
            KickWebhookVerifier.SignedPayload(id, timestamp, body), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        var headers = new Dictionary<string, string>
        {
            ["kick-event-message-id"] = id,
            ["kick-event-subscription-id"] = "sub1",
            ["kick-event-message-timestamp"] = timestamp,
            ["kick-event-type"] = type,
            ["kick-event-version"] = "1",
            ["content-type"] = "application/json",
        };
        if (!omitSignature) headers["kick-event-signature"] = signature;
        return new WebhookDelivery("relay-" + id, "kick", "", "POST", "", headers, Encoding.UTF8.GetBytes(body), DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Maps_relay_deliveries_to_kick_webhook_outcomes()
    {
        var source = new KickWebhookEventSource(_ => Task.FromResult(_kickKey.ExportSubjectPublicKeyInfoPem()));
        var events = new List<KickEvent>();
        source.EventReceived += events.Add;
        var handler = new KickWebhookHandler(source);
        var body = TestData.Read("webhook-chat.json");

        Assert.Equal("kick", handler.Service);
        Assert.Equal(WebhookOutcome.Handled, await handler.HandleAsync(Delivery("chat.message.sent", body), default));
        Assert.Equal(WebhookOutcome.Ignored, await handler.HandleAsync(Delivery("chat.message.sent", body), default));
        Assert.Equal(WebhookOutcome.Rejected, await handler.HandleAsync(Delivery("chat.message.sent", body, "forged", _otherKey), default));
        Assert.Equal(WebhookOutcome.Rejected, await handler.HandleAsync(Delivery("chat.message.sent", body, "unsigned", omitSignature: true), default));
        Assert.Equal(WebhookOutcome.Ignored, await handler.HandleAsync(Delivery("channel.unknown.thing", "{}", "id-3"), default));
        Assert.Equal(WebhookOutcome.Failed, await handler.HandleAsync(Delivery("chat.message.sent", "{oops", "id-4"), default));

        var e = Assert.Single(events);
        Assert.Equal(KickEventTypes.ChatMessage, e.Type);
        Assert.Equal("webhook", e.Source);
    }

    [Fact]
    public void Mirrors_the_relay_connection_onto_the_webhook_source()
    {
        var bus = new EventBus(new AppLog());
        var source = new KickWebhookEventSource(_ => Task.FromResult(""));
        using var handler = new KickWebhookHandler(source, bus);
        var changes = new List<bool>();
        source.ConnectionChanged += (connected, _) => changes.Add(connected);

        bus.Publish(new BotEvent("relay.connected"));
        Assert.True(source.IsConnected);
        bus.Publish(new BotEvent("relay.disconnected", new Dictionary<string, object?> { ["reason"] = "gone" }));
        Assert.False(source.IsConnected);
        Assert.Equal([true, false], changes);
    }
}
