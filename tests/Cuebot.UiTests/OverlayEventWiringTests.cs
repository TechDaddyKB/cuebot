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
}
