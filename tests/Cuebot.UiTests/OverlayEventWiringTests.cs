using System.Text.Json.Nodes;
using Cuebot.App;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Events;

namespace Cuebot.UiTests;

/// <summary>With the app's real registrations, every platform's events reach overlay pages as StreamElements alerts.</summary>
public sealed class OverlayEventWiringTests : IAsyncDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-overlay-events-").FullName;
    private readonly AppHost _host;

    public OverlayEventWiringTests() => _host = AppHost.Create(new AppPaths(_dataDir));

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    [Theory]
    [InlineData("twitch.follow", "follower-latest", "twitch")]
    [InlineData("kick.kicksGifted", "kicks-latest", "kick")]
    [InlineData("rumble.rant", "tip-latest", "rumble")]
    [InlineData("youtube.superChat", "superchat-latest", "youtube")]
    [InlineData("streamelements.tip", "tip-latest", "donations")]
    public void Platform_events_reach_the_overlay_feed(string type, string listener, string provider)
    {
        _host.Get<OverlayEventBridge>();
        var sent = new List<JsonObject>();
        using var subscription = _host.Get<OverlayFeed>().Subscribe(m => sent.Add(JsonNode.Parse(m.Json)!.AsObject()));

        _host.Get<EventBus>().Publish(new BotEvent(type, new Dictionary<string, object?> { ["user"] = "Kris", ["amount"] = 5 }));

        Assert.Equal(2, sent.Count);
        Assert.Equal(listener, sent[0]["listener"]!.GetValue<string>());
        Assert.Equal("Kris", sent[0]["event"]!["name"]!.GetValue<string>());
        Assert.Equal(provider, sent[1]["event"]!["provider"]!.GetValue<string>());
    }

    [Fact]
    public void Alerts_update_the_stream_totals()
    {
        _host.Get<OverlayEventBridge>();
        var session = _host.Get<Cuebot.Core.Overlays.Session.SessionDataStore>();
        var bus = _host.Get<EventBus>();
        bus.Publish(new BotEvent("rumble.rant", new Dictionary<string, object?> { ["user"] = "Kris", ["amount"] = 5.0 }));
        bus.Publish(new BotEvent("streamelements.tip", new Dictionary<string, object?> { ["user"] = "Lee", ["amount"] = 12.5 }));
        bus.Publish(new BotEvent("kick.follow", new Dictionary<string, object?> { ["user"] = "Ash" }));

        var data = session.Snapshot();
        Assert.Equal(17.5, OverlayAlerts.NumberOf(data["tip-session"]!["amount"]));
        Assert.Equal(2, data["tip-session"]!["count"]!.GetValue<long>());
        Assert.Equal("Lee", data["tip-session-top-donation"]!["name"]!.GetValue<string>());
        Assert.Equal(1, data["follower-session"]!["count"]!.GetValue<long>());
    }
}
