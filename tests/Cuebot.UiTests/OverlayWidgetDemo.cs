using Cuebot.App;
using Cuebot.Core;
using Cuebot.Core.Chat;
using Cuebot.Core.Events;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Events;
using Cuebot.Core.Overlays.Session;
using Cuebot.Core.Overlays.Widgets;
using Cuebot.Server;

namespace Cuebot.UiTests;

/// <summary>
/// A manual check, skipped unless CUEBOT_DEMO_WIDGET names a StreamElements widget zip: the app's real services serve an
/// overlay with that widget on port 7499 (its URL is written to overlay-url.txt, and the widget editor's to editor-url.txt,
/// in CUEBOT_DEMO_OUT, or the temp folder) and
/// send chat and alerts from Twitch, Kick, Rumble and YouTube for CUEBOT_DEMO_SECONDS (default 120), so the widget can be
/// watched in a browser or OBS. The widget file is only read, never copied into the repository.
/// </summary>
public sealed class OverlayWidgetDemo
{
    [Fact]
    public async Task Run_a_widget_with_every_platform()
    {
        var zip = Environment.GetEnvironmentVariable("CUEBOT_DEMO_WIDGET");
        Assert.SkipWhen(string.IsNullOrEmpty(zip) || !File.Exists(zip), "Set CUEBOT_DEMO_WIDGET to a widget zip to run the overlay demo");
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("CUEBOT_DEMO_SECONDS"), out var s) ? s : 120;
        var output = Environment.GetEnvironmentVariable("CUEBOT_DEMO_OUT") ?? Path.GetTempPath();

        var data = Directory.CreateTempSubdirectory("cuebot-overlay-demo-").FullName;
        await using var host = AppHost.Create(new AppPaths(data));
        try
        {
            host.Get<ChatHub>();
            host.Get<OverlayEventBridge>();
            host.Get<SessionDataStore>();
            var servers = host.Get<ServerManager>();
            servers.Settings.Overlays = new OverlayServerSettings { Enabled = true, Port = 7499 };
            await servers.RestartAsync(ServerKind.Overlays, TestContext.Current.CancellationToken);

            var overlay = host.Get<OverlayStore>().Create("Widget demo", 1920, 1080);
            OverlayLayer layer;
            await using (var file = File.OpenRead(zip!))
                layer = host.Get<WidgetLibrary>().AddCustomWidget(overlay.Id, Path.GetFileNameWithoutExtension(zip!), WidgetImport.FromZip(file))!;
            var url = servers.OverlayUrl(host.Get<OverlayStore>().Get(overlay.Id)!);
            await File.WriteAllTextAsync(Path.Combine(output, "editor-url.txt"), servers.EditorUrl(overlay.Id, layer.Id), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(output, "overlay-url.txt"), url, TestContext.Current.CancellationToken);

            var bus = host.Get<EventBus>();
            var until = DateTime.UtcNow.AddSeconds(seconds);
            for (var i = 0; DateTime.UtcNow < until; i++)
            {
                foreach (var e in Step(i)) bus.Publish(e);
                await Task.Delay(TimeSpan.FromSeconds(2.5), TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            try { Directory.Delete(data, recursive: true); } catch (IOException) { }
        }
    }

    private static IEnumerable<BotEvent> Step(int i)
    {
        var n = i + 1;
        switch (i % 8)
        {
            case 0:
                yield return Chat("twitch.chatMessage", "TwitchFan", "t1", $"Hello from Twitch Kappa #{n}", new()
                {
                    ["userName"] = "twitchfan", ["color"] = "#9146FF", ["badges"] = "moderator/1,subscriber/0", ["emotes"] = "25:18-22",
                    ["isModerator"] = true, ["isSubscriber"] = true,
                });
                break;
            case 1:
                yield return Chat("kick.chatMessage", "KickViewer", "k1", $"Kick chat here #{n} KEKW", new()
                {
                    ["rawMessage"] = $"Kick chat here #{n} [emote:37226:KEKW]", ["userName"] = "kickviewer", ["color"] = "#53FC18",
                    ["badge"] = new List<Dictionary<string, object?>> { new() { ["id"] = "og", ["name"] = "OG", ["info"] = 0 } },
                });
                break;
            case 2:
                yield return Chat("rumble.chatMessage", "RumbleRumbler", "r1", $"Rumble says hi #{n}", new()
                {
                    ["userName"] = "RumbleRumbler", ["badges"] = "verified,premium", ["isVerified"] = true,
                });
                break;
            case 3:
                yield return Chat("youtube.chatMessage", "YouTube Member", "y1", $"Watching on YouTube #{n}", new()
                {
                    ["userName"] = "YouTube Member", ["isMember"] = true, ["isSubscriber"] = true,
                });
                break;
            case 4:
                yield return Alert("kick.follow", "NewKickFollower");
                break;
            case 5:
                yield return Alert("rumble.rant", "RantGiver", new() { ["amount"] = 5.0, ["currency"] = "USD", ["message"] = "Great stream!" });
                break;
            case 6:
                yield return Alert("twitch.resub", "LoyalSub", new() { ["tier"] = "1", ["cumulativeMonths"] = 12, ["message"] = "A year already" });
                break;
            default:
                yield return Alert("youtube.superChat", "SuperFan", new() { ["amount"] = 10.0, ["currency"] = "USD", ["amountText"] = "$10.00" });
                break;
        }
    }

    private static BotEvent Chat(string type, string user, string userId, string message, Dictionary<string, object?> extra)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["user"] = user, ["userId"] = userId, ["message"] = message, ["rawInput"] = message,
            ["messageId"] = Guid.NewGuid().ToString("N"),
        };
        foreach (var (key, value) in extra) args[key] = value;
        return new BotEvent(type, args);
    }

    private static BotEvent Alert(string type, string user, Dictionary<string, object?>? extra = null)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["user"] = user, ["userId"] = user.ToLowerInvariant() };
        foreach (var (key, value) in extra ?? []) args[key] = value;
        return new BotEvent(type, args);
    }
}
