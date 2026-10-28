using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.GoXlr.Common;
using Cuebot.GoXlr.Protocol;
using static Cuebot.GoXlr.Common.Steps;

namespace Cuebot.GoXlr;

/// <summary>
/// Every GoXLR sub-action, as a table. They control the GoXLR set in goxlr.json (or the first one the utility reports).
/// Type ids are stored in actions.json and must never change.
/// </summary>
internal static class GoXlrSubActions
{
    public static readonly string[] TypeIds =
    [
        "goxlr.setVolume", "goxlr.setFaderMute", "goxlr.setFaderChannel", "goxlr.setFaderMuteFunction", "goxlr.setCoughMute",
        "goxlr.loadProfile", "goxlr.loadMicProfile", "goxlr.setEffect", "goxlr.setEffectPreset", "goxlr.loadEffectPreset",
        "goxlr.setSamplerBank", "goxlr.playSample", "goxlr.setRouting", "goxlr.setGlobalColour", "goxlr.setFaderColours",
        "goxlr.setButtonColours", "goxlr.setAnimationMode", "goxlr.setScribbleText", "goxlr.getStatus", "goxlr.sendCommand",
    ];

    public static IReadOnlyList<ISubActionHandler> Create(GoXlrService g)
    {
        ParameterDescriptor Channel(string label = "Channel") => new("channel", label)
        {
            Help = "Mic, Line In, Console, System, Game, Chat, Sample, Music, Headphones, Mic Monitor or Line Out",
            Suggestions = () => GoXlrNames.Channels.Select(GoXlrNames.Label),
        };
        ParameterDescriptor Fader(string help = "A, B, C or D") =>
            new("fader", "Fader", ParameterKind.Choice) { DefaultValue = "A", Choices = GoXlrNames.Faders, Help = help };
        ParameterDescriptor Mute() => new("state", "State", ParameterKind.Choice)
        {
            DefaultValue = "Toggle",
            Choices = ["Mute", "Mute to all", "Unmute", "Toggle"],
            Help = "Mute follows the button's mute function (e.g. mute to stream only); Mute to all mutes everywhere",
        };
        ParameterDescriptor OnOff(string label = "State") =>
            new("state", label, ParameterKind.Choice) { DefaultValue = "Toggle", Choices = ["On", "Off", "Toggle"] };
        ParameterDescriptor Remember() => new("remember", "Remember", ParameterKind.Boolean)
        {
            DefaultValue = "true",
            Help = "The utility loads this one again next time it starts (like choosing it in the utility)",
        };
        ParameterDescriptor Colour(string key, string label, string defaultValue = "") =>
            new(key, label) { DefaultValue = defaultValue, Help = "Hex colour, e.g. #FF8800" };
        ParameterDescriptor Serial() => new("serial", "Device serial")
        {
            Help = "Leave empty for the GoXLR chosen on the GoXLR page (or the first one)",
            Suggestions = () => g.Status.Mixers.Select(m => m.Serial),
        };

        void Copy(ActionRunContext ctx, Dictionary<string, object?> args)
        {
            foreach (var (k, v) in args) ctx.Args[k] = v;
        }

        return
        [
            Make("goxlr.setVolume", "Set volume",
                [Channel(), new("volume", "Volume (0-100)", ParameterKind.Number) { DefaultValue = "100" },
                    new("relative", "Relative change", ParameterKind.Boolean) { DefaultValue = "false", Help = "Add the value (negative lowers) to the current volume" }],
                async (ctx, v, ct) =>
                {
                    var channel = GoXlrNames.Channel(Require(v["channel"], "Channel"));
                    ctx.Args["goXlrChannel"] = channel;
                    ctx.Args["goXlrVolume"] = await g.SetVolumeAsync(channel, Number(v["volume"], "Volume", 100), IsTrue(v["relative"]), ct).ConfigureAwait(false);
                }),
            Make("goxlr.setFaderMute", "Mute fader",
                [new("target", "Fader or channel") { DefaultValue = "A", Help = "A-D, or the channel on a fader (e.g. Music)", Suggestions = () => [.. GoXlrNames.Faders, .. GoXlrNames.Channels.Select(GoXlrNames.Label)] },
                    Mute()],
                async (ctx, v, ct) =>
                {
                    var fader = g.ResolveFader(Require(v["target"], "Fader or channel"));
                    var state = await g.SetFaderMuteAsync(fader.Name, GoXlrService.ParseMuteAction(v["state"]), ct).ConfigureAwait(false);
                    ctx.Args["goXlrFader"] = fader.Name;
                    ctx.Args["goXlrChannel"] = fader.Channel;
                    ctx.Args["goXlrMuteState"] = state;
                    ctx.Args["goXlrMuted"] = GoXlrNames.IsMuted(state);
                }),
            Make("goxlr.setFaderChannel", "Set fader channel", [Fader(), Channel()],
                (_, v, ct) => g.SetFaderChannelAsync(v["fader"], Require(v["channel"], "Channel"), ct)),
            Make("goxlr.setFaderMuteFunction", "Set fader mute function",
                [Fader(), new("function", "Mute function", ParameterKind.Choice) { DefaultValue = "All", Choices = GoXlrNames.MuteFunctions.Select(GoXlrNames.Label).ToArray() }],
                (_, v, ct) => g.SetFaderMuteFunctionAsync(v["fader"], v["function"], ct)),
            Make("goxlr.setCoughMute", "Cough / mic mute", [Mute()], async (ctx, v, ct) =>
            {
                var state = await g.SetCoughMuteAsync(GoXlrService.ParseMuteAction(v["state"]), ct).ConfigureAwait(false);
                ctx.Args["goXlrMuteState"] = state;
                ctx.Args["goXlrMuted"] = GoXlrNames.IsMuted(state);
            }),
            Make("goxlr.loadProfile", "Load profile",
                [new("profile", "Profile") { Suggestions = () => g.Status.Profiles }, Remember()],
                (_, v, ct) => g.LoadProfileAsync(Require(v["profile"], "Profile"), IsTrue(v["remember"]), ct)),
            Make("goxlr.loadMicProfile", "Load mic profile",
                [new("profile", "Mic profile") { Suggestions = () => g.Status.MicProfiles }, Remember()],
                (_, v, ct) => g.LoadMicProfileAsync(Require(v["profile"], "Mic profile"), IsTrue(v["remember"]), ct)),
            Make("goxlr.setEffect", "Set effect on/off",
                [new("effect", "Effect", ParameterKind.Choice) { DefaultValue = "FX", Choices = ["FX", "Megaphone", "Robot", "Hard Tune"], Help = "FX is the master effects switch" },
                    OnOff()],
                async (ctx, v, ct) =>
                {
                    ctx.Args["goXlrEffect"] = GoXlrNames.Effect(v["effect"]);
                    ctx.Args["goXlrEnabled"] = await g.SetEffectAsync(v["effect"], ParseSwitch(v["state"]), ct).ConfigureAwait(false);
                }),
            Make("goxlr.setEffectPreset", "Select effect preset",
                [new("preset", "Preset") { DefaultValue = "1", Help = "1-6 or the preset's name", Suggestions = () => PresetSuggestions(g) }],
                async (ctx, v, ct) => ctx.Args["goXlrPreset"] = await g.SetEffectPresetAsync(Require(v["preset"], "Preset"), ct).ConfigureAwait(false)),
            Make("goxlr.loadEffectPreset", "Load effect preset from library",
                [new("preset", "Library preset") { Help = "Loads a saved preset into the active effect slot", Suggestions = () => g.Status.Presets }],
                (_, v, ct) => g.LoadEffectPresetAsync(Require(v["preset"], "Library preset"), ct)),
            Make("goxlr.setSamplerBank", "Select sampler bank",
                [new("bank", "Bank", ParameterKind.Choice) { DefaultValue = "A", Choices = GoXlrNames.SampleBanks }],
                (_, v, ct) => g.SetSamplerBankAsync(v["bank"], ct)),
            Make("goxlr.playSample", "Play sample",
                [new("bank", "Bank", ParameterKind.Choice) { DefaultValue = "Active", Choices = ["Active", .. GoXlrNames.SampleBanks] },
                    new("button", "Button", ParameterKind.Choice) { DefaultValue = "Top Left", Choices = GoXlrNames.SampleButtons.Select(GoXlrNames.Label).ToArray() },
                    new("mode", "Mode", ParameterKind.Choice) { DefaultValue = "Play next", Choices = ["Play next", "Play by index", "Stop"] },
                    new("index", "Sample index", ParameterKind.Number) { DefaultValue = "1", Help = "For Play by index: 1 = the button's first sample" }],
                async (ctx, v, ct) =>
                {
                    var bank = v["bank"].Trim().Equals("Active", StringComparison.OrdinalIgnoreCase) ? "" : v["bank"];
                    var (b, button) = v["mode"].Trim().ToLowerInvariant() switch
                    {
                        "stop" => await g.StopSampleAsync(bank, v["button"], ct).ConfigureAwait(false),
                        "play by index" => await g.PlaySampleAsync(bank, v["button"], Math.Max(0, (int)Number(v["index"], "Sample index", 1) - 1), ct).ConfigureAwait(false),
                        _ => await g.PlaySampleAsync(bank, v["button"], null, ct).ConfigureAwait(false),
                    };
                    ctx.Args["goXlrBank"] = b;
                    ctx.Args["goXlrSampleButton"] = button;
                }),
            Make("goxlr.setRouting", "Set routing",
                [new("input", "Input", ParameterKind.Choice) { DefaultValue = "Music", Choices = GoXlrNames.Inputs.Select(GoXlrNames.Label).ToArray() },
                    new("output", "Output", ParameterKind.Choice) { DefaultValue = "Broadcast Mix", Choices = GoXlrNames.Outputs.Select(GoXlrNames.Label).ToArray() },
                    OnOff("Routed")],
                async (ctx, v, ct) => ctx.Args["goXlrEnabled"] = await g.SetRoutingAsync(v["input"], v["output"], ParseSwitch(v["state"]), ct).ConfigureAwait(false)),
            Make("goxlr.setGlobalColour", "Set global colour", [Colour("colour", "Colour", "#00FFFF")],
                (_, v, ct) => g.SetGlobalColourAsync(Require(v["colour"], "Colour"), ct)),
            Make("goxlr.setFaderColours", "Set fader colours", [Fader(), Colour("top", "Top colour", "#00FFFF"), Colour("bottom", "Bottom colour", "#000000")],
                (_, v, ct) => g.SetFaderColoursAsync(v["fader"], Require(v["top"], "Top colour"), Require(v["bottom"], "Bottom colour"), ct)),
            Make("goxlr.setButtonColours", "Set button colours",
                [new("button", "Button", ParameterKind.Choice) { DefaultValue = "Cough", Choices = GoXlrNames.Buttons.Select(GoXlrNames.Label).ToArray() },
                    Colour("on", "On colour", "#00FFFF"), new("off", "Off colour") { Help = "Optional hex colour for the off state; empty keeps it" }],
                (_, v, ct) => g.SetButtonColoursAsync(v["button"], Require(v["on"], "On colour"), v["off"], ct)),
            Make("goxlr.setAnimationMode", "Set lighting animation",
                [new("mode", "Animation", ParameterKind.Choice) { DefaultValue = "None", Choices = GoXlrNames.AnimationModes.Select(GoXlrNames.Label).ToArray(), Help = "Needs a GoXLR whose firmware supports animations" }],
                (_, v, ct) => g.SetAnimationModeAsync(v["mode"], ct)),
            Make("goxlr.setScribbleText", "Set scribble strip text",
                [Fader(), new("text", "Text") { Help = "Shown at the bottom of the fader's screen (full-size GoXLR)" }],
                (_, v, ct) => g.SetScribbleTextAsync(v["fader"], v["text"], ct)),
            Make("goxlr.getStatus", "Get status", [Serial()], (ctx, v, _) =>
            {
                var m = g.Mixer(v["serial"]);
                var args = GoXlrArgs.New();
                GoXlrArgs.Status(args, m);
                Copy(ctx, args);
                return Task.CompletedTask;
            }),
            Make("goxlr.sendCommand", "Send raw command",
                [new("command", "Command (JSON)", ParameterKind.Multiline)
                    {
                        DefaultValue = "{\"SetVolume\": [\"Music\", 128]}",
                        Help = "A GoXLR Utility command, e.g. {\"SetFXEnabled\": true}; or a whole request ({\"Command\": [...]}, \"GetStatus\", {\"Daemon\": ...}) with Raw request on",
                    },
                    Serial(),
                    new("raw", "Raw request", ParameterKind.Boolean) { DefaultValue = "false", Help = "Send the JSON as a whole DaemonRequest instead of a device command" }],
                async (ctx, v, ct) =>
                {
                    var json = Require(v["command"], "Command");
                    JsonNode node;
                    try
                    {
                        node = JsonNode.Parse(json) ?? throw new ArgumentException("Command must not be null.");
                    }
                    catch (JsonException ex)
                    {
                        throw new ArgumentException($"Command is not valid JSON: {ex.Message}");
                    }
                    if (IsTrue(v["raw"]))
                    {
                        var answer = await g.RequestAsync(node, ct).ConfigureAwait(false);
                        ctx.Args["goXlrResponse"] = answer.GetRawText();
                    }
                    else
                    {
                        await g.CommandAsync(node, v["serial"], ct).ConfigureAwait(false);
                        ctx.Args["goXlrResponse"] = "\"Ok\"";
                    }
                }),
        ];
    }

    private static IEnumerable<string> PresetSuggestions(GoXlrService g)
    {
        var names = g.IsConnected && g.Status.Mixers.FirstOrDefault()?.Effects is { } e
            ? GoXlrNames.Presets.Select(e.PresetName)
            : [];
        return ["1", "2", "3", "4", "5", "6", .. names];
    }

    private static GoXlrSubAction Make(string id, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) => new(id, name, parameters, run);
}
