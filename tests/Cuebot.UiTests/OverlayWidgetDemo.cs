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
/// A manual check, skipped unless CUEBOT_DEMO_WIDGET names a StreamElements widget zip (or is "chatbox" for Cuebot's chat box
/// in both layouts, "widgets" for the label, goal, event list, credits and countdown, "engagement" for the emote rain, hype cup and hype boss, "polls" for a Cuebot chat poll voted from every platform, "giveaway" for a giveaway entered from every platform, "media" for the media request player (requests over the API), "extras" for the charity, product showcase and seasonal
/// widgets (with merch purchases and Twitch charity progress), "streamlabs" for sample Streamlabs chat box, alert box and event list code, a folder of StreamElements overlay JSON files to import them, or "alertbox" for Cuebot's alert box beside its chat box, with the REST API on port 7498 (a throwaway
/// token in api-token.txt) and speech paused, so alert TTS and the queue controls can be tried): the app's real services serve an
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
        var chatBox = zip == "chatbox";
        var alertBox = zip == "alertbox";
        var builtIns = zip == "widgets";
        var engagement = zip == "engagement";
        var polls = zip == "polls";
        var giveaway = zip == "giveaway";
        var media = zip == "media";
        var extras = zip == "extras";
        var streamlabs = zip == "streamlabs";
        // A folder of StreamElements overlay JSON files (as the overlays API returns them), imported read only.
        var seImport = Directory.Exists(zip);
        Assert.SkipWhen(string.IsNullOrEmpty(zip) || (!chatBox && !alertBox && !builtIns && !engagement && !polls && !giveaway && !media && !extras && !streamlabs && !seImport
                                                      && !File.Exists(zip)),
            "Set CUEBOT_DEMO_WIDGET to a widget zip (or \"chatbox\" or \"alertbox\" for Cuebot's own) to run the overlay demo");
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
            if (alertBox || polls || giveaway || media)
            {
                var token = Cuebot.Server.Api.ApiToken.Generate();
                servers.Settings.Api = new Cuebot.Server.Api.ApiServerSettings { Enabled = true, Port = 7498, Token = token };
                // MCP too (on the API port at /mcp, same token), so an AI app can drive the demo.
                servers.Settings.Mcp = new Cuebot.Server.Mcp.McpServerSettings { Enabled = true, RequireConfirmation = true };
                await servers.RestartAsync(ServerKind.Api, TestContext.Current.CancellationToken);
                await File.WriteAllTextAsync(Path.Combine(output, "api-token.txt"), token, TestContext.Current.CancellationToken);
                var speech = host.Get<Cuebot.Speech.SpeechService>();
                speech.Paused = true;
                await speech.StartAsync(TestContext.Current.CancellationToken);
            }

            var overlay = host.Get<OverlayStore>().Create("Widget demo", 1920, 1080);
            OverlayLayer layer;
            if (chatBox)
            {
                // Both layouts side by side: Cuebot's on the left, the YouTube popout one on the right.
                layer = host.Get<WidgetLibrary>().AddChatBox(overlay.Id)!;
                var youtube = host.Get<WidgetLibrary>().AddChatBox(overlay.Id, "youtube")!;
                host.Get<WidgetLibrary>().MoveLayer(overlay.Id, youtube.Id, 1400, youtube.Y, 480, youtube.Height);
            }
            else if (builtIns)
            {
                var library = host.Get<WidgetLibrary>();
                layer = library.AddBuiltIn(overlay.Id, "label")!;
                foreach (var kind in new[] { "goal", "eventlist", "credits", "countdown" }) library.AddBuiltIn(overlay.Id, kind);
            }
            else if (seImport)
            {
                var urls = new List<string>();
                OverlayLayer? first = null;
                foreach (var file in Directory.GetFiles(zip!, "*.json").Order())
                {
                    var json = System.Text.Json.Nodes.JsonNode.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken))!.AsObject();
                    var result = StreamElementsOverlayImport.Import(json, host.Get<OverlayStore>(), host.Get<WidgetLibrary>());
                    first ??= result.Overlay.Layers.FirstOrDefault();
                    urls.Add($"{result.Overlay.Name}\t{result.Imported} imported\tskipped: {string.Join("; ", result.Skipped)}\t{servers.OverlayUrl(result.Overlay)}");
                    // And back through a package, as if shared: the copy should look the same.
                    var packages = host.Get<Cuebot.Core.Overlays.Sharing.OverlayPackages>();
                    using var package = new MemoryStream();
                    var exported = packages.ExportOverlay(result.Overlay.Id, package, includeCustomCode: true);
                    package.Position = 0;
                    var copy = await packages.ImportAsync(package, ct: TestContext.Current.CancellationToken);
                    urls.Add($"{copy.Overlay.Name} (package, {package.Length} bytes)\t{copy.Layers} layers\tleft out: {string.Join("; ", copy.LeftOut)}\t{servers.OverlayUrl(copy.Overlay)}");
                }
                await File.WriteAllLinesAsync(Path.Combine(output, "se-import.txt"), urls, TestContext.Current.CancellationToken);
                layer = first!;
                overlay = host.Get<OverlayStore>().All().First(o => o.Layers.Any(l => l.Id == layer.Id));
            }
            else if (streamlabs)
            {
                var library = host.Get<WidgetLibrary>();
                layer = library.AddCustomWidget(overlay.Id, "Streamlabs chat", WidgetImport.FromPanes(SlChatHtml, SlChatCss, SlChatJs, "", ""))!;
                library.MoveLayer(overlay.Id, layer.Id, 40, 400, 600, 640);
                var alert = library.AddCustomWidget(overlay.Id, "Streamlabs alert", WidgetImport.FromPanes(SlAlertHtml, SlAlertCss, SlAlertJs, "", ""))!;
                library.MoveLayer(overlay.Id, alert.Id, 660, 80, 600, 300);
                var list = library.AddCustomWidget(overlay.Id, "Streamlabs events", WidgetImport.FromPanes(SlListHtml, SlListCss, "", "", ""))!;
                library.MoveLayer(overlay.Id, list.Id, 1400, 80, 480, 600);
            }
            else if (extras)
            {
                var library = host.Get<WidgetLibrary>();
                layer = library.AddBuiltIn(overlay.Id, "products")!;
                library.AddBuiltIn(overlay.Id, "charity");
                library.AddBuiltIn(overlay.Id, "seasonal");
            }
            else if (media)
            {
                layer = host.Get<WidgetLibrary>().AddBuiltIn(overlay.Id, "media")!;
                host.Get<Cuebot.Core.Media.MediaRequests>().UpdateSettings(s => s.MaxSeconds = 25);
            }
            else if (giveaway)
            {
                layer = host.Get<WidgetLibrary>().AddBuiltIn(overlay.Id, "giveaway")!;
                var giveaways = host.Get<Cuebot.Core.Giveaways.Giveaways>();
                giveaways.AnnounceWinners = false;
                giveaways.Open(new Cuebot.Core.Giveaways.GiveawayOptions { Title = "A Steam game of your choice", Seconds = 0 });
            }
            else if (polls)
            {
                layer = host.Get<WidgetLibrary>().AddBuiltIn(overlay.Id, "poll")!;
                host.Get<Cuebot.Core.Polls.ChatPolls>().StartPoll("What should we play next?", ["Horror game", "Racing", "Just chatting"], 0);
            }
            else if (engagement)
            {
                var library = host.Get<WidgetLibrary>();
                layer = library.AddBuiltIn(overlay.Id, "emoterain")!;
                library.AddBuiltIn(overlay.Id, "hypecup");
                library.AddBuiltIn(overlay.Id, "hypeboss");
            }
            else if (alertBox)
            {
                layer = host.Get<WidgetLibrary>().AddAlertBox(overlay.Id)!;
                host.Get<WidgetLibrary>().AddChatBox(overlay.Id);
            }
            else
            {
                await using var file = File.OpenRead(zip!);
                layer = host.Get<WidgetLibrary>().AddCustomWidget(overlay.Id, Path.GetFileNameWithoutExtension(zip!), WidgetImport.FromZip(file))!;
            }
            // CUEBOT_DEMO_PACKAGE: also save the demo overlay as a package there (Cuebot's own widgets only), for example
            // to make a gallery item.
            if (Environment.GetEnvironmentVariable("CUEBOT_DEMO_PACKAGE") is { Length: > 0 } packagePath)
            {
                if (Environment.GetEnvironmentVariable("CUEBOT_DEMO_PACKAGE_NAME") is { Length: > 0 } packageName)
                    host.Get<OverlayStore>().Update(overlay.Id, o => o.Name = packageName);
                await using var packageFile = File.Create(packagePath);
                host.Get<Cuebot.Core.Overlays.Sharing.OverlayPackages>().ExportOverlay(overlay.Id, packageFile, includeCustomCode: false);
            }
            var url = servers.OverlayUrl(host.Get<OverlayStore>().Get(overlay.Id)!);
            await File.WriteAllTextAsync(Path.Combine(output, "editor-url.txt"), servers.EditorUrl(overlay.Id, layer.Id), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(output, "designer-url.txt"), servers.DesignerUrl(overlay.Id), TestContext.Current.CancellationToken);
            await File.WriteAllTextAsync(Path.Combine(output, "overlay-url.txt"), url, TestContext.Current.CancellationToken);

            var bus = host.Get<EventBus>();
            var until = DateTime.UtcNow.AddSeconds(seconds);
            for (var i = 0; DateTime.UtcNow < until; i++)
            {
                foreach (var e in Step(i)) bus.Publish(e);
                if (polls) bus.Publish(Vote(i));
                if (giveaway) bus.Publish(Vote(i, "!join"));
                if (extras && i % 3 == 1)
                    bus.Publish(Alert("streamelements.merch", $"Buyer{i}", new() { ["amount"] = 45.0, ["currency"] = "USD", ["product"] = "Cuebot hoodie" }));
                if (extras)
                    bus.Publish(new BotEvent("twitch.charityProgress", new Dictionary<string, object?>
                    {
                        ["charityName"] = "Hearts for Kids", ["currentAmount"] = 50.0 * (i + 1), ["targetAmount"] = 1000.0, ["currency"] = "USD",
                        ["charityLogo"] = "",
                    }));
                await Task.Delay(TimeSpan.FromSeconds(2.5), TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            try { Directory.Delete(data, recursive: true); } catch (IOException) { }
        }
    }

    // Sample Streamlabs custom code written for this demo (the shapes of Streamlabs' starter templates).
    private const string SlChatHtml = """
        <div id="log" class="sl__chat__layout"></div>
        <script type="text/template" id="chatlist_item">
          <div data-from="{from}" data-id="{messageId}">
            <span class="meta" style="color: {color}"><span class="badges"></span><span class="name">{from}</span></span>
            <span class="message">{message}</span>
          </div>
        </script>
        """;

    private const string SlChatCss = """
        body { background: {background_color}; color: {text_color}; font: 700 {font_size} sans-serif; }
        #log { position: absolute; bottom: 0; left: 0; right: 0; padding: 8px; }
        #log > div { background: rgba(20, 20, 30, .8); border-radius: 8px; margin: 6px 0; padding: 6px 10px; }
        #log .badge { height: 18px; margin-right: 4px; vertical-align: middle; }
        #log .emote { height: 24px; vertical-align: middle; }
        #log .platform { font-size: 12px; opacity: .6; margin-left: 6px; }
        """;

    private const string SlChatJs = """
        document.addEventListener('onEventReceived', function (obj) {
          if (obj.detail.command !== 'PRIVMSG') return;
          var item = document.querySelector('[data-id="' + obj.detail.messageId + '"] .meta');
          if (item) item.insertAdjacentHTML('beforeend', '<span class="platform">' + obj.detail.platform + ' ' + obj.detail.userType + '</span>');
        });
        """;

    private const string SlAlertHtml = """
        <div id="alert-wrap"><div id="inner-wrap">
          <div id="alert-text">{messageTemplate}</div>
          <div id="alert-message">{userMessage}</div>
        </div></div>
        """;

    private const string SlAlertCss = """
        #alert-wrap { font: 900 36px sans-serif; color: #fff; text-align: center; text-shadow: 0 2px 6px #000; padding-top: 40px; }
        #alert-text .sl-name, #alert-text .sl-amount { color: #ffcc33; }
        #alert-message { font-size: 22px; font-weight: 600; margin-top: 10px; }
        #alert-type { font-size: 14px; opacity: .7; }
        """;

    private const string SlAlertJs = """
        $(document).ready(function () {
          $('#inner-wrap').append('<div id="alert-type">' + '{name}' + ' via Streamlabs code</div>');
        });
        """;

    private const string SlListHtml = """
        <script type="text/template" id="eventlist_item">
          <li class="{eventType}"><span class="tag">{tag}</span> <span class="from">{from}</span></li>
        </script>
        """;

    private const string SlListCss = """
        .widget-EventList { list-style: none; margin: 0; padding: 0; font: 700 {font_size} "{font_family}", sans-serif; color: {text_color}; }
        .widget-EventList li { background: rgba(20, 20, 30, .8); border-left: 4px solid #4ea1ff; margin: 6px 0; padding: 8px 12px; }
        .widget-EventList li.donation, .widget-EventList li.superchat { border-color: #ffcc33; }
        .widget-EventList .tag { color: #4ea1ff; }
        """;

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

    /// <summary>A new viewer on each platform in turn votes in the Cuebot chat poll.</summary>
    private static BotEvent Vote(int i, string? text = null)
    {
        var platform = (string[])["twitch", "kick", "rumble", "youtube"];
        var name = $"Voter{i + 1}";
        return Chat($"{platform[i % 4]}.chatMessage", name, $"voter{i}", text ?? $"!vote {1 + i * i % 3}", new() { ["userName"] = name.ToLowerInvariant() });
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
