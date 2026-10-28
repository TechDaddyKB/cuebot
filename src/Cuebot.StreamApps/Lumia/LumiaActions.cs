using Cuebot.Core.Actions;
using Cuebot.StreamApps.Common;
using static Cuebot.StreamApps.Common.Steps;
using E = Cuebot.StreamApps.Lumia.LumiaEvents;

namespace Cuebot.StreamApps.Lumia;

/// <summary>
/// Lumia Stream triggers (category "Lumia Stream"). Cuebot extras: Streamer.bot documents no Lumia triggers; these are
/// the events Lumia's websocket pushes. Type ids are stored in actions.json and must never change.
/// </summary>
internal sealed class LumiaTriggers(LumiaService lumia) : ITriggerProvider
{
    public const string Category = "Lumia Stream";

    public IEnumerable<ITriggerType> GetTriggers()
    {
        ParameterDescriptor Origin() => new("origin", "Platforms") { Help = "e.g. twitch, youtube, kick; comma-separated, empty for any" };
        return
        [
            Trigger(Category, E.Connected, "Connected"),
            Trigger(Category, E.Disconnected, "Disconnected"),
            Trigger(Category, E.ChatMessage, "Chat message", [Origin()],
                (e, s) => MatchesList(s["origin"], e.Text("lumia.origin"))),
            Trigger(Category, E.ChatMessageDeleted, "Chat message deleted", [Origin()],
                (e, s) => MatchesList(s["origin"], e.Text("lumia.origin"))),
            Trigger(Category, E.Command, "Command used",
                [
                    new("command", "Commands")
                    {
                        Help = "Comma-separated command names; empty for any",
                        Suggestions = () => lumia.Info.Values("chat-command"),
                    },
                    Origin(),
                ],
                (e, s) => MatchesList(s["command"], e.Text("command")) && MatchesList(s["origin"], e.Text("lumia.origin"))),
            Trigger(Category, E.Alert, "Alert",
                [
                    new("alert", "Alerts")
                    {
                        Help = "Comma-separated alert names such as twitch-follower; empty for any",
                        Suggestions = () => lumia.Info.Values("alert"),
                    },
                    new("replays", "Replays", ParameterKind.Choice) { DefaultValue = "Include", Choices = ["Include", "Ignore", "Only"] },
                ],
                (e, s) => MatchesList(s["alert"], e.Text("lumia.alert")) && s["replays"] switch
                {
                    "Ignore" => e.Flag("lumia.replay") != true,
                    "Only" => e.Flag("lumia.replay") == true,
                    _ => true,
                }),
            Trigger(Category, E.StatesChanged, "States changed"),
            Trigger(Category, E.Event, "Other event",
                [new("type", "Event types") { Help = "e.g. premium, modqueue_list; comma-separated, empty for any" }],
                (e, s) => MatchesList(s["type"], e.Text("lumia.type"))),
        ];
    }
}

/// <summary>Lumia Stream sub-actions. Type ids are stored in actions.json and must never change.</summary>
internal static class LumiaSubActions
{
    public static readonly string[] TypeIds =
    [
        "lumia.sendCommand", "lumia.setColor", "lumia.setToDefault", "lumia.sendAlert", "lumia.chatbotMessage", "lumia.tts",
        "lumia.setLumiaState", "lumia.setStreamMode", "lumia.getVariable", "lumia.setVariable", "lumia.send",
    ];

    public static IReadOnlyList<ISubActionHandler> Create(LumiaService lumia)
    {
        ParameterDescriptor Extra() => new("extraSettings", "Extra settings", ParameterKind.Multiline)
        {
            Help = "Optional name=value per line (or a JSON object); Lumia uses them as {{name}} variables, e.g. username=%user%",
        };
        ParameterDescriptor Hold() => new("hold", "Make it the new default state", ParameterKind.Boolean) { DefaultValue = "false" };

        return
        [
            Make("lumia.sendCommand", "Send command",
                [
                    new("command", "Command")
                    {
                        Help = "A command from Lumia's Control Center → Shortcuts / commands",
                        Suggestions = () => LumiaProtocol.CommandTypes.SelectMany(lumia.Info.Values).Distinct(StringComparer.OrdinalIgnoreCase),
                    },
                    new("commandType", "Command type", ParameterKind.Choice) { DefaultValue = "chat-command", Choices = LumiaProtocol.CommandTypes },
                    Hold(), Extra(),
                ],
                (_, v, ct) => lumia.SendCommandAsync(Require(v["command"], "Command"), v["commandType"], v.GetBool("hold"), Pairs(v), ct)),
            Make("lumia.setColor", "Set colour",
                [
                    new("color", "Colour") { DefaultValue = "#FF4076", Help = "#RRGGBB (an #AARRGGBB alpha is ignored) or r,g,b" },
                    new("brightness", "Brightness (0-100)", ParameterKind.Number) { Help = "Empty uses Lumia's default (100)" },
                    new("duration", "Duration (ms)", ParameterKind.Number) { Help = "How long before the lights go back to default; empty uses Lumia's default (4000)" },
                    new("transition", "Transition (ms)", ParameterKind.Number) { Help = "Fade time; empty uses Lumia's default (0)" },
                    new("default", "Reset to default instead", ParameterKind.Boolean) { DefaultValue = "false", Help = "Sends to-default and ignores the other settings" },
                    new("lights", "Lights")
                    {
                        Help = "Comma-separated light names (or brand:id, e.g. hue:19); empty for all lights",
                        Suggestions = () => lumia.Info.Lights.Select(l => l.Display),
                    },
                    Hold(),
                ],
                (_, v, ct) => v.GetBool("default")
                    ? lumia.SetToDefaultAsync(ct)
                    : lumia.SetColorAsync(Require(v["color"], "Colour"), OptionalNumber(v["brightness"], "Brightness"),
                        OptionalNumber(v["transition"], "Transition"), OptionalNumber(v["duration"], "Duration"), v["lights"], v.GetBool("hold"), ct)),
            Make("lumia.setToDefault", "Set to default", [], (_, _, ct) => lumia.SetToDefaultAsync(ct)),
            Make("lumia.sendAlert", "Send alert",
                [
                    new("alert", "Alert")
                    {
                        Help = "An alert name such as twitch-follower or twitch-bits",
                        Suggestions = () => lumia.Info.Values("alert"),
                    },
                    Extra(),
                ],
                (_, v, ct) => lumia.SendAlertAsync(Require(v["alert"], "Alert"), Pairs(v), ct)),
            Make("lumia.chatbotMessage", "Chat bot message",
                [
                    new("message", "Message", ParameterKind.Multiline),
                    new("platform", "Platform", ParameterKind.Choice) { DefaultValue = "twitch", Choices = LumiaProtocol.ChatbotPlatforms },
                    new("asSelf", "Send as the streamer", ParameterKind.Boolean) { DefaultValue = "false" },
                ],
                (_, v, ct) => lumia.SendChatbotMessageAsync(Require(v["message"], "Message"), v["platform"], v.GetBool("asSelf"), ct)),
            Make("lumia.tts", "Text to speech",
                [
                    new("message", "Message", ParameterKind.Multiline),
                    new("voice", "Voice") { Help = "Empty for Lumia's default voice", Suggestions = () => lumia.Info.Voices },
                    new("volume", "Volume (0-100)", ParameterKind.Number) { Help = "Windows only; empty for the default" },
                ],
                (_, v, ct) => lumia.SpeakAsync(Require(v["message"], "Message"), v["voice"], OptionalNumber(v["volume"], "Volume"), ct)),
            Make("lumia.setLumiaState", "Set Lumia on/off", [Switch("On", "Off")],
                (_, v, ct) => lumia.SetLumiaStateAsync(ParseSwitch(v["state"]), ct)),
            Make("lumia.setStreamMode", "Set stream mode", [Switch("On", "Off")],
                (_, v, ct) => lumia.SetStreamModeAsync(ParseSwitch(v["state"]), ct)),
            Make("lumia.getVariable", "Get variable",
                [
                    new("name", "Variable") { Help = "A Lumia variable name, e.g. twitch_username" },
                    new("argument", "Save as argument") { DefaultValue = "lumia.variableValue" },
                ],
                async (ctx, v, ct) => Store(ctx, v["argument"], await lumia.GetVariableAsync(Require(v["name"], "Variable"), ct).ConfigureAwait(false))),
            Make("lumia.setVariable", "Set variable",
                [new("name", "Variable"), new("value", "Value")],
                (_, v, ct) => lumia.SetVariableAsync(Require(v["name"], "Variable"), v["value"], ct)),
            Make("lumia.send", "Send API request",
                [
                    new("type", "Type") { Help = "Any Send API type, e.g. studio-scene, clear-queue, overlay-set-visibility" },
                    new("params", "Params (JSON)", ParameterKind.Multiline) { Help = "The params object, e.g. {\"value\":\"Sunset\"}; empty for none" },
                    new("argument", "Save reply as argument") { DefaultValue = "lumia.response" },
                ],
                async (ctx, v, ct) =>
                {
                    var raw = v["params"].Trim();
                    object? parameters = raw.Length == 0 ? null
                        : JsonUtil.TryParse(raw) ?? throw new ArgumentException("Params must be a JSON object.");
                    Store(ctx, v["argument"], await lumia.SendApiAsync(Require(v["type"], "Type"), parameters, ct).ConfigureAwait(false));
                }),
        ];
    }

    private static Dictionary<string, string>? Pairs(ParameterValues v) =>
        v["extraSettings"].Trim().Length == 0 ? null : ParsePairs(v["extraSettings"]);

    private static ISubActionHandler Make(string typeId, string name, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, CancellationToken, Task> run) =>
        new StreamAppSubAction(typeId, LumiaTriggers.Category, name, parameters, run);
}
