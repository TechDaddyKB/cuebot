using System.Text.Json;
using Cuebot.Core.Scripting;
using Cuebot.Integrations.Common;
using Cuebot.Integrations.CrowdControl;
using Cuebot.Integrations.StreamLoots;
using Cuebot.Integrations.HeartRate;
using Cuebot.Integrations.HeartRate.HypeRate;
using Cuebot.Integrations.HeartRate.Pulsoid;
using Cuebot.Integrations.VTubeStudio;
using Cuebot.Integrations.Voicemod;

namespace Cuebot.Integrations;

/// <summary>VTube Studio, Voicemod, heart rate, StreamLoots and Crowd Control functions available to user scripts.</summary>
internal sealed class IntegrationsScriptFunctions(VTubeStudioService vts, VoicemodService voicemod, PulsoidService pulsoid,
    HypeRateService hypeRate, HeartRateHub hub, StreamLootsService streamLoots, CrowdControlService crowdControl) : IScriptFunctionProvider
{
    public IEnumerable<ScriptFunction> GetFunctions() =>
    [
        // ---- VTube Studio ----
        new("vtubestudio.isConnected", "True while VTube Studio is connected and authenticated", [],
            (_, _, _) => Task.FromResult<object?>(vts.IsConnected)),
        new("vtubestudio.triggerHotkey", "Trigger a VTube Studio hotkey by name or id; returns its id", ["hotkey", "itemInstanceId"],
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
        new("vtubestudio.setExpression", "Set an expression: state On, Off or Toggle; returns the new state", ["expression", "state", "fadeTime"],
            async (_, a, ct) => await vts.SetExpressionAsync(a.GetString("expression"), Steps.ParseSwitch(a.GetString("state", "On")),
                Num(a, "fadeTime") ?? 0.25, ct).ConfigureAwait(false)),
        new("vtubestudio.injectParameters", "Inject parameter values, e.g. \"FaceAngleX=20, MouthOpen=1\", held for holdSeconds",
            ["parameters", "holdSeconds", "mode", "faceFound"], async (_, a, ct) =>
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
        new("vtubestudio.throwItems", "Throw item files or images at the model; returns how many were thrown",
            ["files", "images", "count", "size", "flinch"], async (_, a, ct) => await vts.ThrowAsync(new VtsThrow
            {
                Files = VtsProtocol.SplitList(a.GetString("files")),
                Images = VtsProtocol.SplitList(a.GetString("images")),
                Count = (int)(Num(a, "count") ?? 1),
                Size = Num(a, "size") ?? 0.15,
                Flinch = Num(a, "flinch") ?? 0.5,
            }, ct).ConfigureAwait(false)),
        new("vtubestudio.colorTint", "Tint the model (or the ArtMeshes whose names contain nameContains); undone after revertAfter seconds if given",
            ["color", "nameContains", "mix", "revertAfter"], async (_, a, ct) => await vts.ColorTintAsync(VtsProtocol.ParseColor(a.GetString("color", "#FF8080")),
                new VtsTintMatcher { NameContains = VtsProtocol.SplitList(a.GetString("nameContains")) }, Num(a, "mix") ?? 1,
                Seconds(a, "revertAfter"), ct).ConfigureAwait(false)),
        new("vtubestudio.loadVfxPreset", "Load a visual effects preset; undone after revertAfter seconds if given", ["preset", "fadeTime", "revertAfter"],
            async (_, a, ct) => (await vts.LoadVfxPresetAsync(a.GetString("preset"), Num(a, "fadeTime") ?? 0.5, Seconds(a, "revertAfter"), ct)
                .ConfigureAwait(false)).ActivePreset),
        new("vtubestudio.setVfx", "Turn visual effects On, Off or Toggle; returns whether they are on", ["state"],
            async (_, a, ct) => await vts.SetVfxOnAsync(Steps.ParseSwitch(a.GetString("state", "Toggle")), ct).ConfigureAwait(false)),
        new("vtubestudio.loadItem", "Load an item file (or an image URL/file) at x, y; returns its instance id",
            ["fileName", "image", "positionX", "positionY", "size", "pin", "unloadAfter"], async (_, a, ct) =>
                await vts.LoadItemAsync(new VtsItemLoad(a.GetString("fileName"))
                {
                    Image = a.GetString("image"), PositionX = Num(a, "positionX") ?? 0, PositionY = Num(a, "positionY") ?? 0,
                    Size = Num(a, "size") ?? 0.32, Pin = VTubeStudioSubActions.ParsePinPlace(a.GetString("pin", "None")),
                    UnloadAfter = Seconds(a, "unloadAfter"),
                }, ct).ConfigureAwait(false)),
        new("vtubestudio.setParameter", "Keep a tracking parameter at a value (re-sent until changed or stopped)", ["parameter", "value", "weight", "seconds"],
            async (_, a, ct) =>
            {
                await vts.SetParameterAsync(Steps.Require(a.GetString("parameter"), "parameter"), Num(a, "value") ?? 0, Num(a, "weight"),
                    Seconds(a, "seconds"), ct).ConfigureAwait(false);
                return true;
            }),
        new("vtubestudio.stopParameter", "Stop keeping a parameter (empty: all); returns how many stopped", ["parameter"],
            (_, a, _) => Task.FromResult<object?>(vts.StopParameter(a.GetString("parameter")))),
        new("vtubestudio.physics", "Override physics strength and/or wind (0-100) for seconds", ["strength", "wind", "seconds"], async (_, a, ct) =>
        {
            await vts.PhysicsOverrideAsync(Num(a, "strength"), Num(a, "wind"), [], TimeSpan.FromSeconds(Math.Clamp(Num(a, "seconds") ?? 5, 0.5, 3600)), ct)
                .ConfigureAwait(false);
            return true;
        }),
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
        new("voicemod.getState", "Last known state of VoiceChanger, HearMyself, BackgroundEffects or MuteMic (null when unknown)", ["switch"],
            (_, a, _) => Task.FromResult<object?>(Enum.TryParse<VoicemodSwitch>(a.GetString("switch"), true, out var which) && Enum.IsDefined(which)
                ? voicemod.GetSwitch(which)
                : throw new ScriptException("switch must be VoiceChanger, HearMyself, BackgroundEffects or MuteMic"))),
        new("voicemod.setCensorBeep", "Turn the bad-language beep On or Off", ["state"], async (_, a, ct) =>
        {
            await voicemod.SetBeepAsync(!string.Equals(a.GetString("state", "On"), "Off", StringComparison.OrdinalIgnoreCase), ct).ConfigureAwait(false);
            return true;
        }),

        // ---- Heart rate ----
        new("heartrate.get", "Latest heart rate {bpm, source, time} from source Latest, Pulsoid or HypeRate (null if none)", ["source"],
            (_, a, _) =>
            {
                var r = hub.Get(a.GetString("source"));
                return Task.FromResult<object?>(r is null ? null : new { bpm = r.Bpm, source = r.Source, time = r.Time.ToString("o") });
            }),
        new("pulsoid.isConnected", "True while Pulsoid is connected", [], (_, _, _) => Task.FromResult<object?>(pulsoid.IsConnected)),
        new("hyperate.isConnected", "True while HypeRate is connected", [], (_, _, _) => Task.FromResult<object?>(hypeRate.IsConnected)),

        // ---- StreamLoots ----
        new("streamloots.isConnected", "True while the StreamLoots alerts stream is open", [],
            (_, _, _) => Task.FromResult<object?>(streamLoots.IsConnected)),
        new("streamloots.sendTestEvent", "Publish a sample alert locally (kind purchase, gift, communityGift or redemption)", ["kind"],
            (_, a, _) => Task.FromResult<object?>(streamLoots.SendTestEvent(a.GetString("kind", "redemption")))),

        // ---- Crowd Control ----
        new("crowdcontrol.isConnected", "True while Crowd Control is connected", [], (_, _, _) => Task.FromResult<object?>(crowdControl.IsConnected)),
        new("crowdcontrol.getSession", "The current game session {active, paused, gameSessionId, gameName, gamePackName, timedEffects}", [],
            (_, _, _) =>
            {
                var s = crowdControl.Session;
                return Task.FromResult<object?>(new
                {
                    active = s is not null,
                    paused = s?.Paused ?? false,
                    gameSessionId = s?.GameSessionId ?? "",
                    gameName = s?.GameName ?? "",
                    gamePackName = s?.GamePackName ?? "",
                    timedEffects = crowdControl.TimedEffects
                        .Select(t => new { requestId = t.RequestId, effectName = t.EffectName, user = t.User, status = t.Status, timeRemaining = t.TimeRemainingMs })
                        .ToArray(),
                });
            }),
        new("crowdcontrol.sendTestEvent", "Publish a sample event locally (e.g. effectSuccess, coinExchange, gameSessionStart)", ["kind"],
            (_, a, _) => Task.FromResult<object?>(crowdControl.SendTestEvent(a.GetString("kind", "effectSuccess")))),
    ];

    private ScriptFunction Switch(string name, string label, VoicemodSwitch which) =>
        new(name, $"{label}: state On, Off or Toggle; returns the new state", ["state"],
            async (_, a, ct) => await voicemod.SetSwitchAsync(which, Steps.ParseSwitch(a.GetString("state", "Toggle")), ct).ConfigureAwait(false));

    private static TimeSpan? Seconds(JsonElement a, string key) => Num(a, key) is { } s && s > 0 ? TimeSpan.FromSeconds(Math.Min(s, 86_400)) : null;

    private static double? Num(JsonElement args, string name) => args.GetElement(name) switch
    {
        { ValueKind: JsonValueKind.Number } n => n.GetDouble(),
        { ValueKind: JsonValueKind.String } s => Steps.OptionalNumber(s.GetString() ?? "", name),
        _ => null,
    };
}
