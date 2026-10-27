using Cuebot.Core.Scripting;

namespace Cuebot.Speech;

/// <summary>Exposes speech to user scripts.</summary>
internal sealed class SpeechScriptFunctions(SpeechService speech) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        new("speech.speak",
            "Speak a text with an optional voice alias; wait=true returns once spoken, silent=true only renders a file. "
            + "Returns {success, duration (ms), speechFile} when waiting or silent",
            ["text", "alias", "wait", "silent", "badWordFilter"], async (_, args, ct) =>
            {
                var alias = args.GetString("alias");
                var options = new SpeakOptions
                {
                    Alias = alias.Length == 0 ? null : alias,
                    Silent = args.GetBool("silent"),
                    ApplyFilters = args.GetBool("badWordFilter"),
                    Source = "script",
                };
                var wait = args.GetBool("wait");
                var result = await speech.SpeakAsync(args.GetString("text"), options, wait, ct).ConfigureAwait(false);
                return wait || options.Silent
                    ? new { success = !result.Skipped, duration = (long)result.Duration.TotalMilliseconds, speechFile = result.FilePath ?? "" }
                    : null;
            }),
        new("speech.stop", "Stop the text being spoken", [], (_, _, _) =>
        {
            speech.SkipCurrent();
            return Task.FromResult<object?>(null);
        }),
        new("speech.clear", "Drop every queued text", [], (_, _, _) =>
        {
            speech.ClearQueue();
            return Task.FromResult<object?>(null);
        }),
        new("speech.pause", "Pause (paused=true, default) or resume (paused=false) the queue", ["paused"], (_, args, _) =>
        {
            speech.Paused = args.GetBool("paused", true);
            return Task.FromResult<object?>(null);
        }),
        new("speech.setEnabled", "Turn speech on or off", ["enabled"], (_, args, _) =>
        {
            speech.Enabled = args.GetBool("enabled", true);
            return Task.FromResult<object?>(null);
        }),
        new("speech.setEvents", "Turn event speech on or off", ["enabled"], (_, args, _) =>
        {
            speech.EventsEnabled = args.GetBool("enabled", true);
            return Task.FromResult<object?>(null);
        }),
        new("speech.setUserVoice", "Assign a voice alias to a user (empty alias resets)", ["platform", "user", "userId", "alias"],
            (_, args, _) =>
            {
                speech.SetUserVoice(args.GetString("platform"), args.GetString("userId"), args.GetString("user"), args.GetString("alias"));
                return Task.FromResult<object?>(null);
            }),
        new("speech.stopUser", "Drop a user's queued texts and stop theirs; returns how many", ["platform", "user", "userId"],
            (_, args, _) => Task.FromResult<object?>(
                speech.StopUser(args.GetString("platform"), args.GetString("userId"), args.GetString("user")))),
        new("speech.activateVoiceGate", "Activate a VoiceGate profile (\"none\" switches it off); returns success", ["profile"],
            (_, args, _) => Task.FromResult<object?>(speech.ActivateVoiceGateProfile(args.GetString("profile")))),
        new("speech.state", "Current state: enabled, paused, eventsEnabled, queueLength, current, voiceGate", [],
            (_, _, _) => Task.FromResult<object?>(new
            {
                enabled = speech.Enabled,
                paused = speech.Paused,
                eventsEnabled = speech.EventsEnabled,
                queueLength = speech.QueueLength,
                current = speech.CurrentText,
                voiceGate = speech.VoiceGateHolding,
            })),
    ];
}
