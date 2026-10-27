using System.Text.Json;
using Cuebot.Core.Scripting;
using Cuebot.Integrations.Common;
using Cuebot.Integrations.HeartRate;
using Cuebot.Integrations.HeartRate.HypeRate;
using Cuebot.Integrations.HeartRate.Pulsoid;
using Cuebot.Integrations.VTubeStudio;
using Cuebot.Integrations.Voicemod;

namespace Cuebot.Integrations;

/// <summary>VTube Studio, Voicemod and heart rate functions available to user scripts.</summary>
internal sealed class IntegrationsScriptFunctions(VTubeStudioService vts, VoicemodService voicemod, PulsoidService pulsoid,
    HypeRateService hypeRate, HeartRateHub hub) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        // ---- VTube Studio ----
        new("vtubestudio.isConnected", "True while VTube Studio is connected and authenticated", [],
            (_, _, _) => Task.FromResult<object?>(vts.IsConnected)),
        new("vtubestudio.triggerHotkey", "Trigger a VTube Studio hotkey by name or id; returns its id", ["hotkey"],
            async (_, a, ct) => await vts.TriggerHotkeyAsync(a.GetString("hotkey"), a.GetString("itemInstanceId"), ct).ConfigureAwait(false)),
        new("vtubestudio.loadModel", "Load a VTube Studio model by name or id; returns its id", ["model"],
            async (_, a, ct) => await vts.LoadModelAsync(a.GetString("model"), ct).ConfigureAwait(false)),
        new("vtubestudio.moveModel", "Move the model (time 0-2 s; omitted values keep their value)",
            ["time", "relative", "positionX", "positionY", "rotation", "size"], async (_, a, ct) =>
            {
                await vts.MoveModelAsync(Num(a, "time") ?? 0.5, a.GetBool("relative"), Num(a, "positionX"), Num(a, "positionY"),
                    Num(a, "rotation"), Num(a, "size"), ct).ConfigureAwait(false);
                return true;
            }),
        new("vtubestudio.setExpression", "Set an expression: state On, Off or Toggle; returns the new state", ["expression", "state"],
            async (_, a, ct) => await vts.SetExpressionAsync(a.GetString("expression"), Steps.ParseSwitch(a.GetString("state", "On")),
                Num(a, "fadeTime") ?? 0.25, ct).ConfigureAwait(false)),
        new("vtubestudio.injectParameters", "Inject parameter values, e.g. \"FaceAngleX=20, MouthOpen=1\", held for holdSeconds",
            ["parameters", "holdSeconds", "mode"], async (_, a, ct) =>
            {
                var parameters = a.GetElement("parameters") is { ValueKind: JsonValueKind.Object or JsonValueKind.Array } json
                    ? json.GetRawText() : a.GetString("parameters");
                await vts.InjectParametersAsync(VtsProtocol.ParseParameterValues(parameters), a.GetString("mode") == "add",
                    a.GetBool("faceFound"), TimeSpan.FromSeconds(Math.Clamp(Num(a, "holdSeconds") ?? 1, 0, 3600)), ct).ConfigureAwait(false);
                return true;
            }),
        new("vtubestudio.getCurrentModel", "The loaded model: name, id, position", [], async (_, _, ct) =>
        {
            var m = await vts.GetCurrentModelAsync(ct).ConfigureAwait(false);
            return new { loaded = m.Loaded, name = m.Name, id = m.Id, positionX = m.Position.PositionX, positionY = m.Position.PositionY, rotation = m.Position.Rotation, size = m.Position.Size };
        }),
        new("vtubestudio.getHotkeys", "Hotkeys of the current model", [], async (_, _, ct) =>
            (await vts.GetHotkeysAsync(ct).ConfigureAwait(false)).Select(h => new { id = h.Id, name = h.Name, type = h.Type, file = h.File }).ToArray()),
        new("vtubestudio.getExpressions", "Expressions of the current model with their state", [], async (_, _, ct) =>
            (await vts.GetExpressionsAsync(ct).ConfigureAwait(false)).Select(e => new { name = e.DisplayName, file = e.File, active = e.Active }).ToArray()),
        new("vtubestudio.request", "Send any VTube Studio API request; returns its data", ["messageType", "data"], async (_, a, ct) =>
        {
            var data = a.GetElement("data") is { ValueKind: JsonValueKind.Object } d ? (object)d : null;
            var result = await vts.RequestAsync(Steps.Require(a.GetString("messageType"), "messageType"), data, ct).ConfigureAwait(false);
            return result.ValueKind == JsonValueKind.Undefined ? null : ScriptArgs.ToClr(result);
        }),

        // ---- Voicemod ----
        new("voicemod.isConnected", "True while Voicemod is connected", [], (_, _, _) => Task.FromResult<object?>(voicemod.IsConnected)),
        new("voicemod.selectVoice", "Select a voice by name or id; returns its name", ["voice"],
            async (_, a, ct) => (await voicemod.SelectVoiceAsync(a.GetString("voice"), ct).ConfigureAwait(false)).DisplayName),
        new("voicemod.randomVoice", "Select a random voice (mode AllVoices, FreeVoices, FavoriteVoices, CustomVoices)", ["mode"], async (_, a, ct) =>
        {
            await voicemod.SelectRandomVoiceAsync(a.GetString("mode", "AllVoices"), ct).ConfigureAwait(false);
            return true;
        }),
        new("voicemod.getCurrentVoice", "The current voice: id and name", [], async (_, _, ct) =>
        {
            var (id, name) = await voicemod.GetCurrentVoiceAsync(ct).ConfigureAwait(false);
            return new { id, name };
        }),
        new("voicemod.getVoices", "All voices", [], async (_, _, ct) =>
            (await voicemod.GetVoicesAsync(ct).ConfigureAwait(false)).Select(v => new { id = v.Id, name = v.DisplayName, favorite = v.Favorited }).ToArray()),
        new("voicemod.getSounds", "All soundboard sounds", [], async (_, _, ct) =>
            (await voicemod.GetSoundsAsync(ct).ConfigureAwait(false)).Select(s => new { name = s.DisplayName, fileName = s.FileName }).ToArray()),
        new("voicemod.playSound", "Play a soundboard sound by name", ["sound"],
            async (_, a, ct) => (await voicemod.PlaySoundAsync(a.GetString("sound"), ct).ConfigureAwait(false)).DisplayName),
        new("voicemod.stopSounds", "Stop all soundboard sounds", [], async (_, _, ct) =>
        {
            await voicemod.StopAllSoundsAsync(ct).ConfigureAwait(false);
            return true;
        }),
        Switch("voicemod.setVoiceChanger", "Voice changer", VoicemodSwitch.VoiceChanger),
        Switch("voicemod.setHearMyself", "Hear myself", VoicemodSwitch.HearMyself),
        Switch("voicemod.setBackgroundEffects", "Background effects", VoicemodSwitch.BackgroundEffects),
        Switch("voicemod.setMute", "Microphone mute", VoicemodSwitch.MuteMic),

        // ---- Heart rate ----
        new("heartrate.get", "Latest heart rate {bpm, source, time} from source Latest, Pulsoid or HypeRate (null if none)", ["source"],
            (_, a, _) =>
            {
                var r = hub.Get(a.GetString("source"));
                return Task.FromResult<object?>(r is null ? null : new { bpm = r.Bpm, source = r.Source, time = r.Time.ToString("o") });
            }),
        new("pulsoid.isConnected", "True while Pulsoid is connected", [], (_, _, _) => Task.FromResult<object?>(pulsoid.IsConnected)),
        new("hyperate.isConnected", "True while HypeRate is connected", [], (_, _, _) => Task.FromResult<object?>(hypeRate.IsConnected)),
    ];

    private ScriptFunction Switch(string name, string label, VoicemodSwitch which) =>
        new(name, $"{label}: state On, Off or Toggle; returns the new state", ["state"],
            async (_, a, ct) => await voicemod.SetSwitchAsync(which, Steps.ParseSwitch(a.GetString("state", "Toggle")), ct).ConfigureAwait(false));

    private static double? Num(JsonElement args, string name) => args.GetElement(name) switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetDouble(),
        { ValueKind: JsonValueKind.String } s => Steps.OptionalNumber(s.GetString() ?? "", name),
        _ => null,
    };
}
