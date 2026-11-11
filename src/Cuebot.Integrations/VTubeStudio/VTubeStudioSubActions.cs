using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Integrations.Common;
using static Cuebot.Integrations.Common.Steps;

namespace Cuebot.Integrations.VTubeStudio;

/// <summary>Every VTube Studio sub-action, as a table. Type ids are stored in actions.json and must never change.</summary>
internal static partial class VTubeStudioSubActions
{
    public const string Category = "VTube Studio";

    private static readonly string[] FadeModes = ["linear", "easeIn", "easeOut", "easeBoth", "overshoot", "zip"];

    public static IReadOnlyList<ISubActionHandler> Create(VTubeStudioService vts) => [.. Core(vts), .. Extra(vts)];

    /// <summary>"Undo after (seconds)": the change goes back after that time; empty or 0 keeps it.</summary>
    internal static ParameterDescriptor RevertAfter(string what = "the change") => new("revertAfter", "Undo after (seconds)", ParameterKind.Number)
    {
        Help = $"Puts {what} back after this many seconds; leave empty to keep it",
    };

    internal static TimeSpan? Seconds(string value, string label) =>
        OptionalNumber(value, label) is { } s && s > 0 ? TimeSpan.FromSeconds(Math.Min(s, 86_400)) : null;

    /// <summary>The ArtMesh filters of a color tint (all empty: the whole model).</summary>
    internal static IEnumerable<ParameterDescriptor> TintFilters(VTubeStudioService vts) =>
    [
        new("nameContains", "Art mesh names containing") { Help = "Comma-separated; leave every filter empty to tint everything" },
        new("tagContains", "Art mesh tags containing") { Help = "Comma-separated" },
        new("nameExact", "Art mesh names (exact)") { Help = "Comma-separated ArtMesh ids", Suggestions = () => vts.ArtMeshes.Names },
        new("tagExact", "Art mesh tags (exact)") { Help = "Comma-separated", Suggestions = () => vts.ArtMeshes.Tags },
        new("groups", "ArtMesh groups") { Help = "Comma-separated group names (VTube Studio's ArtMesh/layer groups)", Suggestions = () => vts.ArtMeshes.Groups.Select(g => g.Name) },
        new("numbers", "Art mesh numbers") { Help = "Comma-separated positions in the model, e.g. 1, 3, 5" },
    ];

    internal static VtsTintMatcher TintMatcher(ParameterValues v) => new()
    {
        NameContains = VtsProtocol.SplitList(v["nameContains"]),
        TagContains = VtsProtocol.SplitList(v["tagContains"]),
        NameExact = VtsProtocol.SplitList(v["nameExact"]),
        TagExact = VtsProtocol.SplitList(v["tagExact"]),
        Groups = VtsProtocol.SplitList(v["groups"]),
        Numbers = VtsProtocol.SplitList(v["numbers"]).Select(n => int.TryParse(n, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var i) ? i : throw new ArgumentException($"'{n}' is not an ArtMesh number.")).ToArray(),
    };

    private static IReadOnlyList<ISubActionHandler> Core(VTubeStudioService vts)
    {
        ParameterDescriptor Hotkey() => new("hotkey", "Hotkey (name or id)") { Suggestions = () => vts.Hotkeys.Select(h => h.Name) };
        ParameterDescriptor Model() => new("model", "Model (name or id)") { Suggestions = () => vts.Models.Select(m => m.Name) };
        ParameterDescriptor Expression() => new("expression", "Expression (name or file)") { Suggestions = () => vts.Expressions.Select(e => e.DisplayName) };
        ParameterDescriptor ItemFile() => new("fileName", "Item file") { Suggestions = () => vts.ItemFiles.Select(f => f.FileName) };
        ParameterDescriptor ItemInstance() => new("item", "Item (instance id or file name)")
        {
            Suggestions = () => vts.Items.Select(i => i.InstanceId).Concat(vts.Items.Select(i => i.FileName)).Distinct(),
        };
        ParameterDescriptor Optional(string key, string label, string help) => new(key, label) { Help = help };

        return
        [
            Make("vtubestudio.triggerHotkey", "Trigger hotkey",
                [Hotkey(), new("itemInstanceId", "Live2D item instance id") { Help = "Optional: trigger the hotkey of a Live2D item" }],
                async (ctx, v, ct) => ctx.Args["vtsHotkeyId"] = await vts.TriggerHotkeyAsync(v["hotkey"], v["itemInstanceId"], ct).ConfigureAwait(false)),
            Make("vtubestudio.loadModel", "Load model", [Model(), RevertAfter("the previous model")],
                async (ctx, v, ct) => ctx.Args["vtsModelId"] = await vts.LoadModelAsync(v["model"], ct, Seconds(v["revertAfter"], "Undo after")).ConfigureAwait(false)),
            Make("vtubestudio.moveModel", "Move model",
                [
                    new("time", "Time (seconds, 0-2)", ParameterKind.Number) { DefaultValue = "0.5" },
                    new("relative", "Relative to the current position", ParameterKind.Boolean) { DefaultValue = "false" },
                    Optional("positionX", "Position X", "-1 (left) to 1 (right); empty keeps it"),
                    Optional("positionY", "Position Y", "-1 (bottom) to 1 (top); empty keeps it"),
                    Optional("rotation", "Rotation (degrees)", "-360 to 360; empty keeps it"),
                    Optional("size", "Size", "-100 to 100; empty keeps it"),
                    RevertAfter("the model where it was"),
                ],
                (_, v, ct) => vts.MoveModelAsync(Number(v["time"], "Time", 0.5), v.GetBool("relative"),
                    OptionalNumber(v["positionX"], "Position X"), OptionalNumber(v["positionY"], "Position Y"),
                    OptionalNumber(v["rotation"], "Rotation"), OptionalNumber(v["size"], "Size"), ct, Seconds(v["revertAfter"], "Undo after"))),
            Make("vtubestudio.setExpression", "Set expression state",
                [Expression(), Switch("state", "State", SwitchMode.On), new("fadeTime", "Fade time (seconds)", ParameterKind.Number) { DefaultValue = "0.25" },
                 RevertAfter("the expression")],
                async (ctx, v, ct) => ctx.Args["vtsExpressionActive"] = await vts.SetExpressionAsync(v["expression"], ParseSwitch(v["state"]),
                    Number(v["fadeTime"], "Fade time", 0.25), ct, Seconds(v["revertAfter"], "Undo after")).ConfigureAwait(false)),
            Make("vtubestudio.removeAllExpressions", "Remove all expressions",
                [new("fadeTime", "Fade time (seconds)", ParameterKind.Number) { DefaultValue = "0.25" }],
                (_, v, ct) => vts.RemoveAllExpressionsAsync(Number(v["fadeTime"], "Fade time", 0.25), ct)),
            Make("vtubestudio.injectParameters", "Move model parts (advanced)",
                [
                    new("parameters", "Parameters", ParameterKind.Multiline)
                    {
                        Help = "name=value pairs (e.g. FaceAngleX=20, MouthOpen=1; optional @weight) or JSON",
                        Suggestions = () => vts.ParameterNames,
                    },
                    new("mode", "Mode", ParameterKind.Choice) { DefaultValue = "set", Choices = ["set", "add"] },
                    new("holdSeconds", "Hold for (seconds)", ParameterKind.Number) { DefaultValue = "1", Help = "Values are re-sent until this time is over" },
                    new("faceFound", "Report the face as found", ParameterKind.Boolean) { DefaultValue = "false" },
                ],
                (_, v, ct) => vts.InjectParametersAsync(VtsProtocol.ParseParameterValues(v["parameters"]), v["mode"] == "add",
                    v.GetBool("faceFound"), TimeSpan.FromSeconds(Math.Clamp(Number(v["holdSeconds"], "Hold", 1), 0, 3600)), ct)),
            Make("vtubestudio.colorTint", "Color tint",
                [
                    new("color", "Color") { DefaultValue = "#FF8080", Help = "#RRGGBB, #RRGGBBAA or r,g,b[,a]" },
                    .. TintFilters(vts),
                    new("mix", "Mix with scene lighting (0-1)", ParameterKind.Number) { DefaultValue = "1" },
                    RevertAfter("the colors"),
                ],
                async (ctx, v, ct) => ctx.Args["vtsMatchedArtMeshes"] = await vts.ColorTintAsync(VtsProtocol.ParseColor(Require(v["color"], "Color")),
                    TintMatcher(v), Number(v["mix"], "Mix", 1), Seconds(v["revertAfter"], "Undo after"), ct).ConfigureAwait(false)),
            Make("vtubestudio.resetColorTint", "Reset all color tints", [], (_, _, ct) => vts.ResetColorTintAsync(ct)),
            Make("vtubestudio.loadItem", "Load item",
                [
                    ItemFile(),
                    new("positionX", "Position X", ParameterKind.Number) { DefaultValue = "0" },
                    new("positionY", "Position Y", ParameterKind.Number) { DefaultValue = "0" },
                    new("size", "Size (0-1)", ParameterKind.Number) { DefaultValue = "0.32" },
                    new("rotation", "Rotation (degrees)", ParameterKind.Number) { DefaultValue = "0" },
                    new("fadeTime", "Fade time (seconds)", ParameterKind.Number) { DefaultValue = "0.5" },
                    Optional("order", "Order", "Layer order (-30 to 30); empty picks a free one"),
                    new("unloadOnDisconnect", "Unload when Cuebot disconnects", ParameterKind.Boolean) { DefaultValue = "true" },
                    new("image", "Image instead of an item file")
                    {
                        Help = "A picture URL, file or base64 PNG/JPG/GIF, e.g. %userProfileImageUrl%; VTube Studio asks once for permission",
                    },
                    new("askFirst", "Ask in VTube Studio before showing a new image", ParameterKind.Boolean) { DefaultValue = "false" },
                    PinPlace(),
                    new("pinArtMesh", "Pin to ArtMesh") { Help = "For the ArtMesh pin places", Suggestions = () => vts.ArtMeshes.Names },
                    new("flipped", "Flipped", ParameterKind.Boolean) { DefaultValue = "false" },
                    new("locked", "Locked (cannot be dragged)", ParameterKind.Boolean) { DefaultValue = "false" },
                    new("censored", "Censored (pixelated)", ParameterKind.Boolean) { DefaultValue = "false" },
                    new("smoothing", "Movement smoothing (0-1)", ParameterKind.Number) { DefaultValue = "0" },
                    new("unloadAfter", "Unload after (seconds)", ParameterKind.Number) { Help = "Leave empty to keep the item" },
                    Result("vtsItemInstanceId"),
                ],
                async (ctx, v, ct) => Store(ctx, v["resultArgument"], await vts.LoadItemAsync(new VtsItemLoad(v["fileName"])
                {
                    PositionX = Number(v["positionX"], "Position X", 0), PositionY = Number(v["positionY"], "Position Y", 0),
                    Size = Number(v["size"], "Size", 0.32), Rotation = Number(v["rotation"], "Rotation", 0),
                    FadeTime = Number(v["fadeTime"], "Fade time", 0.5), Order = (int?)OptionalNumber(v["order"], "Order"),
                    UnloadWhenPluginDisconnects = v.GetBool("unloadOnDisconnect", true),
                    Image = v["image"], AskFirst = v.GetBool("askFirst"),
                    Pin = ParsePinPlace(v["pin"]), PinArtMesh = v["pinArtMesh"],
                    Flipped = v.GetBool("flipped"), Locked = v.GetBool("locked"), Censored = v.GetBool("censored"),
                    Smoothing = Number(v["smoothing"], "Smoothing", 0), UnloadAfter = Seconds(v["unloadAfter"], "Unload after"),
                }, ct).ConfigureAwait(false))),
            Make("vtubestudio.unloadItem", "Unload item",
                [
                    ItemInstance() with { Help = "Instance ids or file names, comma-separated" },
                    new("all", "Unload every item in the scene", ParameterKind.Boolean) { DefaultValue = "false" },
                    new("onlyCuebot", "Unload every item Cuebot loaded", ParameterKind.Boolean) { DefaultValue = "false" },
                ],
                async (ctx, v, ct) =>
                {
                    var (ids, files) = vts.SplitItemEntries(VtsProtocol.SplitList(v["item"]));
                    ctx.Args["vtsUnloadedItems"] = await vts.UnloadItemsAsync(ids, files, v.GetBool("all"), ct, v.GetBool("onlyCuebot")).ConfigureAwait(false);
                }),
            Make("vtubestudio.moveItem", "Move item",
                [
                    ItemInstance() with { Help = "Instance ids or file names (every copy), comma-separated" },
                    new("time", "Time (seconds)", ParameterKind.Number) { DefaultValue = "1" },
                    new("fadeMode", "Motion", ParameterKind.Choice) { DefaultValue = "easeBoth", Choices = FadeModes },
                    Optional("positionX", "Position X", "Empty keeps it"),
                    Optional("positionY", "Position Y", "Empty keeps it"),
                    Optional("size", "Size", "0-1; empty keeps it"),
                    Optional("rotation", "Rotation", "Degrees; empty keeps it"),
                    Optional("order", "Layer order", "-30 to 30 (a free spot); empty keeps it"),
                    new("flip", "Flip", ParameterKind.Choice) { DefaultValue = "Unchanged", Choices = ["Unchanged", "Flipped", "NotFlipped"] },
                ],
                (_, v, ct) => vts.MoveItemsAsync(VtsProtocol.SplitList(Require(v["item"], "Item")), Number(v["time"], "Time", 1), v["fadeMode"],
                    OptionalNumber(v["positionX"], "Position X"), OptionalNumber(v["positionY"], "Position Y"), OptionalNumber(v["size"], "Size"),
                    OptionalNumber(v["rotation"], "Rotation"), (int?)OptionalNumber(v["order"], "Order"),
                    v["flip"] switch { "Flipped" => true, "NotFlipped" => false, _ => null }, ct)),
            Make("vtubestudio.getCurrentModel", "Get current model", [], async (ctx, _, ct) =>
            {
                var model = await vts.GetCurrentModelAsync(ct).ConfigureAwait(false);
                ctx.Args["vtsModelLoaded"] = model.Loaded;
                ctx.Args["vtsModelName"] = model.Name;
                ctx.Args["vtsModelId"] = model.Id;
                ctx.Args["vtsPositionX"] = model.Position.PositionX;
                ctx.Args["vtsPositionY"] = model.Position.PositionY;
                ctx.Args["vtsRotation"] = model.Position.Rotation;
                ctx.Args["vtsSize"] = model.Position.Size;
                foreach (var (key, value) in VtsProtocol.StreamerBotModelArgs(model)) ctx.Args[key] = value;
            }),
            Make("vtubestudio.getModels", "Get available models", [], async (ctx, _, ct) =>
            {
                var models = await vts.GetModelsAsync(ct).ConfigureAwait(false);
                ctx.Args["vtsModels"] = string.Join(", ", models.Select(m => m.Name));
                ctx.Args["vtsModelCount"] = models.Count;
                ctx.Args["vtsModelsJson"] = JsonUtil.Serialize(models.Select(m => new { id = m.Id, name = m.Name, loaded = m.Loaded }));
            }),
            Make("vtubestudio.getHotkeys", "Get hotkeys",
                [
                    Model() with { Label = "Of another model", Help = "Leave empty for the current model" },
                    new("live2DItem", "Of a Live2D item (file name)") { Suggestions = () => vts.ItemFiles.Where(f => f.Type == "Live2D").Select(f => f.FileName) },
                ],
                async (ctx, v, ct) =>
            {
                var hotkeys = await vts.GetHotkeysAsync(ct, v["model"], v["live2DItem"]).ConfigureAwait(false);
                ctx.Args["vtsHotkeys"] = string.Join(", ", hotkeys.Select(h => h.Name));
                ctx.Args["vtsHotkeyCount"] = hotkeys.Count;
                ctx.Args["vtsHotkeysJson"] = JsonUtil.Serialize(hotkeys.Select(h => new { id = h.Id, name = h.Name, type = h.Type, file = h.File }));
            }),
            Make("vtubestudio.getExpressions", "Get expressions", [], async (ctx, _, ct) =>
            {
                var expressions = await vts.GetExpressionsAsync(ct).ConfigureAwait(false);
                ctx.Args["vtsExpressions"] = string.Join(", ", expressions.Select(e => e.DisplayName));
                ctx.Args["vtsActiveExpressions"] = string.Join(", ", expressions.Where(e => e.Active).Select(e => e.DisplayName));
                ctx.Args["vtsExpressionCount"] = expressions.Count;
                ctx.Args["vtsExpressionsJson"] = JsonUtil.Serialize(expressions.Select(e => new { name = e.DisplayName, file = e.File, active = e.Active }));
            }),
            Make("vtubestudio.rawRequest", "Send VTube Studio request (advanced)",
                [
                    new("messageType", "Message type") { Help = "A VTube Studio API request, e.g. SceneColorOverlayInfoRequest" },
                    new("data", "Data (JSON)", ParameterKind.Multiline) { Help = "A JSON object; leave empty for none" },
                    Result("vtsResult", "Save the reply in variable"),
                    new("variablePrefix", "Variable prefix")
                    {
                        DefaultValue = "vtsRaw",
                        Help = "Also stores each response field, e.g. %vtsRaw.data.modelName%; leave empty to skip",
                    },
                ],
                async (ctx, v, ct) =>
                {
                    object? data = null;
                    if (v["data"].Trim().Length > 0)
                        data = JsonUtil.TryParse(v["data"]) is { ValueKind: JsonValueKind.Object } o ? o : throw new ArgumentException("Data must be a JSON object.");
                    var result = await vts.RequestAsync(Require(v["messageType"], "Message type"), data, ct).ConfigureAwait(false);
                    Store(ctx, v["resultArgument"], result.ValueKind == JsonValueKind.Undefined ? "{}" : result.GetRawText());
                    VtsProtocol.StoreRawResponse(ctx.Args, v["variablePrefix"], result);
                }),
        ];
    }

    private static ISubActionHandler Make(string typeId, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) =>
        new IntegrationSubAction(typeId, Category, name, parameters, run);
}
