using System.Diagnostics;
using System.Text.Json.Nodes;
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
/// The documentation's pictures of the web editors (website/docs/assets/screens): the overlay designer, every built-in
/// widget's editor, the code editor's tabs, the alert designer and a live overlay. The app's real services serve example
/// overlays while tools/docs-shots/overlay-shots.mjs drives a headless Chromium through them, with chat and alerts from
/// every platform coming in. Skipped unless CUEBOT_WRITE_OVERLAY_SCREENSHOTS=1; needs Node.js, playwright-core
/// (PLAYWRIGHT_CORE = its folder) and Chromium (CHROMIUM = its path, default /usr/bin/chromium).
/// </summary>
public sealed class DocsOverlayScreenshots
{
    private static readonly string Root = FindRoot();
    private static readonly string OutputDir = Path.Combine(Root, "website", "docs", "assets", "screens");

    [Fact]
    public async Task Write_the_overlay_editor_screenshots()
    {
        Assert.SkipWhen(Environment.GetEnvironmentVariable("CUEBOT_WRITE_OVERLAY_SCREENSHOTS") != "1",
            "Set CUEBOT_WRITE_OVERLAY_SCREENSHOTS=1 to rewrite the overlay editor pictures in website/docs/assets/screens");
        var ct = TestContext.Current.CancellationToken;
        var data = Directory.CreateTempSubdirectory("cuebot-docs-overlays-").FullName;
        await using var host = AppHost.Create(new AppPaths(data));
        try
        {
            host.Get<ChatHub>();
            host.Get<OverlayEventBridge>();
            host.Get<SessionDataStore>();
            var servers = host.Get<ServerManager>();
            servers.Settings.Overlays = new OverlayServerSettings { Enabled = true, Port = 0, WidgetPort = 0 };
            await servers.RestartAsync(ServerKind.Overlays, ct);

            var store = host.Get<OverlayStore>();
            var library = host.Get<WidgetLibrary>();
            // The overlay the designer and OBS show: a streamer's main scene.
            var main = store.Create("Main", 1920, 1080);
            var chat = library.AddChatBox(main.Id)!;
            library.MoveLayer(main.Id, chat.Id, 40, 560, 520, 480);
            var alert = library.AddAlertBox(main.Id)!;
            var label = library.AddBuiltIn(main.Id, "label")!;
            var goal = library.AddBuiltIn(main.Id, "goal")!;
            var events = library.AddBuiltIn(main.Id, "eventlist")!;
            // Every other built-in widget on a second overlay, so each one's editor can be shown.
            var all = store.Create("Every widget", 1920, 1080);
            var widgets = new JsonArray
            {
                Widget("chatbox", servers.EditorUrl(main.Id, chat.Id)),
                Widget("label", servers.EditorUrl(main.Id, label.Id)),
                Widget("goal", servers.EditorUrl(main.Id, goal.Id)),
                Widget("eventlist", servers.EditorUrl(main.Id, events.Id)),
            };
            foreach (var kind in BuiltInWidgets.Kinds.Select(k => k.Id).Except(["label", "goal", "eventlist"]))
                widgets.Add(Widget(kind, servers.EditorUrl(all.Id, library.AddBuiltIn(all.Id, kind)!.Id)));
            var blank = library.AddCustomWidget(all.Id, "My widget", BuiltInWidgets.Blank())!;
            // The designer guide's worked example starts from an overlay with just a countdown on it.
            var starting = store.Create("Starting soon", 1920, 1080);
            var countdown = library.AddBuiltIn(starting.Id, "countdown")!;
            library.MoveLayer(starting.Id, countdown.Id, 640, 560, 640, 260);

            var urls = new JsonObject
            {
                ["overlay"] = servers.OverlayUrl(store.Get(main.Id)!),
                ["designer"] = servers.DesignerUrl(main.Id),
                ["alertbox"] = servers.EditorUrl(main.Id, alert.Id),
                ["blank"] = servers.EditorUrl(all.Id, blank.Id),
                ["widgets"] = widgets,
                ["startingDesigner"] = servers.DesignerUrl(starting.Id),
                ["startingOverlay"] = servers.OverlayUrl(store.Get(starting.Id)!),
                ["logo"] = Path.Combine(Root, "website", "docs", "assets", "cuebot.png"),
            };
            var urlsFile = Path.Combine(data, "urls.json");
            await File.WriteAllTextAsync(urlsFile, urls.ToJsonString(), ct);

            // Chat and alerts from every platform while the pictures are taken, so the chat box and event list are not empty.
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var bus = host.Get<EventBus>();
            var feed = Task.Run(async () =>
            {
                for (var i = 0; !stop.IsCancellationRequested; i++)
                {
                    foreach (var e in OverlayWidgetDemo.Step(i)) bus.Publish(e);
                    try { await Task.Delay(i < 16 ? 150 : 1500, stop.Token); } catch (OperationCanceledException) { }
                }
            }, ct);

            var node = new ProcessStartInfo("node", [Path.Combine(Root, "tools", "docs-shots", "overlay-shots.mjs"), urlsFile, OutputDir])
            {
                RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var process = Process.Start(node)!;
            var output = process.StandardOutput.ReadToEndAsync(ct);
            var errors = process.StandardError.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await stop.CancelAsync();
            await feed;
            Assert.True(process.ExitCode == 0, $"overlay-shots.mjs failed ({process.ExitCode}):\n{await output}\n{await errors}");
        }
        finally
        {
            try { Directory.Delete(data, recursive: true); } catch (IOException) { }
        }
    }

    private static JsonObject Widget(string kind, string url) => new() { ["kind"] = kind, ["url"] = url };

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Cuebot.slnx"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Cuebot.slnx not found");
    }
}
