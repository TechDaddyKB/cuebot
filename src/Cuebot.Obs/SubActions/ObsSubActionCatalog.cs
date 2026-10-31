using System.Text.Json;
using Cuebot.Core.Actions;
using static Cuebot.Obs.SubActions.ObsSubActionSupport;

namespace Cuebot.Obs.SubActions;

/// <summary>Every OBS sub-action, as a table. Type ids are stored in actions.json and must never change.</summary>
public static class ObsSubActionCatalog
{
    public static IReadOnlyList<ISubActionHandler> Create(ObsService obs)
    {
        var p = new Params(obs);
        return
        [
            // ---- Scenes ----
            Make("obs.setScene", "Set scene", [p.Scene()],
                (c, _, v, ct) => c.SetCurrentSceneAsync(Require(v["scene"], "Scene"), ct)),
            Make("obs.getCurrentScene", "Get current scene", [p.Result("obsScene")], async (c, ctx, v, ct) =>
            {
                var current = await c.GetCurrentSceneAsync(ct).ConfigureAwait(false);
                Store(ctx, v["resultArgument"], current);
                ctx.Args["currentScene"] = current;   // Streamer.bot's name
                ctx.Args["obsPreviewScene"] = c.PreviewScene ?? "";
            }),
            Make("obs.setPreviewScene", "Set preview scene (studio mode)", [p.Scene()],
                (c, _, v, ct) => c.SetPreviewSceneAsync(Require(v["scene"], "Scene"), ct)),

            // ---- Scene items ----
            Make("obs.setSourceVisibility", "Set source visibility", [p.Scene(), p.Source(), p.Choice<VisibilityMode>("state", "State", VisibilityMode.Toggle)],
                (c, _, v, ct) => c.SetSourceVisibilityAsync(Require(v["scene"], "Scene"), Require(v["source"], "Source"),
                    ParseChoice<VisibilityMode>(v["state"], "State"), ct)),
            Make("obs.hideSceneSources", "Hide all scene sources", [p.Scene()],
                (c, _, v, ct) => c.HideAllSourcesAsync(Require(v["scene"], "Scene"), group: false, ct)),
            Make("obs.hideGroupSources", "Hide all group sources", [p.Group()],
                (c, _, v, ct) => c.HideAllSourcesAsync(Require(v["group"], "Group"), group: true, ct)),
            Make("obs.randomSceneSource", "Show random scene source", [p.Scene(), p.Result("obsSourceName")], async (c, ctx, v, ct) =>
                Store(ctx, v["resultArgument"], await c.ShowRandomSourceAsync(Require(v["scene"], "Scene"), group: false, ct).ConfigureAwait(false))),
            Make("obs.randomGroupSource", "Show random group source", [p.Group(), p.Result("obsSourceName")], async (c, ctx, v, ct) =>
                Store(ctx, v["resultArgument"], await c.ShowRandomSourceAsync(Require(v["group"], "Group"), group: true, ct).ConfigureAwait(false))),
            Make("obs.getSceneItemProperties", "Get scene item properties", [p.Scene(), p.Source()], GetSceneItemPropertiesAsync),
            Make("obs.setSourceTransform", "Set source transform",
                [p.Scene(), p.Source(), .. TransformFields.Select(f => new ParameterDescriptor(f, Label(f)) { Help = "Leave empty to keep" })],
                (c, _, v, ct) =>
                {
                    var patch = new Dictionary<string, object>();
                    foreach (var field in TransformFields)
                        if (OptionalNumber(v[field], Label(field)) is { } number) patch[field] = number;
                    return c.SetSceneItemTransformAsync(Require(v["scene"], "Scene"), Require(v["source"], "Source"), patch, ct);
                }),
            Make("obs.flipSource", "Flip source", [p.Scene(), p.Source(), p.Choice<FlipAxis>("axis", "Axis", FlipAxis.Horizontal)],
                (c, _, v, ct) => c.FlipSourceAsync(Require(v["scene"], "Scene"), Require(v["source"], "Source"),
                    ParseChoice<FlipAxis>(v["axis"], "Axis"), ct)),
            Make("obs.rotateSource", "Rotate source",
                [p.Scene(), p.Source(), new("degrees", "Degrees") { DefaultValue = "90" },
                 new("relative", "Add to the current rotation", ParameterKind.Boolean) { DefaultValue = "true" }],
                (c, _, v, ct) => c.RotateSourceAsync(Require(v["scene"], "Scene"), Require(v["source"], "Source"),
                    RequireNumber(v["degrees"], "Degrees"), v.GetBool("relative"), ct)),

            // ---- Inputs ----
            Make("obs.setInputMute", "Set input mute", [p.Input(), p.Choice<MuteMode>("state", "State", MuteMode.Toggle)],
                (c, _, v, ct) => c.SetInputMuteAsync(Require(v["input"], "Input"), ParseChoice<MuteMode>(v["state"], "State"), ct)),
            Make("obs.setInputVolume", "Set input volume",
                [p.Input(), p.Choice<VolumeMode>("mode", "Mode", VolumeMode.SetDb), new("value", "Value") { DefaultValue = "0", Help = "dB (-100 to 26) or multiplier (0 to 20)" }],
                (c, _, v, ct) => c.SetInputVolumeAsync(Require(v["input"], "Input"), ParseChoice<VolumeMode>(v["mode"], "Mode"),
                    RequireNumber(v["value"], "Value"), ct)),
            Make("obs.setAudioTrack", "Set source audio track state",
                [p.Input(), new("track", "Track (1-6)", ParameterKind.Number) { DefaultValue = "1" }, p.Choice<EnableMode>("state", "State", EnableMode.Toggle)],
                (c, _, v, ct) => c.SetAudioTrackAsync(Require(v["input"], "Input"), v.GetInt("track", 1), ParseChoice<EnableMode>(v["state"], "State"), ct)),
            Make("obs.setText", "Set text", [p.Input("Text source"), new("text", "Text", ParameterKind.Multiline)],
                (c, _, v, ct) => c.SetInputTextAsync(Require(v["input"], "Text source"), v["text"], ct)),
            Make("obs.setBrowserUrl", "Set browser source URL", [p.Input("Browser source"), new("url", "URL")],
                (c, _, v, ct) => c.SetBrowserUrlAsync(Require(v["input"], "Browser source"), v["url"], ct)),
            Make("obs.refreshBrowser", "Refresh browser source", [p.Input("Browser source")],
                (c, _, v, ct) => c.RefreshBrowserAsync(Require(v["input"], "Browser source"), ct)),
            Make("obs.setImageFile", "Set image source file", [p.Input("Image source"), new("file", "File") { Help = "Path on the OBS computer" }],
                (c, _, v, ct) => c.SetImageFileAsync(Require(v["input"], "Image source"), v["file"], ct)),
            Make("obs.setMediaFile", "Set media source file", [p.Input("Media source"), new("file", "File") { Help = "Path on the OBS computer" }],
                (c, _, v, ct) => c.SetMediaFileAsync(Require(v["input"], "Media source"), v["file"], ct)),
            Make("obs.setColor", "Set color source color", [p.Input("Color source"), new("color", "Color") { DefaultValue = "#FFFFFF", Help = "#RRGGBB or #AARRGGBB" }],
                (c, _, v, ct) => c.SetColorSourceColorAsync(Require(v["input"], "Color source"), Require(v["color"], "Color"), ct)),
            Make("obs.setInputSettings", "Set input settings (JSON)",
                [p.Input(), new("settings", "Settings (JSON object)", ParameterKind.Multiline), p.Overlay()],
                (c, _, v, ct) => c.SetInputSettingsAsync(Require(v["input"], "Input"),
                    ParseJson(v["settings"], "Settings") ?? throw new ArgumentException("Settings are required."), v.GetBool("overlay", true), ct)),
            Make("obs.getInputSettings", "Get input settings", [p.Input(), p.Result("obsInputSettings")], async (c, ctx, v, ct) =>
            {
                var settings = await c.GetInputSettingsAsync(Require(v["input"], "Input"), ct).ConfigureAwait(false);
                Store(ctx, v["resultArgument"], settings.ValueKind == JsonValueKind.Undefined ? "{}" : settings.GetRawText());
            }),

            // ---- Media ----
            Make("obs.mediaControl", "Play / pause media", [p.Input("Media source"), p.Choice<MediaCommand>("command", "Action", MediaCommand.Play)],
                (c, _, v, ct) => c.MediaControlAsync(Require(v["input"], "Media source"), ParseChoice<MediaCommand>(v["command"], "Action"), ct)),
            Make("obs.mediaSeek", "Seek media", [p.Input("Media source"), p.Choice<SeekMode>("mode", "Mode", SeekMode.Absolute),
                 new("milliseconds", "Milliseconds") { DefaultValue = "0", Help = "Position, or offset (may be negative)" }],
                (c, _, v, ct) => c.SeekMediaAsync(Require(v["input"], "Media source"), ParseChoice<SeekMode>(v["mode"], "Mode"),
                    RequireNumber(v["milliseconds"], "Milliseconds"), ct)),

            // ---- Filters ----
            Make("obs.setFilterState", "Set filter state", [p.Source("Source or scene"), new("filter", "Filter"), p.Choice<EnableMode>("state", "State", EnableMode.Toggle)],
                (c, _, v, ct) => c.SetFilterStateAsync(Require(v["source"], "Source"), Require(v["filter"], "Filter"),
                    ParseChoice<EnableMode>(v["state"], "State"), ct)),
            Make("obs.setRandomFilterState", "Set state of a random filter",
                [p.Source("Source or scene"), p.Choice<EnableMode>("state", "State", EnableMode.Enable), p.Result("obsFilterName")],
                async (c, ctx, v, ct) => Store(ctx, v["resultArgument"], await c.SetRandomFilterStateAsync(Require(v["source"], "Source"),
                    ParseChoice<EnableMode>(v["state"], "State"), ct).ConfigureAwait(false))),
            Make("obs.setAllFiltersState", "Set all filters state",
                [p.Source("Source or scene"), new("state", "State", ParameterKind.Choice) { DefaultValue = "Disable", Choices = ["Enable", "Disable"] }],
                (c, _, v, ct) => c.SetAllFiltersEnabledAsync(Require(v["source"], "Source"),
                    ParseChoice<EnableMode>(v["state"], "State") == EnableMode.Enable, ct)),
            Make("obs.setFilterSettings", "Set filter settings (JSON)",
                [p.Source("Source or scene"), new("filter", "Filter"), new("settings", "Settings (JSON object)", ParameterKind.Multiline), p.Overlay()],
                (c, _, v, ct) => c.SetFilterSettingsAsync(Require(v["source"], "Source"), Require(v["filter"], "Filter"),
                    ParseJson(v["settings"], "Settings") ?? throw new ArgumentException("Settings are required."), v.GetBool("overlay", true), ct)),

            // ---- Transitions and studio mode ----
            Make("obs.setTransition", "Set transition", [new("transition", "Transition") { Suggestions = () => obs.All(c => c.Transitions) }],
                (c, _, v, ct) => c.SetTransitionAsync(Require(v["transition"], "Transition"), ct)),
            Make("obs.setTransitionDuration", "Set transition duration", [new("milliseconds", "Duration (ms)", ParameterKind.Number) { DefaultValue = "300" }],
                (c, _, v, ct) => c.SetTransitionDurationAsync(v.GetInt("milliseconds", 300), ct)),
            Make("obs.studioMode", "Set studio mode", [p.Choice<EnableMode>("state", "State", EnableMode.Toggle)],
                (c, _, v, ct) => c.SetStudioModeAsync(ParseChoice<EnableMode>(v["state"], "State"), ct)),
            Make("obs.studioTransition", "Send preview scene live", [], (c, _, _, ct) => c.TriggerStudioTransitionAsync(ct)),

            // ---- Outputs ----
            Make("obs.streaming", "Streaming", [p.Choice<OutputCommand>("mode", "Mode", OutputCommand.Start)],
                (c, _, v, ct) => c.ControlStreamAsync(ParseChoice<OutputCommand>(v["mode"], "Mode"), ct)),
            Make("obs.recording", "Recording", [p.Choice<RecordCommand>("mode", "Mode", RecordCommand.Start)],
                (c, _, v, ct) => c.ControlRecordAsync(ParseChoice<RecordCommand>(v["mode"], "Mode"), ct)),
            Make("obs.replayBuffer", "Set replay buffer state", [p.Choice<ReplayBufferCommand>("mode", "Mode", ReplayBufferCommand.Save), p.Result("obsReplayPath")],
                async (c, ctx, v, ct) =>
                {
                    if (await c.ControlReplayBufferAsync(ParseChoice<ReplayBufferCommand>(v["mode"], "Mode"), ct).ConfigureAwait(false) is { } path)
                        Store(ctx, v["resultArgument"], path);
                }),
            Make("obs.virtualCam", "Virtual camera", [p.Choice<OutputCommand>("mode", "Mode", OutputCommand.Start)],
                (c, _, v, ct) => c.ControlVirtualCamAsync(ParseChoice<OutputCommand>(v["mode"], "Mode"), ct)),
            Make("obs.createRecordChapter", "Create record chapter", [new("chapterName", "Chapter name") { Help = "Optional; needs OBS 30.2+ and Hybrid MP4" }],
                async (c, ctx, v, ct) =>
                {
                    // Like Streamer.bot, OBS refusing (e.g. not recording, or not Hybrid MP4) sets success=false and
                    // carries on; losing the connection still fails the action.
                    try
                    {
                        await c.CreateRecordChapterAsync(v["chapterName"], ct).ConfigureAwait(false);
                        ctx.Args["success"] = true;
                    }
                    catch (Protocol.ObsRequestException)
                    {
                        ctx.Args["success"] = false;
                    }
                }),
            Make("obs.getOutputState", "Check if stream/recording is running",
                [p.Choice<ObsOutputKind>("output", "Output", ObsOutputKind.Stream), new("outputName", "Output name") { Help = "Only for Other" }],
                async (c, ctx, v, ct) =>
                {
                    var state = await c.GetOutputStatusAsync(ParseChoice<ObsOutputKind>(v["output"], "Output"), v["outputName"], ct).ConfigureAwait(false);
                    StoreObject(ctx, "obs", state);
                    ctx.Args["obsOutputState"] = state.ValueKind == JsonValueKind.Undefined ? "{}" : state.GetRawText();
                }),

            // ---- Misc ----
            Make("obs.screenshot", "Take screenshot",
                [p.Source("Source or scene") with { Help = "Leave empty for the current program scene" },
                 new("file", "File") { Help = "Path on the OBS computer; the extension picks the format (png, jpg, bmp, webp)" },
                 new("width", "Width") { Help = "Optional, 8-4096" }, new("height", "Height") { Help = "Optional, 8-4096" },
                 new("quality", "Quality", ParameterKind.Number) { DefaultValue = "-1", Help = "0-100, -1 for the default" },
                 p.Result("obsScreenshotPath")],
                async (c, ctx, v, ct) => Store(ctx, v["resultArgument"], await c.TakeScreenshotAsync(v["source"], Require(v["file"], "File"),
                    (int?)OptionalNumber(v["width"], "Width"), (int?)OptionalNumber(v["height"], "Height"), v.GetInt("quality", -1), ct).ConfigureAwait(false))),
            Make("obs.setProfile", "Set profile", [new("profile", "Profile") { Suggestions = () => obs.All(c => c.Profiles) }],
                (c, _, v, ct) => c.SetProfileAsync(Require(v["profile"], "Profile"), ct)),
            Make("obs.setSceneCollection", "Set scene collection", [new("sceneCollection", "Scene collection") { Suggestions = () => obs.All(c => c.SceneCollections) }],
                (c, _, v, ct) => c.SetSceneCollectionAsync(Require(v["sceneCollection"], "Scene collection"), ct)),
            Make("obs.triggerHotkey", "Trigger hotkey", [new("hotkey", "Hotkey name") { Help = "e.g. OBSBasic.StartStreaming (see GetHotkeyList)" }],
                (c, _, v, ct) => c.TriggerHotkeyAsync(Require(v["hotkey"], "Hotkey name"), ct)),
            Make("obs.getStats", "Get performance stats", [], async (c, ctx, _, ct) =>
            {
                StoreObject(ctx, "obs", await c.GetStatsAsync(ct).ConfigureAwait(false));
                try
                {
                    var stream = await c.GetOutputStatusAsync(ObsOutputKind.Stream, ct: ct).ConfigureAwait(false);
                    StoreObject(ctx, "obsStream", stream);
                }
                catch (Protocol.ObsRequestException)
                {
                    // Stream statistics are optional.
                }
            }),
            Make("obs.getStatus", "Get status", [], async (c, ctx, _, ct) =>
            {
                foreach (var (key, value) in await c.GetStatusAsync(ct).ConfigureAwait(false)) ctx.Args[key] = value;
            }),
            Make("obs.rawRequest", "Send OBS command (advanced)",
                [new("requestType", "Request type") { Help = "An obs-websocket 5 request type, e.g. GetStats" },
                 new("requestData", "Request data (JSON)", ParameterKind.Multiline) { Help = "A JSON object; leave empty for none" },
                 p.Result("obsResult", "Save the reply in variable")],
                async (c, ctx, v, ct) =>
                {
                    var response = await c.RequestAsync(Require(v["requestType"], "Request type"), RawRequestSubAction.ParseRequestData(v["requestData"]), ct)
                        .ConfigureAwait(false);
                    Store(ctx, v["resultArgument"], response.GetRawText());
                }),
            Make("obs.rawBatch", "Send several OBS commands (advanced)",
                [new("requests", "Requests (JSON array)", ParameterKind.Multiline) { Help = """[{"requestType":"GetVersion"},{"requestType":"SetCurrentProgramScene","requestData":{"sceneName":"Main"}}]""" },
                 new("haltOnFailure", "Stop at the first failure", ParameterKind.Boolean) { DefaultValue = "false" },
                 p.Result("obsResult", "Save the results in variable")],
                async (c, ctx, v, ct) =>
                {
                    var results = await c.RequestBatchAsync(RawRequestSubAction.ParseBatch(v["requests"]), v.GetBool("haltOnFailure"), ct)
                        .ConfigureAwait(false);
                    Store(ctx, v["resultArgument"], results.GetRawText());
                }),
        ];

        ISubActionHandler Make(string typeId, string name, IEnumerable<ParameterDescriptor> parameters,
            Func<ObsConnection, ActionRunContext, ParameterValues, CancellationToken, Task> run) =>
            new ObsSubAction(obs, typeId, name, parameters, run);
    }

    /// <summary>Transform fields "Set source transform" can change, as obs-websocket names them.</summary>
    internal static readonly string[] TransformFields =
        ["positionX", "positionY", "scaleX", "scaleY", "rotation", "cropLeft", "cropTop", "cropRight", "cropBottom", "boundsWidth", "boundsHeight"];

    private static string Label(string field) => string.Concat(field.Select((ch, i) =>
        i == 0 ? char.ToUpperInvariant(ch).ToString() : char.IsUpper(ch) ? " " + char.ToLowerInvariant(ch) : ch.ToString()));

    private static async Task GetSceneItemPropertiesAsync(ObsConnection c, ActionRunContext ctx, ParameterValues v, CancellationToken ct)
    {
        var scene = Require(v["scene"], "Scene");
        var source = Require(v["source"], "Source");
        var id = await c.GetSceneItemIdAsync(scene, source, ct).ConfigureAwait(false);
        var enabled = await c.RequestAsync("GetSceneItemEnabled", new { sceneName = scene, sceneItemId = id }, ct).ConfigureAwait(false);
        var locked = await c.RequestAsync("GetSceneItemLocked", new { sceneName = scene, sceneItemId = id }, ct).ConfigureAwait(false);
        var transform = await c.GetSceneItemTransformAsync(scene, source, ct).ConfigureAwait(false);
        ctx.Args["obsItemId"] = id;
        ctx.Args["obsItemVisible"] = enabled.GetBoolOrNull("sceneItemEnabled") ?? false;
        ctx.Args["obsItemLocked"] = locked.GetBoolOrNull("sceneItemLocked") ?? false;
        StoreObject(ctx, "obsItem", transform);
        ctx.Args["obsItemTransform"] = transform.ValueKind == JsonValueKind.Undefined ? "{}" : transform.GetRawText();

        // Streamer.bot's props.* names.
        var props = ObsSceneItemProps.Build(id, source, enabled.GetBoolOrNull("sceneItemEnabled") ?? false,
            locked.GetBoolOrNull("sceneItemLocked") ?? false, await IsMutedAsync(c, source, ct).ConfigureAwait(false), transform);
        var children = new List<Dictionary<string, object?>>();
        var item = (await c.GetSceneItemsAsync(scene, ct).ConfigureAwait(false)).FirstOrDefault(i => i.Id == id);
        if (item is { IsGroup: true })
        {
            foreach (var child in await c.GetSceneItemsAsync(source, ct, group: true).ConfigureAwait(false))
            {
                var childTransform = await c.RequestAsync("GetSceneItemTransform", new { sceneName = source, sceneItemId = child.Id }, ct)
                    .ConfigureAwait(false);
                children.Add(ObsSceneItemProps.Build(child.Id, child.SourceName, child.Enabled, locked: false,
                    await IsMutedAsync(c, child.SourceName, ct).ConfigureAwait(false),
                    childTransform.TryGetProperty("sceneItemTransform", out var t) ? t : default));
            }
        }
        ObsSceneItemProps.Store(ctx.Args, props, children);
    }

    /// <summary>Whether an input is muted; false for sources that are not audio inputs.</summary>
    private static async Task<bool> IsMutedAsync(ObsConnection c, string source, CancellationToken ct)
    {
        if (!c.Inputs.Contains(source, StringComparer.Ordinal)) return false;
        try { return await c.GetInputMuteAsync(source, ct).ConfigureAwait(false); }
        catch (Protocol.ObsRequestException) { return false; }   // inputs without audio
    }

    /// <summary>Parameter descriptors shared by many sub-actions; suggestions span every connection.</summary>
    private sealed class Params(ObsService obs)
    {
        public ParameterDescriptor Scene() => new("scene", "Scene") { Suggestions = () => obs.All(c => c.Scenes) };

        public ParameterDescriptor Group() => new("group", "Group") { Suggestions = () => obs.All(c => c.Inputs.Concat(c.Scenes)) };

        public ParameterDescriptor Source(string label = "Source") =>
            new("source", label) { Suggestions = () => obs.All(c => c.Inputs.Concat(c.Scenes)) };

        public ParameterDescriptor Input(string label = "Input") => new("input", label) { Suggestions = () => obs.All(c => c.Inputs) };

        public ParameterDescriptor Result(string defaultArgument, string label = "Save the answer in variable") =>
            new("resultArgument", label) { DefaultValue = defaultArgument };

        public ParameterDescriptor Overlay() =>
            new("overlay", "Merge with existing settings", ParameterKind.Boolean) { DefaultValue = "true", Help = "Off replaces every setting" };

        public ParameterDescriptor Choice<TEnum>(string key, string label, TEnum defaultValue) where TEnum : struct, Enum =>
            new(key, label, ParameterKind.Choice) { DefaultValue = defaultValue.ToString(), Choices = ChoicesOf<TEnum>() };
    }
}
