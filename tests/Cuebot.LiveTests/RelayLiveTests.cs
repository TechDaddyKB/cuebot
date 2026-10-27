using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Webhooks;
using Cuebot.Relay;
using Cuebot.Webhooks;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.LiveTests;

/// <summary>
/// Opt-in end-to-end test through the user's deployed Cloudflare relay (relay/README.md): a webhook posted to the
/// public URL reaches Cuebot over the relay connection and fires the custom-webhook trigger. See docs/LIVE_TESTING.md.
/// </summary>
public sealed class RelayLiveTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [LiveRelayFact]
    public async Task Webhook_posted_to_the_relay_reaches_cuebot_and_a_wrong_token_is_rejected()
    {
        var dataDirectory = Path.Combine(LiveEnvironment.DataDirectory, "relay");
        Directory.CreateDirectory(dataDirectory);
        await using var services = new ServiceCollection().AddCuebotCore(new AppPaths(dataDirectory)).AddWebhooks().AddRelay()
            .BuildServiceProvider();
        var events = new ConcurrentQueue<BotEvent>();
        services.GetRequiredService<EventBus>().Subscribe(events.Enqueue);

        var relay = services.GetRequiredService<RelayClient>();
        relay.Settings.Url = RelayLiveEnvironment.Get("CUEBOT_LIVE_RELAY_URL")!;
        relay.Settings.ConnectToken = RelayLiveEnvironment.Get("CUEBOT_LIVE_RELAY_CONNECT_TOKEN")!;
        relay.Settings.RouteSecret = RelayLiveEnvironment.Get("CUEBOT_LIVE_RELAY_ROUTE_SECRET")!;
        relay.Settings.Enabled = true;

        var custom = services.GetRequiredService<WebhookServices>().Custom;
        var name = "live-test-" + Guid.NewGuid().ToString("N")[..8];
        var hook = custom.AddHook(name);

        await relay.ConnectAsync();
        await WaitUntilAsync(() => relay.State == ConnectionState.Connected, "the relay connection");

        using var http = new HttpClient();
        var url = relay.WebhookUrl("custom") + "/" + name;
        var marker = Guid.NewGuid().ToString("N");
        using (var ok = await http.PostAsJsonAsync($"{url}?token={Uri.EscapeDataString(hook.Token)}", new { note = "dev testing", marker }))
            Assert.True(ok.IsSuccessStatusCode, $"relay answered {(int)ok.StatusCode}");

        await WaitUntilAsync(() => events.Any(e => e.Type == CustomWebhookEventTypes.Received && Equals(e.Args.GetValueOrDefault("marker"), marker)),
            "the custom webhook event");
        var received = events.First(e => e.Type == CustomWebhookEventTypes.Received);
        Assert.Equal(name, received.Args["hookName"]);
        Assert.Equal("dev testing", received.Args["note"]);

        // The relay forwards anything with the right route secret; Cuebot itself must refuse a wrong hook token.
        using (var bad = await http.PostAsJsonAsync($"{url}?token=wrong-token", new { marker = "should-not-fire" }))
            Assert.True(bad.IsSuccessStatusCode);
        await WaitUntilAsync(() => relay.RecentDeliveries.Any(d => d.Outcome == WebhookOutcome.Rejected), "the rejected delivery");
        Assert.DoesNotContain(events, e => e.Type == CustomWebhookEventTypes.Received && Equals(e.Args.GetValueOrDefault("marker"), "should-not-fire"));

        custom.RemoveHook(hook);
        await relay.DisconnectAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Timed out waiting for {what}");
            await Task.Delay(200);
        }
    }
}

internal static class RelayLiveEnvironment
{
    public static string? Get(string name)
    {
        // LiveEnvironment's static constructor loads live.env into the environment.
        RuntimeHelpers.RunClassConstructor(typeof(LiveEnvironment).TypeHandle);
        return Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
    }
}

/// <summary>Runs only with CUEBOT_LIVE_RELAY_URL, _CONNECT_TOKEN and _ROUTE_SECRET set (printed by relay/setup).</summary>
public sealed class LiveRelayFactAttribute : LiveFactAttribute
{
    public LiveRelayFactAttribute()
    {
        if (Skip is not null) return;
        if (RelayLiveEnvironment.Get("CUEBOT_LIVE_RELAY_URL") is null || RelayLiveEnvironment.Get("CUEBOT_LIVE_RELAY_CONNECT_TOKEN") is null
            || RelayLiveEnvironment.Get("CUEBOT_LIVE_RELAY_ROUTE_SECRET") is null)
            Skip = "Set CUEBOT_LIVE_RELAY_URL, CUEBOT_LIVE_RELAY_CONNECT_TOKEN and CUEBOT_LIVE_RELAY_ROUTE_SECRET (see relay/README.md)";
    }
}
