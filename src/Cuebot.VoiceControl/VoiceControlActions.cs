using System.Globalization;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.VoiceControl;

/// <summary>Voice Control triggers. Type ids are stored in actions.json and must never change.</summary>
internal sealed class VoiceControlTriggers(VoiceControlService voice) : ITriggerProvider
{
    public const string Category = "Voice Control";
    public const string CommandTrigger = "voiceControl.command";
    public const string DictationTrigger = "voiceControl.dictation";

    public IEnumerable<ITriggerType> GetTriggers()
    {
        ParameterDescriptor[] Filter() =>
        [
            new("command", "Voice Control")
            {
                DefaultValue = "Any",
                Help = "Voice command name (comma-separated for several); Any = every command",
                Suggestions = () => ["Any", .. voice.Commands.Select(c => c.Name)],
            },
        ];
        return
        [
            new EventTriggerType(CommandTrigger, Category, "Command", VoiceControlEvents.Command, Filter(), (e, s, _) => MatchesCommand(e, s["command"])),
            new EventTriggerType(DictationTrigger, Category, "Dictation", VoiceControlEvents.Dictation, Filter(), (e, s, _) => MatchesCommand(e, s["command"])),
        ];
    }

    /// <summary>Any/empty matches everything; otherwise the event's command name or id must be in the list.</summary>
    internal static bool MatchesCommand(BotEvent e, string filter)
    {
        var wanted = filter.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (wanted.Length == 0 || wanted.Any(w => w.Equals("Any", StringComparison.OrdinalIgnoreCase))) return true;
        var name = Text(e, "voiceCommandName");
        var id = Text(e, "voiceCommandId");
        return wanted.Any(w => (name.Length > 0 && w.Equals(name, StringComparison.OrdinalIgnoreCase)) || (id.Length > 0 && w.Equals(id, StringComparison.OrdinalIgnoreCase)));
    }

    private static string Text(BotEvent e, string key) =>
        e.Args.TryGetValue(key, out var v) ? Convert.ToString(v, CultureInfo.InvariantCulture) ?? "" : "";
}

/// <summary>Voice Control sub-actions. Type ids are stored in actions.json and must never change.</summary>
internal static class VoiceControlSubActions
{
    public const string Category = "Voice Control";

    public static readonly string[] TypeIds =
    [
        "voiceControl.setCommand", "voiceControl.setCommandState", "voiceControl.setInput", "voiceControl.setListening",
    ];

    public static IReadOnlyList<ISubActionHandler> Create(VoiceControlService voice)
    {
        ParameterDescriptor Command() => new("command", "Command")
        {
            Help = "Voice command name (or id)",
            Suggestions = () => voice.Commands.Select(c => c.Name),
        };

        return
        [
            new Step("voiceControl.setCommand", "Set Voice Control Command",
                [
                    Command(),
                    new("phrase", "Command phrase") { Help = "What to listen for; variables are substituted" },
                    new("location", "Location", ParameterKind.Choice) { DefaultValue = "Unchanged", Choices = ["Unchanged", "Exact", "Start", "Anywhere"] },
                    new("create", "Create the command if it doesn't exist", ParameterKind.Boolean) { DefaultValue = "false" },
                ],
                (ctx, v) =>
                {
                    CommandLocation? location = Enum.TryParse<CommandLocation>(v["location"], true, out var l) ? l : null;
                    var command = voice.SetCommand(v["command"], v["phrase"], location, v.GetBool("create"));
                    ctx.Args["voiceCommandId"] = command.Id.ToString();
                    ctx.Args["voiceCommandName"] = command.Name;
                    ctx.Args["voiceCommandPhrase"] = command.Phrase;
                    return Task.CompletedTask;
                }),
            new Step("voiceControl.setCommandState", "Set Voice Control Command State",
                [Command(), new("state", "State", ParameterKind.Choice) { DefaultValue = "Enabled", Choices = ["Enabled", "Disabled", "Toggle"] }],
                (ctx, v) =>
                {
                    ctx.Args["voiceCommandEnabled"] = voice.SetCommandState(v["command"], ParseSwitch(v["state"]));
                    return Task.CompletedTask;
                }),
            new Step("voiceControl.setInput", "Set Voice Control Input",
                [
                    new("device", "Audio Input Device")
                    {
                        DefaultValue = "Default",
                        Help = "Microphone name; Default = the system default. Takes effect immediately while listening.",
                        Suggestions = () => ["Default", .. voice.InputDevices.Select(d => d.Name)],
                    },
                ],
                (_, v) =>
                {
                    var device = v["device"].Trim();
                    voice.SetInputDevice(device.Equals("Default", StringComparison.OrdinalIgnoreCase) ? "" : device);
                    return Task.CompletedTask;
                }),
            new Step("voiceControl.setListening", "Start/Stop Listening",
                [new("state", "State", ParameterKind.Choice) { DefaultValue = "Start", Choices = ["Start", "Stop", "Toggle"] }],
                async (ctx, v) =>
                {
                    await voice.SetListeningAsync(ParseSwitch(v["state"])).ConfigureAwait(false);
                    ctx.Args["voiceControlListening"] = voice.IsListening;
                }),
        ];
    }

    public static VoiceSwitch ParseSwitch(string value) => value.Trim().ToLowerInvariant().Replace(" ", "") switch
    {
        "toggle" or "" => VoiceSwitch.Toggle,
        "on" or "true" or "1" or "enabled" or "enable" or "start" => VoiceSwitch.On,
        "off" or "false" or "0" or "disabled" or "disable" or "stop" => VoiceSwitch.Off,
        _ => throw new ArgumentException($"Unknown state '{value}'."),
    };

    private sealed class Step(string typeId, string displayName, IReadOnlyList<ParameterDescriptor> parameters,
        Func<ActionRunContext, ParameterValues, Task> run) : ISubActionHandler
    {
        public string TypeId => typeId;
        public string Category => VoiceControlSubActions.Category;
        public string DisplayName => displayName;
        public IReadOnlyList<ParameterDescriptor> Parameters => parameters;

        public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues values, CancellationToken ct)
        {
            await run(context, values).ConfigureAwait(false);
            return SubActionResult.Next;
        }
    }
}
