using System.Net;
using System.Text;
using Cuebot.Core.Webhooks;
using Cuebot.Webhooks.Services;
using Cuebot.Webhooks.Tests.Support;

namespace Cuebot.Webhooks.Tests;

public sealed class VerificationTests : IDisposable
{
    private const string KofiToken = "8e7f3a2b-1c4d-4e5f-9a8b-7c6d5e4f3a2b";
    private readonly Harness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void Hmac_helpers_match_the_rfc_test_vectors()
    {
        var data = Encoding.ASCII.GetBytes("what do ya want for nothing?");
        // RFC 2104 / RFC 2202 test case 2 (HMAC-MD5) and RFC 4231 test case 2 (HMAC-SHA256), key "Jefe".
        Assert.Equal("750c783e6ab0b503eaa86e310a5db738", WebhookVerification.HmacMd5Hex("Jefe", data));
        Assert.Equal(Convert.ToBase64String(Convert.FromHexString("5bdcc146bf60754e6a042426089575c75a003f089d2739839dec58b964ec3843")),
            WebhookVerification.HmacSha256Base64("Jefe", data));
        Assert.True(WebhookVerification.CheckHmacMd5Hex("Jefe", data, "750C783E6AB0B503EAA86E310A5DB738"));
        Assert.False(WebhookVerification.CheckHmacMd5Hex("Jefe", data, "not hex"));
        Assert.False(WebhookVerification.CheckHmacSha256Base64("Jefe", data, "%%%"));
        Assert.False(WebhookVerification.CheckHmacSha256Base64("", data, WebhookVerification.HmacSha256Base64("", data)));
        Assert.False(WebhookVerification.TokensEqual("", ""));
        Assert.True(WebhookVerification.TokensEqual("abc", "abc"));
        Assert.False(WebhookVerification.TokensEqual("abc", "abd"));
        Assert.False(WebhookVerification.TokensEqual("abc", "abcd"));
        Assert.Equal(32, WebhookVerification.NewToken().Length);
        Assert.NotEqual(WebhookVerification.NewToken(), WebhookVerification.NewToken());
    }

    private static string KofiForm(string json) => "data=" + WebUtility.UrlEncode(json);

    [Fact]
    public void Kofi_accepts_the_matching_verification_token_and_rejects_others()
    {
        var kofi = new KofiWebhookHandler(_h.Settings, _h.Bus, _h.Log);
        var body = KofiForm(Harness.Data("kofi-donation.json"));
        Assert.Equal(WebhookOutcome.NotConfigured, kofi.Handle(Harness.Delivery("kofi", body)));

        kofi.Settings.Secret = "wrong-token";
        Assert.Equal(WebhookOutcome.Rejected, kofi.Handle(Harness.Delivery("kofi", body)));
        Assert.Empty(_h.Published);

        kofi.Settings.Secret = KofiToken;
        Assert.Equal(WebhookOutcome.Handled, kofi.Handle(Harness.Delivery("kofi", body)));
        Assert.Single(_h.Events(KofiEventTypes.Donation));

        // A body without the token, or not Ko-fi's shape at all, is rejected too.
        Assert.Equal(WebhookOutcome.Rejected, kofi.Handle(Harness.Delivery("kofi", KofiForm("""{"type":"Donation","message_id":"x"}"""))));
        Assert.Equal(WebhookOutcome.Rejected, kofi.Handle(Harness.Delivery("kofi", "garbage")));
        Assert.Single(_h.Events(KofiEventTypes.Donation));
    }

    [Fact]
    public void Patreon_checks_the_hmac_md5_signature_of_the_raw_body()
    {
        var patreon = new PatreonWebhookHandler(_h.Settings, _h.Bus, _h.Log);
        patreon.Settings.Secret = "patreon-webhook-secret";
        var body = Harness.Data("patreon-pledge-create.json");
        var good = WebhookVerification.HmacMd5Hex("patreon-webhook-secret", Encoding.UTF8.GetBytes(body));
        Dictionary<string, string> Headers(string sig) => new() { ["X-Patreon-Event"] = "members:pledge:create", ["X-Patreon-Signature"] = sig };

        Assert.Equal(WebhookOutcome.Rejected, patreon.Handle(Harness.Delivery("patreon", body, Headers(WebhookVerification.HmacMd5Hex("other", Encoding.UTF8.GetBytes(body))))));
        Assert.Equal(WebhookOutcome.Rejected, patreon.Handle(Harness.Delivery("patreon", body + " ", Headers(good))));
        Assert.Equal(WebhookOutcome.Rejected, patreon.Handle(Harness.Delivery("patreon", body, new Dictionary<string, string> { ["X-Patreon-Event"] = "members:pledge:create" })));
        Assert.Empty(_h.Published);
        Assert.Equal(WebhookOutcome.Handled, patreon.Handle(Harness.Delivery("patreon", body, Headers(good))));
        Assert.Single(_h.Events(PatreonEventTypes.PledgeCreated));
    }

    [Fact]
    public void Shopify_checks_the_base64_hmac_sha256_header()
    {
        var shopify = new ShopifyWebhookHandler(_h.Settings, _h.Bus, _h.Log);
        shopify.Settings.Secret = "shpss_client_secret";
        var body = Harness.Data("shopify-order.json");
        Dictionary<string, string> Headers(string sig) => new() { ["X-Shopify-Topic"] = "orders/create", ["X-Shopify-Hmac-Sha256"] = sig, ["X-Shopify-Event-Id"] = "evt-1" };

        Assert.Equal(WebhookOutcome.Rejected, shopify.Handle(Harness.Delivery("shopify", body, Headers(WebhookVerification.HmacSha256Base64("nope", Encoding.UTF8.GetBytes(body))))));
        Assert.Equal(WebhookOutcome.Rejected, shopify.Handle(Harness.Delivery("shopify", body, Headers(""))));
        Assert.Empty(_h.Published);
        var good = WebhookVerification.HmacSha256Base64("shpss_client_secret", Encoding.UTF8.GetBytes(body));
        Assert.Equal(WebhookOutcome.Handled, shopify.Handle(Harness.Delivery("shopify", body, Headers(good))));
    }

    [Fact]
    public void Fourthwall_checks_the_base64_hmac_sha256_header_including_the_platform_app_variant()
    {
        var fw = new FourthwallWebhookHandler(_h.Settings, _h.Bus, _h.Log);
        fw.Settings.Secret = "fw-secret";
        var body = Harness.Data("fourthwall-donation.json");
        var good = WebhookVerification.HmacSha256Base64("fw-secret", Encoding.UTF8.GetBytes(body));

        Assert.Equal(WebhookOutcome.Rejected, fw.Handle(Harness.Delivery("fourthwall", body,
            new Dictionary<string, string> { ["X-Fourthwall-Hmac-SHA256"] = WebhookVerification.HmacSha256Base64("bad", Encoding.UTF8.GetBytes(body)) })));
        Assert.Empty(_h.Published);
        Assert.Equal(WebhookOutcome.Handled, fw.Handle(Harness.Delivery("fourthwall", body, new Dictionary<string, string> { ["X-Fourthwall-Hmac-SHA256"] = good })));

        var other = Harness.Data("fourthwall-order-placed.json");
        Assert.Equal(WebhookOutcome.Handled, fw.Handle(Harness.Delivery("fourthwall", other,
            new Dictionary<string, string> { ["X-Fourthwall-Hmac-Apps-SHA256"] = WebhookVerification.HmacSha256Base64("fw-secret", Encoding.UTF8.GetBytes(other)) })));
    }

    [Fact]
    public void Throne_needs_the_url_token_in_the_path_query_or_header()
    {
        var throne = new ThroneWebhookHandler(_h.Settings, _h.Bus, _h.Log);
        var body = Harness.Data("throne-gift-purchased.json");
        Assert.Equal(WebhookOutcome.NotConfigured, throne.Handle(Harness.Delivery("throne", body, path: "anything")));

        throne.Settings.Secret = "throne-token-123";
        Assert.Equal(WebhookOutcome.Rejected, throne.Handle(Harness.Delivery("throne", body)));
        Assert.Equal(WebhookOutcome.Rejected, throne.Handle(Harness.Delivery("throne", body, path: "throne-token-12")));
        Assert.Equal(WebhookOutcome.Rejected, throne.Handle(Harness.Delivery("throne", body, query: "token=nope")));
        Assert.Empty(_h.Published);

        Assert.Equal(WebhookOutcome.Handled, throne.Handle(Harness.Delivery("throne", body, path: "throne-token-123")));
        Assert.Equal(WebhookOutcome.Handled, throne.Handle(Harness.Delivery("throne", Harness.Data("throne-contribution-flat.json"), query: "?token=throne-token-123")));
        Assert.Equal(WebhookOutcome.Handled, throne.Handle(Harness.Delivery("throne", body.Replace("54900", "100", StringComparison.Ordinal),
            new Dictionary<string, string> { ["X-Cuebot-Token"] = "throne-token-123" })));
    }

    [Fact]
    public void Custom_hooks_check_their_own_token_from_query_or_header()
    {
        var custom = new CustomWebhookHandler(_h.Settings, _h.Bus, _h.Log);
        var body = Harness.Data("custom-ifttt.json");
        Assert.Equal(WebhookOutcome.NotConfigured, custom.Handle(Harness.Delivery("custom", body, path: "door")));

        custom.Hooks.Add(new CustomWebhookSettings { Name = "door", Token = "door-token" });
        custom.Hooks.Add(new CustomWebhookSettings { Name = "bell", Token = "bell-token" });
        custom.Hooks.Add(new CustomWebhookSettings { Name = "notoken", Token = "" });

        Assert.Equal(WebhookOutcome.NotConfigured, custom.Handle(Harness.Delivery("custom", body, path: "unknown", query: "token=door-token")));
        Assert.Equal(WebhookOutcome.NotConfigured, custom.Handle(Harness.Delivery("custom", body, path: "notoken")));
        Assert.Equal(WebhookOutcome.NotConfigured, custom.Handle(Harness.Delivery("custom", body, path: "")));
        Assert.Equal(WebhookOutcome.Rejected, custom.Handle(Harness.Delivery("custom", body, path: "door")));
        Assert.Equal(WebhookOutcome.Rejected, custom.Handle(Harness.Delivery("custom", body, path: "door", query: "token=bell-token")));
        Assert.Equal(WebhookOutcome.Rejected, custom.Handle(Harness.Delivery("custom", body, path: "door",
            headers: new Dictionary<string, string> { ["X-Cuebot-Token"] = "door-token-x" })));
        Assert.Empty(_h.Published);

        Assert.Equal(WebhookOutcome.Handled, custom.Handle(Harness.Delivery("custom", body, path: "door", query: "token=door-token")));
        Assert.Equal(WebhookOutcome.Handled, custom.Handle(Harness.Delivery("custom", body, path: "DOOR/extra",
            headers: new Dictionary<string, string> { ["X-Cuebot-Token"] = "door-token" })));
        Assert.Equal(2, _h.Events(CustomWebhookEventTypes.Received).Length);

        // A switched-off hook is verified but publishes nothing.
        custom.Hooks[1].Enabled = false;
        Assert.Equal(WebhookOutcome.Ignored, custom.Handle(Harness.Delivery("custom", body, path: "bell", query: "token=bell-token")));
        Assert.Equal(2, _h.Published.Count);
    }

    [Fact]
    public void Rejections_are_logged_without_the_secret()
    {
        var shopify = new ShopifyWebhookHandler(_h.Settings, _h.Bus, _h.Log);
        shopify.Settings.Secret = "super-secret-signing-key";
        shopify.Handle(Harness.Delivery("shopify", "{}", new Dictionary<string, string> { ["X-Shopify-Hmac-Sha256"] = "AAAA" }));
        Assert.Equal(WebhookOutcome.Rejected, shopify.LastDelivery!.Outcome);
        Assert.DoesNotContain("super-secret-signing-key", shopify.LastDelivery.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(_h.Log.Recent(), e => e.Message.Contains("super-secret-signing-key", StringComparison.Ordinal));
    }
}
