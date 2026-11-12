using System.Text.Json;
using Cuebot.Core.Actions;
using static Cuebot.Obs.SubActions.ObsSubActionSupport;

namespace Cuebot.Obs.SubActions;

/// <summary>
/// The sub-actions for the newer obs-websocket requests (OBS 28-32): recording files, audio, scene item order, lock and
/// blending, creating and removing things, transitions, screenshots, captions, projectors, hotkeys, persistent data,
/// profiles and stream settings, outputs, dialogs and deinterlacing. Type ids are stored in actions.json and must never change.
/// </summary>
public static partial class ObsSubActionCatalog
{
    private static IEnumerable<ISubActionHandler> Features(ObsService obs, Params p)
    {
        ParameterDescriptor Filter() => new("filter", "Filter");
        ParameterDescriptor Transition() => new("transition", "Transition") { Suggestions = () => obs.All(c => c.Transitions) };
        ParameterDescriptor Text(string key, string label, string help = "") => new(key, label) { Help = help };
        ParameterDescriptor Json(string key, string label) => new(key, label, ParameterKind.Multiline) { Help = "A JSON object; leave empty for none" };
        ParameterDescriptor Bool(string key, string label, bool value) => new(key, label, ParameterKind.Boolean) { DefaultValue = value ? "true" : "false" };
        ParameterDescriptor Kind(string label, Func<ObsConnection, IEnumerable<string>> list) =>
            new("kind", label) { Suggestions = () => obs.All(list) };

        return
        [
            // ---- Recording ----
            Make("obs.splitRecording", "Split recording file", [], (c, _, _, ct) => c.SplitRecordFileAsync(ct)),
            Make("obs.getRecordDirectory", "Get recording folder", [p.Result("obsRecordDirectory")], async (c, ctx, v, ct) =>
                Store(ctx, v["resultArgument"], await c.GetRecordDirectoryAsync(ct).ConfigureAwait(false))),
            Make("obs.setRecordDirectory", "Set recording folder", [Text("directory", "Folder", "Full path on the OBS computer")],
                (c, _, v, ct) => c.SetRecordDirectoryAsync(Require(v["directory"], "Folder"), ct)),

            // ---- Audio ----
            Make("obs.setAudioMonitor", "Set audio monitoring", [p.Input(), p.Choice<ObsMonitorType>("monitorType", "Monitoring", ObsMonitorType.MonitorOnly)],
                (c, _, v, ct) => c.SetAudioMonitorTypeAsync(Require(v["input"], "Input"), ParseChoice<ObsMonitorType>(v["monitorType"], "Monitoring"), ct)),
            Make("obs.setAudioBalance", "Set audio balance", [p.Input(), new("balance", "Balance") { DefaultValue = "0.5", Help = "0 left, 0.5 center, 1 right" }],
                (c, _, v, ct) => c.SetAudioBalanceAsync(Require(v["input"], "Input"), RequireNumber(v["balance"], "Balance"), ct)),
            Make("obs.setAudioSyncOffset", "Set audio sync offset", [p.Input(), new("milliseconds", "Offset (ms)", ParameterKind.Number) { DefaultValue = "0" }],
                (c, _, v, ct) => c.SetAudioSyncOffsetAsync(Require(v["input"], "Input"), v.GetInt("milliseconds"), ct)),
            Make("obs.getAudioSettings", "Get audio settings", [p.Input()], async (c, ctx, v, ct) =>
            {
                foreach (var (key, value) in await c.GetAudioSettingsAsync(Require(v["input"], "Input"), ct).ConfigureAwait(false)) ctx.Args[key] = value;
            }),

            // ---- Scene items ----
            Make("obs.setSourceOrder", "Set source order", [p.Scene(), p.Source(), p.Choice<ObsItemOrder>("order", "Move to", ObsItemOrder.Top),
                    new("position", "Position", ParameterKind.Number) { DefaultValue = "0", Help = "Only for Position: 0 is the bottom" }],
                async (c, ctx, v, ct) => ctx.Args["obsSourceIndex"] = await c.SetSceneItemOrderAsync(Require(v["scene"], "Scene"), Require(v["source"], "Source"),
                    ParseChoice<ObsItemOrder>(v["order"], "Move to"), v.GetInt("position"), ct).ConfigureAwait(false)),
            Make("obs.setSourceLocked", "Lock source", [p.Scene(), p.Source(), new("state", "State", ParameterKind.Choice)
                    { DefaultValue = "Lock", Choices = ["Lock", "Unlock", "Toggle"] }],
                (c, _, v, ct) => c.SetSceneItemLockedAsync(Require(v["scene"], "Scene"), Require(v["source"], "Source"),
                    v["state"] switch { "Lock" => EnableMode.Enable, "Unlock" => EnableMode.Disable, _ => EnableMode.Toggle }, ct)),
            Make("obs.setBlendMode", "Set source blend mode", [p.Scene(), p.Source(), p.Choice<ObsBlendMode>("blendMode", "Blend mode", ObsBlendMode.Normal)],
                (c, _, v, ct) => c.SetSceneItemBlendModeAsync(Require(v["scene"], "Scene"), Require(v["source"], "Source"),
                    ParseChoice<ObsBlendMode>(v["blendMode"], "Blend mode"), ct)),
            Make("obs.createInput", "Create source",
                [p.Scene(), Text("name", "New source name"), Kind("Kind", c => c.InputKinds) with { Help = "e.g. browser_source, image_source, text_gdiplus_v3, color_source_v3" },
                 Json("settings", "Settings (JSON)"), Bool("visible", "Visible", true), p.Result("obsSceneItemId")],
                async (c, ctx, v, ct) => Store(ctx, v["resultArgument"], await c.CreateInputAsync(Require(v["scene"], "Scene"), Require(v["name"], "New source name"),
                    Require(v["kind"], "Kind"), ParseJson(v["settings"], "Settings"), v.GetBool("visible", true), ct).ConfigureAwait(false))),
            Make("obs.removeInput", "Delete source", [p.Input()], (c, _, v, ct) => c.RemoveInputAsync(Require(v["input"], "Input"), ct)),
            Make("obs.renameInput", "Rename source", [p.Input(), Text("newName", "New name")],
                (c, _, v, ct) => c.RenameInputAsync(Require(v["input"], "Input"), Require(v["newName"], "New name"), ct)),
            Make("obs.addSceneItem", "Add source to scene", [p.Scene(), p.Source(), Bool("visible", "Visible", true), p.Result("obsSceneItemId")],
                async (c, ctx, v, ct) => Store(ctx, v["resultArgument"], await c.AddSceneItemAsync(Require(v["scene"], "Scene"), Require(v["source"], "Source"),
                    v.GetBool("visible", true), ct).ConfigureAwait(false))),
            Make("obs.removeSceneItem", "Remove source from scene", [p.Scene(), p.Source()],
                (c, _, v, ct) => c.RemoveSceneItemAsync(Require(v["scene"], "Scene"), Require(v["source"], "Source"), ct)),
            Make("obs.duplicateSceneItem", "Duplicate source",
                [p.Scene(), p.Source(), new("destinationScene", "Into scene") { Suggestions = () => obs.All(c => c.Scenes), Help = "Leave empty for the same scene" },
                 p.Result("obsSceneItemId")],
                async (c, ctx, v, ct) => Store(ctx, v["resultArgument"], await c.DuplicateSceneItemAsync(Require(v["scene"], "Scene"), Require(v["source"], "Source"),
                    v["destinationScene"], ct).ConfigureAwait(false))),
            Make("obs.getSourceActive", "Is source live", [p.Source("Source or scene")], async (c, ctx, v, ct) =>
            {
                var (active, showing) = await c.GetSourceActiveAsync(Require(v["source"], "Source"), ct).ConfigureAwait(false);
                ctx.Args["obsSourceActive"] = active;
                ctx.Args["obsSourceShowing"] = showing;
            }),

            // ---- Scenes ----
            Make("obs.createScene", "Create scene", [Text("name", "Scene name")], (c, _, v, ct) => c.CreateSceneAsync(Require(v["name"], "Scene name"), ct)),
            Make("obs.removeScene", "Delete scene", [p.Scene()], (c, _, v, ct) => c.RemoveSceneAsync(Require(v["scene"], "Scene"), ct)),
            Make("obs.renameScene", "Rename scene", [p.Scene(), Text("newName", "New name")],
                (c, _, v, ct) => c.RenameSceneAsync(Require(v["scene"], "Scene"), Require(v["newName"], "New name"), ct)),
            Make("obs.setSceneTransitionOverride", "Set scene transition override",
                [p.Scene(), Transition() with { Help = "Leave empty to remove the override" }, new("milliseconds", "Duration (ms)") { Help = "50-20000; leave empty to remove" }],
                (c, _, v, ct) => c.SetSceneTransitionOverrideAsync(Require(v["scene"], "Scene"), v["transition"],
                    OptionalNumber(v["milliseconds"], "Duration") is { } ms ? (int)ms : null, ct)),

            // ---- Filters ----
            Make("obs.createFilter", "Add filter",
                [p.Source("Source or scene"), Filter() with { Label = "New filter name" }, Kind("Filter kind", c => c.FilterKinds) with { Help = "e.g. color_filter_v2, mask_filter_v2, scroll_filter" },
                 Json("settings", "Settings (JSON)")],
                (c, _, v, ct) => c.CreateFilterAsync(Require(v["source"], "Source"), Require(v["filter"], "Filter name"), Require(v["kind"], "Filter kind"),
                    ParseJson(v["settings"], "Settings"), ct)),
            Make("obs.removeFilter", "Remove filter", [p.Source("Source or scene"), Filter()],
                (c, _, v, ct) => c.RemoveFilterAsync(Require(v["source"], "Source"), Require(v["filter"], "Filter"), ct)),
            Make("obs.renameFilter", "Rename filter", [p.Source("Source or scene"), Filter(), Text("newName", "New name")],
                (c, _, v, ct) => c.RenameFilterAsync(Require(v["source"], "Source"), Require(v["filter"], "Filter"), Require(v["newName"], "New name"), ct)),
            Make("obs.setFilterOrder", "Set filter order", [p.Source("Source or scene"), Filter(), new("position", "Position", ParameterKind.Number) { DefaultValue = "0", Help = "0 is the first (top) filter" }],
                (c, _, v, ct) => c.SetFilterIndexAsync(Require(v["source"], "Source"), Require(v["filter"], "Filter"), v.GetInt("position"), ct)),

            // ---- Transitions ----
            Make("obs.setTransitionSettings", "Set transition settings (JSON)", [new("settings", "Settings (JSON object)", ParameterKind.Multiline), p.Overlay()],
                (c, _, v, ct) => c.SetTransitionSettingsAsync(ParseJson(v["settings"], "Settings") ?? throw new ArgumentException("Settings are required."),
                    v.GetBool("overlay", true), ct)),
            Make("obs.getCurrentTransition", "Get current transition", [], async (c, ctx, _, ct) =>
            {
                StoreObject(ctx, "obs", await c.GetCurrentTransitionAsync(ct).ConfigureAwait(false));
                try
                {
                    var cursor = await c.RequestAsync("GetCurrentSceneTransitionCursor", ct: ct).ConfigureAwait(false);
                    ctx.Args["obsTransitionCursor"] = cursor.GetDoubleOrNull("transitionCursor");
                }
                catch (Protocol.ObsRequestException)
                {
                    ctx.Args["obsTransitionCursor"] = null;   // e.g. a transition without a cursor
                }
            }),
            Make("obs.setTBar", "Move T-bar (studio mode)", [new("position", "Position") { DefaultValue = "1", Help = "0 to 1" }, Bool("release", "Release", true)],
                (c, _, v, ct) => c.SetTBarPositionAsync(RequireNumber(v["position"], "Position"), v.GetBool("release", true), ct)),

            // ---- Screenshots, captions, projectors, dialogs ----
            Make("obs.screenshotToVariable", "Take screenshot into variable",
                [p.Source("Source or scene") with { Help = "Leave empty for the current program scene" },
                 new("format", "Format", ParameterKind.Choice) { DefaultValue = "png", Choices = ["png", "jpg", "webp", "bmp"] },
                 new("width", "Width") { Help = "Optional, 8-4096" }, new("height", "Height") { Help = "Optional, 8-4096" },
                 new("quality", "Quality", ParameterKind.Number) { DefaultValue = "-1", Help = "0-100, -1 for the default" },
                 p.Result("obsScreenshot")],
                async (c, ctx, v, ct) => Store(ctx, v["resultArgument"], await c.GetScreenshotAsync(v["source"], v["format"],
                    (int?)OptionalNumber(v["width"], "Width"), (int?)OptionalNumber(v["height"], "Height"), v.GetInt("quality", -1), ct).ConfigureAwait(false))),
            Make("obs.sendCaption", "Send stream caption", [new("text", "Caption text", ParameterKind.Multiline)],
                (c, _, v, ct) => c.SendStreamCaptionAsync(Require(v["text"], "Caption text"), ct)),
            Make("obs.openProjector", "Open projector",
                [p.Choice<ObsProjector>("projector", "Projector", ObsProjector.Program), p.Source() with { Help = "Only for Source" },
                 new("monitor", "Monitor", ParameterKind.Number) { DefaultValue = "-1", Help = "0 is the first monitor; -1 opens a window" }],
                (c, _, v, ct) => c.OpenProjectorAsync(ParseChoice<ObsProjector>(v["projector"], "Projector"), v["source"], v.GetInt("monitor", -1), ct)),
            Make("obs.openSourceDialog", "Open source window", [p.Input(), p.Choice<ObsInputDialog>("dialog", "Window", ObsInputDialog.Properties)],
                (c, _, v, ct) => c.OpenInputDialogAsync(Require(v["input"], "Input"), ParseChoice<ObsInputDialog>(v["dialog"], "Window"), ct)),

            // ---- Hotkeys ----
            Make("obs.triggerHotkeyKeys", "Trigger hotkey by keys",
                [Text("key", "Key", "An OBS key id like OBS_KEY_F13 or OBS_KEY_NUM1"), Bool("shift", "Shift", false), Bool("control", "Ctrl", false),
                 Bool("alt", "Alt", false), Bool("command", "Cmd (Mac)", false)],
                (c, _, v, ct) => c.TriggerHotkeyByKeysAsync(Require(v["key"], "Key"), v.GetBool("shift"), v.GetBool("control"), v.GetBool("alt"),
                    v.GetBool("command"), ct)),

            // ---- Persistent data ----
            Make("obs.setPersistentData", "Save value in OBS",
                [p.Choice<ObsDataRealm>("realm", "Where", ObsDataRealm.Global), Text("slot", "Name"), Text("value", "Value", "Text, a number, or JSON")],
                (c, _, v, ct) => c.SetPersistentDataAsync(ParseChoice<ObsDataRealm>(v["realm"], "Where"), Require(v["slot"], "Name"), AsJsonValue(v["value"]), ct)),
            Make("obs.getPersistentData", "Get value from OBS", [p.Choice<ObsDataRealm>("realm", "Where", ObsDataRealm.Global), Text("slot", "Name"), p.Result("obsValue")],
                async (c, ctx, v, ct) =>
                {
                    var value = await c.GetPersistentDataAsync(ParseChoice<ObsDataRealm>(v["realm"], "Where"), Require(v["slot"], "Name"), ct).ConfigureAwait(false);
                    Store(ctx, v["resultArgument"], value.ToArgValue());
                }),

            // ---- Profiles, scene collections, video and stream settings ----
            Make("obs.createProfile", "Create profile", [Text("profile", "Profile name")], (c, _, v, ct) => c.CreateProfileAsync(Require(v["profile"], "Profile name"), ct)),
            Make("obs.removeProfile", "Delete profile", [new("profile", "Profile") { Suggestions = () => obs.All(c => c.Profiles) }],
                (c, _, v, ct) => c.RemoveProfileAsync(Require(v["profile"], "Profile"), ct)),
            Make("obs.createSceneCollection", "Create scene collection", [Text("sceneCollection", "Scene collection name")],
                (c, _, v, ct) => c.CreateSceneCollectionAsync(Require(v["sceneCollection"], "Scene collection name"), ct)),
            Make("obs.getProfileParameter", "Get profile setting", [Text("category", "Category", "e.g. Output"), Text("name", "Name", "e.g. Mode"), p.Result("obsProfileValue")],
                async (c, ctx, v, ct) => Store(ctx, v["resultArgument"], await c.GetProfileParameterAsync(Require(v["category"], "Category"), Require(v["name"], "Name"), ct)
                    .ConfigureAwait(false) ?? "")),
            Make("obs.setProfileParameter", "Set profile setting", [Text("category", "Category", "e.g. Output"), Text("name", "Name", "e.g. Mode"),
                    Text("value", "Value", "Leave empty to delete the setting")],
                (c, _, v, ct) => c.SetProfileParameterAsync(Require(v["category"], "Category"), Require(v["name"], "Name"),
                    string.IsNullOrEmpty(v["value"]) ? null : v["value"], ct)),
            Make("obs.getVideoSettings", "Get video settings", [], async (c, ctx, _, ct) =>
                StoreObject(ctx, "obsVideo", await c.GetVideoSettingsAsync(ct).ConfigureAwait(false))),
            Make("obs.setVideoSettings", "Set video settings",
                [Text("baseWidth", "Canvas width"), Text("baseHeight", "Canvas height"), Text("outputWidth", "Output width"), Text("outputHeight", "Output height"),
                 Text("fpsNumerator", "FPS"), Text("fpsDenominator", "FPS divider", "Usually 1; leave empty for 1")],
                (c, _, v, ct) => c.SetVideoSettingsAsync(Int(v["baseWidth"], "Canvas width"), Int(v["baseHeight"], "Canvas height"),
                    Int(v["outputWidth"], "Output width"), Int(v["outputHeight"], "Output height"), Int(v["fpsNumerator"], "FPS"),
                    Int(v["fpsDenominator"], "FPS divider"), ct)),
            Make("obs.getStreamService", "Get stream destination", [], async (c, ctx, _, ct) =>
            {
                var (type, settings) = await c.GetStreamServiceAsync(ct).ConfigureAwait(false);
                ctx.Args["obsStreamServiceType"] = type;
                ctx.Args["obsStreamService"] = settings.ValueKind == JsonValueKind.Undefined ? "{}" : settings.GetRawText();
                ctx.Args["obsStreamServer"] = settings.GetStringOrNull("server") ?? "";
                ctx.Args["obsStreamServiceName"] = settings.GetStringOrNull("service") ?? "";
            }),
            Make("obs.setStreamService", "Set stream destination",
                [new("serviceType", "Service type", ParameterKind.Choice) { DefaultValue = "rtmp_custom", Choices = ["rtmp_custom", "rtmp_common"] },
                 new("settings", "Settings (JSON object)", ParameterKind.Multiline) { Help = """e.g. {"server":"rtmp://...","key":"..."}""" }],
                (c, _, v, ct) => c.SetStreamServiceAsync(Require(v["serviceType"], "Service type"),
                    ParseJson(v["settings"], "Settings") ?? throw new ArgumentException("Settings are required."), ct)),

            // ---- Outputs ----
            Make("obs.output", "Start / stop output",
                [new("output", "Output") { Suggestions = () => obs.All(c => c.Outputs), Help = "An output name, e.g. from a plugin" }, p.Choice<OutputCommand>("mode", "Mode", OutputCommand.Start)],
                (c, _, v, ct) => c.ControlOutputAsync(Require(v["output"], "Output"), ParseChoice<OutputCommand>(v["mode"], "Mode"), ct)),
            Make("obs.getOutputSettings", "Get output settings",
                [new("output", "Output") { Suggestions = () => obs.All(c => c.Outputs) }, p.Result("obsOutputSettings")],
                async (c, ctx, v, ct) =>
                {
                    var settings = await c.GetOutputSettingsAsync(Require(v["output"], "Output"), ct).ConfigureAwait(false);
                    Store(ctx, v["resultArgument"], settings.ValueKind == JsonValueKind.Undefined ? "{}" : settings.GetRawText());
                }),
            Make("obs.setOutputSettings", "Set output settings (JSON)",
                [new("output", "Output") { Suggestions = () => obs.All(c => c.Outputs) }, new("settings", "Settings (JSON object)", ParameterKind.Multiline)],
                (c, _, v, ct) => c.SetOutputSettingsAsync(Require(v["output"], "Output"),
                    ParseJson(v["settings"], "Settings") ?? throw new ArgumentException("Settings are required."), ct)),

            // ---- Inputs: special inputs, list items, deinterlacing ----
            Make("obs.getSpecialInputs", "Get desktop and mic sources", [], async (c, ctx, _, ct) =>
            {
                foreach (var (key, name) in await c.GetSpecialInputsAsync(ct).ConfigureAwait(false))
                    ctx.Args["obs" + char.ToUpperInvariant(key[0]) + key[1..]] = name;
            }),
            Make("obs.getInputListItems", "Get source list options", [p.Input(), Text("property", "Setting name", "e.g. monitor_id or window"), p.Result("obsListItems")],
                async (c, ctx, v, ct) =>
                {
                    var items = await c.GetInputListItemsAsync(Require(v["input"], "Input"), Require(v["property"], "Setting name"), ct).ConfigureAwait(false);
                    Store(ctx, v["resultArgument"], items.ValueKind == JsonValueKind.Undefined ? "[]" : items.GetRawText());
                    ctx.Args["obsListItemCount"] = items.GetArrayItems().Count();
                }),
            Make("obs.setDeinterlace", "Set deinterlacing",
                [p.Input(), new("mode", "Mode", ParameterKind.Choice) { DefaultValue = Keep, Choices = [Keep, .. ChoicesOf<ObsDeinterlaceMode>()] },
                 new("fieldOrder", "Field order", ParameterKind.Choice) { DefaultValue = Keep, Choices = [Keep, .. ChoicesOf<ObsFieldOrder>()] }],
                (c, _, v, ct) => c.SetDeinterlaceAsync(Require(v["input"], "Input"),
                    IsKeep(v["mode"]) ? null : ParseChoice<ObsDeinterlaceMode>(v["mode"], "Mode"),
                    IsKeep(v["fieldOrder"]) ? null : ParseChoice<ObsFieldOrder>(v["fieldOrder"], "Field order"), ct)),
        ];

        ISubActionHandler Make(string typeId, string name, IEnumerable<ParameterDescriptor> parameters,
            Func<ObsConnection, ActionRunContext, ParameterValues, CancellationToken, Task> run) =>
            new ObsSubAction(obs, typeId, name, WithCanvas(typeId, parameters, p), run);
    }

    private const string Keep = "Keep";

    private static bool IsKeep(string value) => string.IsNullOrWhiteSpace(value) || value.Equals(Keep, StringComparison.OrdinalIgnoreCase);

    private static int? Int(string value, string label) => OptionalNumber(value, label) is { } number ? (int)number : null;

    /// <summary>Text as a JSON value: valid JSON (numbers, true, objects...) as is, anything else as a string.</summary>
    internal static JsonElement AsJsonValue(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Plain text.
            }
        }
        return JsonSerializer.SerializeToElement(text);
    }
}
