using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.StreamApps.Common;
using static Cuebot.StreamApps.Common.Steps;

namespace Cuebot.StreamApps.Meld;

/// <summary>Every Meld Studio sub-action, as a table. Type ids are stored in actions.json and must never change.</summary>
internal static class MeldSubActions
{
    public const string Category = "Meld Studio";

    public static readonly string[] TypeIds =
    [
        "meldstudio.showScene", "meldstudio.getCurrentScene", "meldstudio.getStatus", "meldstudio.recording", "meldstudio.streaming",
        "meldstudio.takeScreenshot", "meldstudio.takeVerticalScreenshot", "meldstudio.setLayerVisibility", "meldstudio.setEffectState",
        "meldstudio.setLayerTrackMuted", "meldstudio.setTrackMuted", "meldstudio.setBrowserLayerUrl", "meldstudio.setLayerSource",
        "meldstudio.setTrackMonitoring", "meldstudio.setTrackGain", "meldstudio.stageScene", "meldstudio.showStagedScene",
        "meldstudio.recordClip", "meldstudio.replay", "meldstudio.toggleVirtualCamera", "meldstudio.mediaControl",
        "meldstudio.widgetEvent", "meldstudio.setProperty", "meldstudio.sendCommand", "meldstudio.getScenes",
    ];

    private static readonly string[] WidgetEvents =
    [
        "STOPWATCH_RESET", "STOPWATCH_PAUSE", "STOPWATCH_RESUME", "COUNTDOWN_RESET", "COUNTDOWN_PAUSE", "COUNTDOWN_RESUME",
        "CONFETTIFALL_TRIGGER", "CONFETTIPOP_TRIGGER", "SUBATHONTIMER_RESET", "SUBATHONTIMER_PAUSE", "SUBATHONTIMER_RESUME",
        "SUBATHONTIMER_ADDTIME", "WHEELSPIN_SPIN", "COUNTER_INCREMENT", "COUNTER_DECREMENT",
    ];

    public static IReadOnlyList<ISubActionHandler> Create(MeldStudioService meld)
    {
        ParameterDescriptor Scene(string help = "Scene name (or id)") => new("scene", "Scene")
        {
            Help = help,
            Suggestions = () => meld.Session.Scenes.Select(s => s.Name),
        };
        ParameterDescriptor OptionalScene() => Scene("Scene containing the layer; leave empty to search the current scene, then all scenes");
        ParameterDescriptor Layer() => new("layer", "Layer")
        {
            Help = "Layer name (or id)",
            Suggestions = () => meld.Session.Layers.Select(l => l.Name).Distinct(),
        };
        ParameterDescriptor Track() => new("track", "Track")
        {
            Help = "Global track name, or the layer name for a layer's track (or the track id)",
            Suggestions = () => meld.Session.Tracks.Select(t => meld.Session.DisplayName(t)).Distinct(),
        };

        return
        [
            Make("meldstudio.showScene", "Show scene", [Scene()],
                async (ctx, v, ct) => ctx.Args["meldStudio.sceneName"] = (await meld.ShowSceneAsync(Require(v["scene"], "Scene"), ct).ConfigureAwait(false)).Name),
            Make("meldstudio.getCurrentScene", "Get current scene", [], (ctx, _, _) =>
            {
                var scene = meld.IsConnected ? meld.Session.CurrentScene : throw new StreamAppNotConnectedException(meld.Name);
                ctx.Args["currentScene"] = scene?.Name ?? "";
                ctx.Args["currentSceneId"] = scene?.Id ?? "";
                return Task.CompletedTask;
            }),
            Make("meldstudio.getStatus", "Get status", [], (ctx, _, _) =>
            {
                ctx.Args["meldStudio.isConnected"] = meld.IsConnected;
                ctx.Args["meldStudio.isRecording"] = meld.IsConnected && meld.IsRecording;
                ctx.Args["meldStudio.isStreaming"] = meld.IsConnected && meld.IsStreaming;
                // Streamer.bot documents this variable with a typo; set it too so imported actions keep working.
                ctx.Args["meldSutdio.isStreaming"] = meld.IsConnected && meld.IsStreaming;
                return Task.CompletedTask;
            }),
            Make("meldstudio.recording", "Recording", [Switch("Start", "Stop")],
                async (ctx, v, ct) => ctx.Args["meldStudio.isRecording"] = await meld.SetRecordingAsync(ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Make("meldstudio.streaming", "Streaming", [Switch("Start", "Stop")],
                async (ctx, v, ct) => ctx.Args["meldStudio.isStreaming"] = await meld.SetStreamingAsync(ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Make("meldstudio.takeScreenshot", "Take screenshot",
                [new("orientation", "Orientation", ParameterKind.Choice) { DefaultValue = "Horizontal", Choices = ["Horizontal", "Vertical"] }],
                (ctx, v, ct) => ScreenshotAsync(meld, ctx, v["orientation"] == "Vertical", ct)),
            Make("meldstudio.takeVerticalScreenshot", "Take vertical screenshot", [], (ctx, _, ct) => ScreenshotAsync(meld, ctx, true, ct)),
            Make("meldstudio.setLayerVisibility", "Set layer visibility state", [OptionalScene(), Layer(), Switch("Visible", "Hidden")],
                async (ctx, v, ct) => ctx.Args["meldStudio.layerVisible"] = await meld.SetLayerVisibilityAsync(v["scene"], Require(v["layer"], "Layer"),
                    ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Make("meldstudio.setEffectState", "Set effect enabled state",
                [
                    OptionalScene(), Layer(),
                    new("effect", "Effect") { Help = "Effect name (or id)", Suggestions = () => meld.Session.Effects.Select(e => e.Name).Distinct() },
                    Switch("Enabled", "Disabled"),
                ],
                async (ctx, v, ct) => ctx.Args["meldStudio.effectEnabled"] = await meld.SetEffectEnabledAsync(v["scene"], Require(v["layer"], "Layer"),
                    Require(v["effect"], "Effect"), ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Make("meldstudio.setLayerTrackMuted", "Set layer track muted state", [OptionalScene(), Layer(), Switch("Muted", "Not Muted")],
                async (ctx, v, ct) => ctx.Args["meldStudio.trackMuted"] = await meld.SetLayerTrackMutedAsync(v["scene"], Require(v["layer"], "Layer"),
                    ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Make("meldstudio.setTrackMuted", "Set track muted state", [Track(), Switch("Muted", "Not Muted")],
                async (ctx, v, ct) => ctx.Args["meldStudio.trackMuted"] = await meld.SetTrackMutedAsync(Require(v["track"], "Track"),
                    ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Make("meldstudio.setBrowserLayerUrl", "Set browser layer URL",
                [OptionalScene(), Layer(), new("url", "URL") { Help = "The page the browser layer shows" }],
                (_, v, ct) => meld.SetBrowserLayerUrlAsync(v["scene"], Require(v["layer"], "Layer"), Require(v["url"], "URL"), ct)),
            Make("meldstudio.setLayerSource", "Set image/media layer source",
                [OptionalScene(), Layer(), new("source", "File") { Help = "Path or file:// URL of the image or video the layer shows" }],
                (_, v, ct) => meld.SetLayerSourceAsync(v["scene"], Require(v["layer"], "Layer"), Require(v["source"], "File"), ct)),
            Make("meldstudio.setTrackMonitoring", "Set track monitoring state", [Track(), Switch("Monitoring", "Not Monitoring")],
                async (ctx, v, ct) => ctx.Args["meldStudio.trackMonitoring"] = await meld.SetTrackMonitoringAsync(Require(v["track"], "Track"),
                    ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Make("meldstudio.setTrackGain", "Set track gain",
                [Track(), new("gain", "Gain (0-1)", ParameterKind.Number) { DefaultValue = "1", Help = "Meld's fader scale, 0.0 to 1.0" }],
                (_, v, ct) => meld.SetTrackGainAsync(Require(v["track"], "Track"), OptionalNumber(v["gain"], "Gain") ?? 1, ct)),
            Make("meldstudio.stageScene", "Stage scene", [Scene()],
                async (_, v, ct) => await meld.StageSceneAsync(Require(v["scene"], "Scene"), ct).ConfigureAwait(false)),
            Make("meldstudio.showStagedScene", "Show staged scene", [], (_, _, ct) => meld.ShowStagedSceneAsync(ct)),
            Make("meldstudio.recordClip", "Record clip",
                [new("seconds", "Length (seconds, 0-90)", ParameterKind.Number) { Help = "Empty or 0 records the maximum available" }],
                (_, v, ct) => meld.RecordClipAsync(OptionalNumber(v["seconds"], "Length"), ct)),
            Make("meldstudio.replay", "Replay",
                [new("mode", "Action", ParameterKind.Choice) { DefaultValue = "Show", Choices = ["Show", "Dismiss"] }],
                (_, v, ct) => meld.SendCommandAsync(v["mode"] == "Dismiss" ? "meld.replay.dismiss" : "meld.replay.show", ct)),
            Make("meldstudio.toggleVirtualCamera", "Toggle virtual camera", [],
                (_, _, ct) => meld.SendCommandAsync("meld.toggleVirtualCameraAction", ct)),
            Make("meldstudio.mediaControl", "Media layer control",
                [
                    OptionalScene(), Layer(),
                    new("mediaAction", "Action", ParameterKind.Choice) { DefaultValue = "Play", Choices = ["Play", "Pause", "Seek"] },
                    new("seconds", "Seek to (seconds)", ParameterKind.Number) { DefaultValue = "0" },
                ],
                (_, v, ct) => meld.MediaAsync(v["scene"], Require(v["layer"], "Layer"), v["mediaAction"], OptionalNumber(v["seconds"], "Seek to") ?? 0, ct)),
            Make("meldstudio.widgetEvent", "Widget event",
                [
                    new("eventType", "Event") { DefaultValue = "CONFETTIPOP_TRIGGER", Suggestions = () => WidgetEvents, Help = "A Meld widget event, e.g. COUNTDOWN_RESET" },
                    new("amount", "Amount (seconds)", ParameterKind.Number) { Help = "Only for SUBATHONTIMER_ADDTIME" },
                ],
                (_, v, ct) => meld.SendStreamEventAsync(Require(v["eventType"], "Event"), OptionalNumber(v["amount"], "Amount"), ct)),
            Make("meldstudio.setProperty", "Set item property",
                [
                    new("item", "Item") { Help = "Scene, layer, effect or track name (or id)", Suggestions = () => meld.Session.Items.Select(i => i.Name).Distinct() },
                    new("property", "Property") { Help = "e.g. name, visible, x, y, width, height, url, source" },
                    new("value", "Value") { Help = "JSON (true, 10, \"text\") or plain text" },
                ],
                (_, v, ct) => meld.SetPropertyAsync(ResolveItem(meld, Require(v["item"], "Item")), Require(v["property"], "Property"), ParseValue(v["value"]), ct)),
            Make("meldstudio.sendCommand", "Send Meld command (advanced)",
                [new("command", "Command") { Help = "e.g. meld.screenshot, meld.recordClip, meld.replay.show", Suggestions = () => Commands }],
                (_, v, ct) => meld.SendCommandAsync(Require(v["command"], "Command"), ct)),
            Make("meldstudio.getScenes", "Get scenes", [],
                (ctx, _, _) =>
                {
                    if (!meld.IsConnected) throw new StreamAppNotConnectedException(meld.Name);
                    ctx.Args["meldStudio.scenes"] = JsonSerializer.Serialize(meld.Session.Scenes.Select(s => s.Name));
                    ctx.Args["meldStudio.sceneCount"] = meld.Session.Scenes.Count;
                    return Task.CompletedTask;
                }),
        ];
    }

    private static readonly string[] Commands =
    [
        "meld.screenshot", "meld.screenshot.vertical", "meld.startStreamingAction", "meld.stopStreamingAction", "meld.toggleStreamingAction",
        "meld.startRecordingAction", "meld.stopRecordingAction", "meld.toggleRecordingAction", "meld.toggleVirtualCameraAction",
        "meld.recordClip", "meld.replay.show", "meld.replay.dismiss",
    ];

    private static async Task ScreenshotAsync(MeldStudioService meld, ActionRunContext ctx, bool vertical, CancellationToken ct)
    {
        try
        {
            await meld.TakeScreenshotAsync(vertical, ct).ConfigureAwait(false);
            ctx.Args["screenshotSuccess"] = true;
        }
        catch (Exception ex) when (ex is StreamAppNotConnectedException or StreamAppRequestException or TimeoutException)
        {
            ctx.Args["screenshotSuccess"] = false;
            ctx.Log.Warn("Meld Studio", $"Screenshot failed: {ex.Message}");
        }
    }

    internal static string ResolveItem(MeldStudioService meld, string nameOrId)
    {
        var session = meld.Session;
        var match = session.Get(nameOrId)
                    ?? session.Items.FirstOrDefault(i => string.Equals(i.Name, nameOrId, StringComparison.OrdinalIgnoreCase));
        return match?.Id ?? throw new StreamAppRequestException($"Meld Studio has no item '{nameOrId}'.");
    }

    /// <summary>JSON when it parses (true, 12, "x", {...}), otherwise the text itself.</summary>
    internal static object? ParseValue(string text) =>
        JsonUtil.TryParse(text.Trim()) is { } json ? json : text;

    private static ISubActionHandler Make(string typeId, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) =>
        new StreamAppSubAction(typeId, Category, name, parameters, run);
}
