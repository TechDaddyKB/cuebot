using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Integrations.Common;
using static Cuebot.Integrations.Common.Steps;

namespace Cuebot.Integrations.VTubeStudio;

/// <summary>Every VTube Studio sub-action, as a table. Type ids are stored in actions.json and must never change.</summary>
internal static class VTubeStudioSubActions
{
    public const string Category = "VTube Studio";

    private static readonly string[] FadeModes = ["linear", "easeIn", "easeOut", "easeBoth", "overshoot", "zip"];

    public static IReadOnlyList<ISubActionHandler> Create(VTubeStudioService vts)
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
            Make("vtubestudio.loadModel", "Load model", [Model()],
                async (ctx, v, ct) => ctx.Args["vtsModelId"] = await vts.LoadModelAsync(v["model"], ct).ConfigureAwait(false)),
            Make("vtubestudio.moveModel", "Move model",
                [
                    new("time", "Time (seconds, 0-2)", ParameterKind.Number) { DefaultValue = "0.5" },
                    new("relative", "Relative to the current position", ParameterKind.Boolean) { DefaultValue = "false" },
                    Optional("positionX", "Position X", "-1 (left) to 1 (right); empty keeps it"),
                    Optional("positionY", "Position Y", "-1 (bottom) to 1 (top); empty keeps it"),
                    Optional("rotation", "Rotation (degrees)", "-360 to 360; empty keeps it"),
                    Optional("size", "Size", "-100 to 100; empty keeps it"),
                ],
                (_, v, ct) => vts.MoveModelAsync(Number(v["time"], "Time", 0.5), v.GetBool("relative"),
                    OptionalNumber(v["positionX"], "Position X"), OptionalNumber(v["positionY"], "Position Y"),
                    OptionalNumber(v["rotation"], "Rotation"), OptionalNumber(v["size"], "Size"), ct)),
            Make("vtubestudio.setExpression", "Set expression state",
                [Expression(), Switch("state", "State", SwitchMode.On), new("fadeTime", "Fade time (seconds)", ParameterKind.Number) { DefaultValue = "0.25" }],
                async (ctx, v, ct) => ctx.Args["vtsExpressionActive"] = await vts.SetExpressionAsync(v["expression"], ParseSwitch(v["state"]),
                    Number(v["fadeTime"], "Fade time", 0.25), ct).ConfigureAwait(false)),
            Make("vtubestudio.removeAllExpressions", "Remove all expressions",
                [new("fadeTime", "Fade time (seconds)", ParameterKind.Number) { DefaultValue = "0.25" }],
                (_, v, ct) => vts.RemoveAllExpressionsAsync(Number(v["fadeTime"], "Fade time", 0.25), ct)),
            Make("vtubestudio.injectParameters", "Inject parameter values",
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
                    new("nameContains", "Art mesh names containing") { Help = "Comma-separated; leave both filters empty to tint everything" },
                    new("tagContains", "Art mesh tags containing") { Help = "Comma-separated" },
                    new("mix", "Mix with scene lighting (0-1)", ParameterKind.Number) { DefaultValue = "1" },
                ],
                async (ctx, v, ct) => ctx.Args["vtsMatchedArtMeshes"] = await vts.ColorTintAsync(VtsProtocol.ParseColor(Require(v["color"], "Color")),
                    VtsProtocol.SplitList(v["nameContains"]), VtsProtocol.SplitList(v["tagContains"]), Number(v["mix"], "Mix", 1), ct).ConfigureAwait(false)),
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
                    new("unloadOnDisconnect", "Unload when Cuebot disconnects", ParameterKind.Boolean) { DefaultValue = "false" },
                    Result("vtsItemInstanceId"),
                ],
                async (ctx, v, ct) => Store(ctx, v["resultArgument"], await vts.LoadItemAsync(v["fileName"], Number(v["positionX"], "Position X", 0),
                    Number(v["positionY"], "Position Y", 0), Number(v["size"], "Size", 0.32), Number(v["rotation"], "Rotation", 0),
                    Number(v["fadeTime"], "Fade time", 0.5), (int?)OptionalNumber(v["order"], "Order"), v.GetBool("unloadOnDisconnect"), ct).ConfigureAwait(false))),
            Make("vtubestudio.unloadItem", "Unload item",
                [
                    ItemInstance() with { Help = "Instance ids or file names, comma-separated" },
                    new("all", "Unload every item in the scene", ParameterKind.Boolean) { DefaultValue = "false" },
                ],
                async (ctx, v, ct) =>
                {
                    var entries = VtsProtocol.SplitList(v["item"]);
                    var ids = entries.Where(e => vts.Items.Any(i => i.InstanceId == e) || !e.Contains('.')).ToArray();
                    var files = entries.Except(ids).ToArray();
                    ctx.Args["vtsUnloadedItems"] = await vts.UnloadItemsAsync(ids, files, v.GetBool("all"), ct).ConfigureAwait(false);
                }),
            Make("vtubestudio.moveItem", "Move item",
                [
                    ItemInstance(),
                    new("time", "Time (seconds)", ParameterKind.Number) { DefaultValue = "1" },
                    new("fadeMode", "Motion", ParameterKind.Choice) { DefaultValue = "easeBoth", Choices = FadeModes },
                    Optional("positionX", "Position X", "Empty keeps it"),
                    Optional("positionY", "Position Y", "Empty keeps it"),
                    Optional("size", "Size", "0-1; empty keeps it"),
                    Optional("rotation", "Rotation", "Degrees; empty keeps it"),
                ],
                (_, v, ct) => vts.MoveItemAsync(v["item"], Number(v["time"], "Time", 1), v["fadeMode"], OptionalNumber(v["positionX"], "Position X"),
                    OptionalNumber(v["positionY"], "Position Y"), OptionalNumber(v["size"], "Size"), OptionalNumber(v["rotation"], "Rotation"), ct)),
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
            }),
            Make("vtubestudio.getModels", "Get available models", [], async (ctx, _, ct) =>
            {
                var models = await vts.GetModelsAsync(ct).ConfigureAwait(false);
                ctx.Args["vtsModels"] = string.Join(", ", models.Select(m => m.Name));
                ctx.Args["vtsModelCount"] = models.Count;
                ctx.Args["vtsModelsJson"] = JsonUtil.Serialize(models.Select(m => new { id = m.Id, name = m.Name, loaded = m.Loaded }));
            }),
            Make("vtubestudio.getHotkeys", "Get hotkeys", [], async (ctx, _, ct) =>
            {
                var hotkeys = await vts.GetHotkeysAsync(ct).ConfigureAwait(false);
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
            Make("vtubestudio.rawRequest", "Send raw request",
                [
                    new("messageType", "Message type") { Help = "A VTube Studio API request, e.g. SceneColorOverlayInfoRequest" },
                    new("data", "Data (JSON)", ParameterKind.Multiline) { Help = "A JSON object; leave empty for none" },
                    Result("vtsResult", "Store response in argument"),
                ],
                async (ctx, v, ct) =>
                {
                    object? data = null;
                    if (v["data"].Trim().Length > 0)
                        data = JsonUtil.TryParse(v["data"]) is { ValueKind: JsonValueKind.Object } o ? o : throw new ArgumentException("Data must be a JSON object.");
                    var result = await vts.RequestAsync(Require(v["messageType"], "Message type"), data, ct).ConfigureAwait(false);
                    Store(ctx, v["resultArgument"], result.ValueKind == JsonValueKind.Undefined ? "{}" : result.GetRawText());
                }),
        ];
    }

    private static ISubActionHandler Make(string typeId, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) =>
        new IntegrationSubAction(typeId, Category, name, parameters, run);
}
