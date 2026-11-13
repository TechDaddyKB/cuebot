using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Integrations.Common;
using static Cuebot.Integrations.Common.Steps;

namespace Cuebot.Integrations.VTubeStudio;

/// <summary>The sub-actions added for visual effects, throwing, pinning, custom parameters, physics and VTube Studio's other requests.</summary>
internal static partial class VTubeStudioSubActions
{
    private static readonly string[] PinPlaces = ["None", "Random", "ArtMeshCenter", "ArtMeshRandom", "LastClick"];

    internal static ParameterDescriptor PinPlace(string key = "pin", string defaultValue = "None") => new(key, "Pin to the model", ParameterKind.Choice)
    {
        DefaultValue = defaultValue,
        Choices = PinPlaces,
        ChoiceLabels = new Dictionary<string, string>
        {
            ["None"] = "Don't pin",
            ["Random"] = "Somewhere random on the model",
            ["ArtMeshCenter"] = "The middle of an ArtMesh",
            ["ArtMeshRandom"] = "Somewhere random on an ArtMesh",
            ["LastClick"] = "Where the model was last clicked",
        },
    };

    internal static VtsPinPlace ParsePinPlace(string value) =>
        Enum.TryParse<VtsPinPlace>(value.Trim(), ignoreCase: true, out var place) ? place : VtsPinPlace.None;

    private static IReadOnlyList<ISubActionHandler> Extra(VTubeStudioService vts)
    {
        ParameterDescriptor Item() => new("item", "Item (instance id or file name)")
        {
            Suggestions = () => vts.Items.Select(i => i.InstanceId).Concat(vts.Items.Select(i => i.FileName)).Distinct(),
        };
        ParameterDescriptor Preset() => new("preset", "Preset") { Suggestions = () => vts.VfxPresets, Help = "A visual effects preset you saved in VTube Studio" };
        ParameterDescriptor Fade(string defaultValue = "0.5") => new("fadeTime", "Fade time (seconds, 0-2)", ParameterKind.Number) { DefaultValue = defaultValue };
        ParameterDescriptor Parameter() => new("parameter", "Parameter") { Suggestions = () => vts.ParameterNames };

        return
        [
            // ---- Models ----
            Make("vtubestudio.changeModel", "Change model (next, random, unload)",
                [
                    new("change", "Change", ParameterKind.Choice)
                    {
                        DefaultValue = "Next", Choices = Enum.GetNames<VtsModelChange>(),
                        ChoiceLabels = new Dictionary<string, string>
                        {
                            ["Next"] = "Next model (alphabetically)", ["Previous"] = "Previous model", ["Random"] = "A random other model", ["Unload"] = "Unload the model",
                        },
                    },
                    RevertAfter("the previous model"),
                ],
                async (ctx, v, ct) => ctx.Args["vtsModelName"] = await vts.ChangeModelAsync(Enum.Parse<VtsModelChange>(v["change"], ignoreCase: true),
                    Seconds(v["revertAfter"], "Undo after"), ct).ConfigureAwait(false)),

            // ---- Visual effects ----
            Make("vtubestudio.loadVfxPreset", "Load visual effects preset", [Preset(), Fade(), RevertAfter("the previous effects")],
                async (ctx, v, ct) => StoreVfx(ctx, await vts.LoadVfxPresetAsync(v["preset"], Number(v["fadeTime"], "Fade time", 0.5),
                    Seconds(v["revertAfter"], "Undo after"), ct).ConfigureAwait(false))),
            Make("vtubestudio.setVfx", "Turn visual effects on or off", [Switch("state", "State", SwitchMode.Toggle), RevertAfter("the effects")],
                async (ctx, v, ct) => ctx.Args["vtsVfxOn"] = await vts.SetVfxOnAsync(ParseSwitch(v["state"]), ct, Seconds(v["revertAfter"], "Undo after"))
                    .ConfigureAwait(false)),
            Make("vtubestudio.setVfxValues", "Set visual effect settings",
                [
                    new("values", "Settings", ParameterKind.Multiline)
                    {
                        Help = "setting=value pairs, e.g. Bloom_Strength=0.8, ColorGrading_Strength=1 (colors as RRGGBBAA)",
                        Suggestions = () => vts.VfxConfigIds,
                    },
                    Fade(),
                    new("othersToDefault", "Turn every other effect off", ParameterKind.Boolean) { DefaultValue = "false" },
                    new("restricted", "Uses restricted effects", ParameterKind.Boolean) { DefaultValue = "false" },
                    RevertAfter("the previous effects"),
                ],
                async (ctx, v, ct) => StoreVfx(ctx, await vts.SetVfxValuesAsync(v["values"], Number(v["fadeTime"], "Fade time", 0.5),
                    v.GetBool("othersToDefault"), v.GetBool("restricted"), Seconds(v["revertAfter"], "Undo after"), ct).ConfigureAwait(false))),
            Make("vtubestudio.randomizeVfx", "Randomize visual effects",
                [new("chaos", "Chaos (0-1)", ParameterKind.Number) { DefaultValue = "0.5" }, Fade(), RevertAfter("the previous effects")],
                async (ctx, v, ct) => StoreVfx(ctx, await vts.RandomizeVfxAsync(Number(v["chaos"], "Chaos", 0.5), Number(v["fadeTime"], "Fade time", 0.5),
                    Seconds(v["revertAfter"], "Undo after"), ct).ConfigureAwait(false))),
            Make("vtubestudio.getVfx", "Get visual effects state",
                [new("refreshPresets", "Re-read the preset list", ParameterKind.Boolean) { DefaultValue = "false" }],
                async (ctx, v, ct) =>
                {
                    StoreVfx(ctx, await vts.GetVfxStateAsync(v.GetBool("refreshPresets"), ct).ConfigureAwait(false));
                    ctx.Args["vtsVfxPresets"] = string.Join(", ", vts.VfxPresets);
                }),

            // ---- Throwing ----
            Make("vtubestudio.throwItems", "Throw items at the model",
                [
                    new("files", "Item files") { Help = "Comma-separated; one is picked at random for each throw", Suggestions = () => vts.ItemFiles.Select(f => f.FileName) },
                    new("images", "Or images") { Help = "Picture URLs, files or base64 (e.g. %userProfileImageUrl%), comma-separated; used instead of the item files" },
                    new("count", "How many", ParameterKind.Number) { DefaultValue = "1" },
                    new("size", "Size (0-1)", ParameterKind.Number) { DefaultValue = "0.15" },
                    new("interval", "Seconds between throws", ParameterKind.Number) { DefaultValue = "0.15" },
                    new("flightTime", "Flight time (seconds)", ParameterKind.Number) { DefaultValue = "0.35" },
                    new("flinch", "Flinch (0-1)", ParameterKind.Number) { DefaultValue = "0.5", Help = "How hard the model reacts; 0 for no reaction" },
                    new("from", "From", ParameterKind.Choice)
                    {
                        DefaultValue = "Random", Choices = VtsThrowDirection.All, ChoiceLabels = VtsThrowDirection.Labels,
                    },
                    new("askFirst", "Ask in VTube Studio before showing a new image", ParameterKind.Boolean) { DefaultValue = "false" },
                ],
                async (ctx, v, ct) => ctx.Args["vtsThrown"] = await vts.ThrowAsync(new VtsThrow
                {
                    Files = VtsProtocol.SplitList(v["files"]),
                    Images = SplitImages(v["images"]),
                    Count = (int)Number(v["count"], "How many", 1),
                    Size = Number(v["size"], "Size", 0.15),
                    Interval = Number(v["interval"], "Seconds between throws", 0.15),
                    FlightTime = Number(v["flightTime"], "Flight time", 0.35),
                    Flinch = Number(v["flinch"], "Flinch", 0.5),
                    From = v["from"],
                    AskFirst = v.GetBool("askFirst"),
                }, ct).ConfigureAwait(false)),

            // ---- Items ----
            Make("vtubestudio.pinItem", "Pin item to the model",
                [
                    Item(),
                    PinPlace("place", "Random") with { Label = "Where" },
                    new("artMesh", "ArtMesh") { Help = "For the ArtMesh places", Suggestions = () => vts.ArtMeshes.Names },
                    new("angle", "Angle (degrees)", ParameterKind.Number) { Help = "Relative to the model; empty keeps the item's rotation" },
                    new("size", "Size (0-1)", ParameterKind.Number) { Help = "Empty keeps the item's size" },
                ],
                async (ctx, v, ct) =>
                {
                    var place = ParsePinPlace(v["place"]);
                    if (place == VtsPinPlace.None) place = VtsPinPlace.Random;
                    ctx.Args["vtsPinnedItems"] = await vts.PinItemAsync(v["item"], place, v["artMesh"], OptionalNumber(v["angle"], "Angle"),
                        OptionalNumber(v["size"], "Size"), ct).ConfigureAwait(false);
                }),
            Make("vtubestudio.unpinItem", "Unpin item", [Item()],
                async (ctx, v, ct) => ctx.Args["vtsUnpinnedItems"] = await vts.UnpinItemAsync(v["item"], ct).ConfigureAwait(false)),
            Make("vtubestudio.getItems", "Get items in the scene",
                [new("includeFiles", "Also re-read the item files", ParameterKind.Boolean) { DefaultValue = "false", Help = "Makes VTube Studio read its Items folder (it may stutter)" }],
                async (ctx, v, ct) =>
                {
                    var items = await vts.GetItemsAsync(v.GetBool("includeFiles"), ct).ConfigureAwait(false);
                    ctx.Args["vtsItemCount"] = items.Count;
                    ctx.Args["vtsItems"] = string.Join(", ", items.Select(i => i.FileName));
                    ctx.Args["vtsItemsJson"] = JsonUtil.Serialize(items.Select(i => new
                    {
                        instanceId = i.InstanceId, fileName = i.FileName, type = i.Type, order = i.Order, pinned = i.PinnedToModel,
                        pinnedArtMesh = i.PinnedArtMeshId, flipped = i.Flipped, locked = i.Locked, censored = i.Censored,
                        frameCount = i.FrameCount, currentFrame = i.CurrentFrame, framerate = i.Framerate,
                    }));
                    for (var i = 0; i < items.Count; i++)
                    {
                        ctx.Args[$"vtsItem{i}.instanceId"] = items[i].InstanceId;
                        ctx.Args[$"vtsItem{i}.fileName"] = items[i].FileName;
                        ctx.Args[$"vtsItem{i}.pinned"] = items[i].PinnedToModel;
                    }
                    if (v.GetBool("includeFiles")) ctx.Args["vtsItemFiles"] = string.Join(", ", vts.ItemFiles.Select(f => f.FileName));
                }),
            Make("vtubestudio.itemAnimation", "Item look and animation",
                [
                    Item(),
                    new("opacity", "Opacity (0-1)", ParameterKind.Number) { Help = "Empty keeps it" },
                    new("brightness", "Brightness (0-1)", ParameterKind.Number) { Help = "Empty keeps it; lower is darker" },
                    new("framerate", "Frames per second", ParameterKind.Number) { Help = "Animated items only; empty keeps it" },
                    new("frame", "Jump to frame", ParameterKind.Number) { Help = "Animated items only (0 is the first); empty keeps it" },
                    new("play", "Animation", ParameterKind.Choice) { DefaultValue = "Unchanged", Choices = ["Unchanged", "Play", "Stop"] },
                    new("autoStopFrames", "Stop on frames") { Help = "Comma-separated frame numbers the animation stops on; \"none\" clears them; empty keeps them" },
                ],
                async (ctx, v, ct) =>
                {
                    var stops = v["autoStopFrames"].Trim() switch
                    {
                        "" => null,
                        var t when t.Equals("none", StringComparison.OrdinalIgnoreCase) => [],
                        var t => VtsProtocol.SplitList(t).Select(f => int.Parse(f, CultureInfo.InvariantCulture)).ToArray(),
                    };
                    var (frame, playing) = await vts.ItemAnimationAsync(v["item"], OptionalNumber(v["framerate"], "Frames per second"),
                        (int?)OptionalNumber(v["frame"], "Frame"), OptionalNumber(v["brightness"], "Brightness"), OptionalNumber(v["opacity"], "Opacity"),
                        v["play"] switch { "Play" => true, "Stop" => false, _ => null }, stops, ct).ConfigureAwait(false);
                    ctx.Args["vtsItemFrame"] = frame;
                    ctx.Args["vtsItemPlaying"] = playing;
                }),
            Make("vtubestudio.sortItem", "Put item between model layers",
                [
                    Item(),
                    new("front", "In front of")
                    {
                        Help = "An ArtMesh id, FullyInFront or FullyInBack; empty takes the item out of the model",
                        Suggestions = () => new[] { "FullyInFront", "FullyInBack" }.Concat(vts.ArtMeshes.Names),
                    },
                    new("back", "Back part in front of") { Help = "Live2D items only: an ArtMesh id or FullyInBack; empty leaves it", Suggestions = () => vts.ArtMeshes.Names },
                    new("splitAt", "Split the Live2D item at") { Help = "Live2D items only: an ArtMesh id of the item; empty leaves it" },
                ],
                (_, v, ct) => vts.SortItemAsync(v["item"], v["front"], v["back"], v["splitAt"], ct)),

            // ---- Color tints and ArtMeshes ----
            Make("vtubestudio.rainbowTint", "Rainbow color tint",
                [
                    new("seconds", "For (seconds)", ParameterKind.Number) { DefaultValue = "10" },
                    new("cycleSeconds", "One rainbow every (seconds)", ParameterKind.Number) { DefaultValue = "2" },
                    .. TintFilters(vts),
                    new("mix", "Mix with scene lighting (0-1)", ParameterKind.Number) { DefaultValue = "1" },
                ],
                (_, v, ct) => vts.RainbowTintAsync(TintMatcher(v), TimeSpan.FromSeconds(Math.Clamp(Number(v["seconds"], "Seconds", 10), 0, 3600)),
                    Number(v["cycleSeconds"], "Cycle", 2), Number(v["mix"], "Mix", 1), ct)),
            Make("vtubestudio.pickArtMeshes", "Pick ArtMeshes in VTube Studio",
                [
                    new("text", "Message shown in VTube Studio") { DefaultValue = "Pick the ArtMeshes for Cuebot" },
                    new("count", "How many (0 for any)", ParameterKind.Number) { DefaultValue = "0" },
                    Result("vtsArtMeshes"),
                ],
                async (ctx, v, ct) =>
                {
                    var picked = await vts.PickArtMeshesAsync(v["text"], (int)Number(v["count"], "How many", 0), [], ct).ConfigureAwait(false);
                    Store(ctx, v["resultArgument"], string.Join(", ", picked));
                    ctx.Args["vtsArtMeshCount"] = picked.Count;
                }),
            Make("vtubestudio.getArtMeshes", "Get ArtMeshes", [], async (ctx, _, ct) =>
            {
                var meshes = await vts.GetArtMeshesAsync(ct).ConfigureAwait(false);
                ctx.Args["vtsArtMeshes"] = string.Join(", ", meshes.Names);
                ctx.Args["vtsArtMeshCount"] = meshes.Names.Count;
                ctx.Args["vtsArtMeshTags"] = string.Join(", ", meshes.Tags);
                ctx.Args["vtsArtMeshGroups"] = string.Join(", ", meshes.Groups.Select(g => g.Name));
            }),

            // ---- Parameters ----
            Make("vtubestudio.createParameter", "Create custom parameter",
                [
                    new("name", "Name") { Help = "4-32 letters and digits, no spaces, e.g. CuebotHeartRate" },
                    new("explanation", "Explanation") { Help = "Shown in VTube Studio" },
                    new("min", "Lowest value", ParameterKind.Number) { DefaultValue = "0" },
                    new("max", "Highest value", ParameterKind.Number) { DefaultValue = "1" },
                    new("default", "Default value", ParameterKind.Number) { DefaultValue = "0" },
                ],
                (_, v, ct) => vts.CreateCustomParameterAsync(new VtsCustomParameter
                {
                    Name = v["name"], Explanation = v["explanation"].Trim(), Min = Number(v["min"], "Lowest value", 0),
                    Max = Number(v["max"], "Highest value", 1), DefaultValue = Number(v["default"], "Default value", 0),
                }, ct)),
            Make("vtubestudio.deleteParameter", "Delete custom parameter",
                [new("name", "Name") { Suggestions = () => vts.Settings.CustomParameters.Select(p => p.Name) }],
                (_, v, ct) => vts.DeleteCustomParameterAsync(v["name"], ct)),
            Make("vtubestudio.setParameter", "Set parameter (keep it)",
                [
                    Parameter(),
                    new("value", "Value", ParameterKind.Number) { Help = "e.g. %heartRate%" },
                    new("weight", "Weight (0-1)", ParameterKind.Number) { Help = "Mix with face tracking; empty for fully" },
                    new("seconds", "For (seconds)", ParameterKind.Number) { Help = "Empty keeps it until set again or stopped" },
                ],
                (_, v, ct) => vts.SetParameterAsync(Require(v["parameter"], "Parameter"), Number(v["value"], "Value", 0), OptionalNumber(v["weight"], "Weight"),
                    Seconds(v["seconds"], "Seconds"), ct)),
            Make("vtubestudio.stopParameter", "Stop setting parameter",
                [Parameter() with { Help = "Leave empty to stop every parameter Cuebot keeps" }],
                (ctx, v, _) =>
                {
                    ctx.Args["vtsStoppedParameters"] = vts.StopParameter(v["parameter"]);
                    return Task.CompletedTask;
                }),

            // ---- Physics ----
            Make("vtubestudio.physics", "Physics and wind boost",
                [
                    new("strength", "Physics strength", ParameterKind.Number) { Help = "0-100 for the whole model, or a 0-2 multiplier with groups; empty keeps it" },
                    new("wind", "Wind", ParameterKind.Number) { Help = "0-100 for the whole model, or a 0-2 multiplier with groups; empty keeps it" },
                    new("groups", "Physics groups") { Help = "Comma-separated; empty for the whole model", Suggestions = () => vts.Physics.Groups.Select(g => g.DisplayName) },
                    new("seconds", "For (seconds)", ParameterKind.Number) { DefaultValue = "5" },
                ],
                (_, v, ct) => vts.PhysicsOverrideAsync(OptionalNumber(v["strength"], "Strength"), OptionalNumber(v["wind"], "Wind"),
                    VtsProtocol.SplitList(v["groups"]), TimeSpan.FromSeconds(Math.Clamp(Number(v["seconds"], "Seconds", 5), 0.5, 3600)), ct)),
            Make("vtubestudio.getPhysics", "Get physics", [], async (ctx, _, ct) =>
            {
                var physics = await vts.GetPhysicsAsync(ct).ConfigureAwait(false);
                ctx.Args["vtsHasPhysics"] = physics.HasPhysics;
                ctx.Args["vtsPhysicsOn"] = physics.SwitchedOn;
                ctx.Args["vtsPhysicsStrength"] = physics.BaseStrength;
                ctx.Args["vtsWind"] = physics.BaseWind;
                ctx.Args["vtsPhysicsGroups"] = string.Join(", ", physics.Groups.Select(g => g.DisplayName));
            }),

            // ---- VTube Studio itself ----
            Make("vtubestudio.requestPermission", "Ask VTube Studio for a permission",
                [new("permission", "Permission", ParameterKind.Choice) { DefaultValue = VtsProtocol.PermissionCustomImages, Choices = [VtsProtocol.PermissionCustomImages, VtsProtocol.PermissionNdi] }],
                async (ctx, v, ct) => ctx.Args["vtsPermissionGranted"] = await vts.RequestPermissionAsync(v["permission"], ct).ConfigureAwait(false)),
            Make("vtubestudio.ndi", "NDI output",
                [
                    Switch("state", "State", SwitchMode.On),
                    new("width", "Width", ParameterKind.Number) { Help = "A fixed resolution (multiple of 16); empty keeps it" },
                    new("height", "Height", ParameterKind.Number) { Help = "Multiple of 8; empty keeps it" },
                ],
                async (ctx, v, ct) => ctx.Args["vtsNdiOn"] = await vts.SetNdiAsync(ParseSwitch(v["state"]), (int?)OptionalNumber(v["width"], "Width"),
                    (int?)OptionalNumber(v["height"], "Height"), ct).ConfigureAwait(false)),
            Make("vtubestudio.getStatistics", "Get VTube Studio statistics", [], async (ctx, _, ct) =>
            {
                var d = await vts.GetStatisticsAsync(ct).ConfigureAwait(false);
                ctx.Args["vtsUptimeSeconds"] = (long)((d.Num("uptime") ?? 0) / 1000);
                ctx.Args["vtsFramerate"] = d.Num("framerate") ?? 0;
                ctx.Args["vtsVersion"] = d.Str("vTubeStudioVersion");
                ctx.Args["vtsAllowedPlugins"] = (int)(d.Num("allowedPlugins") ?? 0);
                ctx.Args["vtsConnectedPlugins"] = (int)(d.Num("connectedPlugins") ?? 0);
                ctx.Args["vtsWindowWidth"] = (int)(d.Num("windowWidth") ?? 0);
                ctx.Args["vtsWindowHeight"] = (int)(d.Num("windowHeight") ?? 0);
                ctx.Args["vtsFullscreen"] = d.Bool("windowIsFullscreen") ?? false;
            }),
            Make("vtubestudio.getSceneLighting", "Get scene lighting color", [], async (ctx, _, ct) =>
            {
                var d = await vts.GetSceneLightingAsync(ct).ConfigureAwait(false);
                ctx.Args["vtsLightingOn"] = d.Bool("active") ?? false;
                int Byte(string key) => (int)Math.Clamp(d.Num(key) ?? 255, 0, 255);
                ctx.Args["vtsLightingColor"] = $"#{Byte("colorAvgR"):X2}{Byte("colorAvgG"):X2}{Byte("colorAvgB"):X2}";
                ctx.Args["vtsLightingR"] = Byte("colorAvgR");
                ctx.Args["vtsLightingG"] = Byte("colorAvgG");
                ctx.Args["vtsLightingB"] = Byte("colorAvgB");
                ctx.Args["vtsOverlayR"] = (int)(d.Num("colorOverlayR") ?? 255);
                ctx.Args["vtsOverlayG"] = (int)(d.Num("colorOverlayG") ?? 255);
                ctx.Args["vtsOverlayB"] = (int)(d.Num("colorOverlayB") ?? 255);
            }),
            Make("vtubestudio.getTracking", "Get tracking status", [], async (ctx, _, ct) =>
            {
                var (face, left, right) = await vts.GetTrackingAsync(ct).ConfigureAwait(false);
                ctx.Args["vtsFaceFound"] = face;
                ctx.Args["vtsLeftHandFound"] = left ?? false;
                ctx.Args["vtsRightHandFound"] = right ?? false;
            }),
        ];
    }

    private static void StoreVfx(ActionRunContext ctx, VtsVfxState state)
    {
        ctx.Args["vtsVfxOn"] = state.Active;
        ctx.Args["vtsVfxPreset"] = state.PresetActive ? state.ActivePreset : "";
        ctx.Args["vtsVfxActiveEffects"] = state.ActiveEffectCount;
    }

    /// <summary>Comma-separated image sources; base64 and data: URLs contain no commas except the data: header, so those are kept whole.</summary>
    private static string[] SplitImages(string text) =>
        text.Trim().StartsWith("data:", StringComparison.OrdinalIgnoreCase) ? [text.Trim()] : VtsProtocol.SplitList(text);
}
