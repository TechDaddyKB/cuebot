using Cuebot.Core.Scripting;

namespace Cuebot.VoiceControl;

/// <summary>Voice control functions for user scripts (Streamer.bot documents no CPH methods for voice control).</summary>
internal sealed class VoiceControlScriptFunctions(VoiceControlService voice) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("voiceControl.isListening", "True while voice control is listening", [],
            (_, _, _) => Task.FromResult<object?>(voice.IsListening)),
        new("voiceControl.status", "Engine, state and status message", [], (_, _, _) => Task.FromResult<object?>(new
        {
            engine = voice.CurrentEngine.Id,
            state = voice.State.ToString(),
            message = voice.StatusMessage,
            listening = voice.IsListening,
            inputDevice = voice.Settings.InputDevice,
        })),
        new("voiceControl.startListening", "Start listening; returns true when listening", [], async (_, _, ct) =>
        {
            await voice.StartListeningAsync(ct).ConfigureAwait(false);
            return voice.IsListening;
        }),
        new("voiceControl.stopListening", "Stop listening", [], async (_, _, _) =>
        {
            await voice.StopListeningAsync().ConfigureAwait(false);
            return true;
        }),
        new("voiceControl.commands", "Every voice command: id, name, enabled, phrase, location, stopAfter, threshold", [],
            (_, _, _) => Task.FromResult<object?>(voice.Commands.Select(Describe).ToArray())),
        new("voiceControl.setCommandState", "Enable, disable or toggle a command (state: Enabled, Disabled, Toggle); returns the new state",
            ["command", "state"], (_, a, _) => Task.FromResult<object?>(
                voice.SetCommandState(a.GetString("command"), VoiceControlSubActions.ParseSwitch(a.GetString("state", "Toggle"))))),
        new("voiceControl.setCommand", "Change a command's phrase (location: Exact, Start, Anywhere; create: add it when missing); returns it",
            ["command", "phrase", "location", "create"], (_, a, _) =>
            {
                CommandLocation? location = Enum.TryParse<CommandLocation>(a.GetString("location"), true, out var l) ? l : null;
                return Task.FromResult<object?>(Describe(voice.SetCommand(a.GetString("command"), a.GetString("phrase"), location, a.GetBool("create"))));
            }),
        new("voiceControl.setInputDevice", "Switch the microphone (empty or \"Default\" = system default)", ["device"], (_, a, _) =>
        {
            var device = a.GetString("device").Trim();
            voice.SetInputDevice(device.Equals("Default", StringComparison.OrdinalIgnoreCase) ? "" : device);
            return Task.FromResult<object?>(true);
        }),
        new("voiceControl.inputDevices", "Microphones: id, name, isDefault", [], (_, _, _) =>
            Task.FromResult<object?>(voice.InputDevices.Select(d => new { id = d.Id, name = d.Name, isDefault = d.IsDefault }).ToArray())),
        new("voiceControl.dictationLog", "Recent recognised phrases: time, text, confidencePercent, commands, ignored", [], (_, _, _) =>
            Task.FromResult<object?>(voice.DictationLog.Select(d => new
            {
                time = d.Time.ToString("O"),
                text = d.Text,
                confidencePercent = d.ConfidencePercent,
                commands = d.Commands,
                ignored = d.Ignored,
            }).ToArray())),
    ];

    private static object Describe(VoiceCommand c) => new
    {
        id = c.Id.ToString(),
        name = c.Name,
        enabled = c.Enabled,
        phrase = c.Phrase,
        location = c.Location.ToString(),
        stopAfter = c.StopAfter,
        overrideGlobal = c.OverrideGlobal,
        confidenceThreshold = c.ConfidenceThreshold,
    };
}
