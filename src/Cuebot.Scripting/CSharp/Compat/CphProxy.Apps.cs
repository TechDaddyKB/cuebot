using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Streamer.bot.Plugin.Interface.Model;

namespace Cuebot.Scripting.Compat;

/// <summary>
/// Streamer.bot's Stream Deck, VTube Studio, Voicemod, Wave Link, Lumia Stream, MIDI and T.I.T.S. methods, on the
/// script functions of Cuebot's Stream Deck plugin, Integrations, Wave Link, Stream Apps and MIDI modules. Methods
/// returning bool report a failed request as false with a warning; a missing module throws.
/// </summary>
public sealed partial class CphProxy
{
    // ---- Stream Deck (keys are found by the Button ID set on them in Stream Deck; states are 0 and 1) -------------

    public void StreamDeckSetTitle(string buttonId, string title) => StreamDeck("streamdeck.setTitle", buttonId, ("title", title), ("state", ""));
    public void StreamDeckSetTitle(string buttonId, string title, int state) => StreamDeck("streamdeck.setTitle", buttonId, ("title", title), ("state", Num(state)));

    public void StreamDeckSetValue(string buttonId, string value) => StreamDeck("streamdeck.setValue", buttonId, ("value", value), ("indicator", ""));

    public void StreamDeckSetState(string buttonId, int state) => StreamDeck("streamdeck.setState", buttonId, ("state", Num(state)));
    public void StreamDeckToggleState(string buttonId) => StreamDeck("streamdeck.toggleState", buttonId);
    public void StreamDeckShowAlert(string buttonId) => StreamDeck("streamdeck.showAlert", buttonId);
    public void StreamDeckShowOk(string buttonId) => StreamDeck("streamdeck.showOk", buttonId);

    /// <summary>Fills the key with a colour ("#RRGGBB" or a CSS colour name), sent as a one-colour image.</summary>
    public void StreamDeckSetBackgroundColor(string buttonId, string color) => StreamDeckBackground(buttonId, ColorImage(color), "");
    public void StreamDeckSetBackgroundColor(string buttonId, string color, int state) => StreamDeckBackground(buttonId, ColorImage(color), Num(state));

    // Background images: a file or an http(s) URL. Cuebot sends the image alone, so the colour behind it is ignored.
    public void StreamDeckSetBackgroundLocal(string buttonId, string imageFile) => StreamDeckBackground(buttonId, imageFile, "");
    public void StreamDeckSetBackgroundLocal(string buttonId, string imageFile, int state) => StreamDeckBackground(buttonId, imageFile, Num(state));
    public void StreamDeckSetBackgroundLocal(string buttonId, string imageFile, string color) => StreamDeckBackground(buttonId, imageFile, "");
    public void StreamDeckSetBackgroundLocal(string buttonId, string imageFile, string color, int state) => StreamDeckBackground(buttonId, imageFile, Num(state));
    public void StreamDeckSetBackgroundUrl(string buttonId, string imageUrl) => StreamDeckBackground(buttonId, imageUrl, "");
    public void StreamDeckSetBackgroundUrl(string buttonId, string imageUrl, int state) => StreamDeckBackground(buttonId, imageUrl, Num(state));
    public void StreamDeckSetBackgroundUrl(string buttonId, string imageUrl, string color) => StreamDeckBackground(buttonId, imageUrl, "");
    public void StreamDeckSetBackgroundUrl(string buttonId, string imageUrl, string color, int state) => StreamDeckBackground(buttonId, imageUrl, Num(state));

    private void StreamDeckBackground(string buttonId, string image, string state, [CallerMemberName] string method = "") =>
        StreamDeck("streamdeck.setBackground", buttonId, method, ("image", image), ("state", state));

    private void StreamDeck(string function, string buttonId, params (string Key, string? Value)[] args) =>
        StreamDeck(function, buttonId, function, args);

    private void StreamDeck(string function, string buttonId, string method, params (string Key, string? Value)[] args)
    {
        if (string.IsNullOrWhiteSpace(buttonId)) return;
        var values = args.ToDictionary(a => a.Key, a => (object?)(a.Value ?? ""));
        values["buttonId"] = buttonId.Trim();
        Call(function, values, method);
    }

    /// <summary>A 144x144 SVG image of one colour as a data: URI.</summary>
    private static string ColorImage(string color)
    {
        var fill = System.Security.SecurityElement.Escape(string.IsNullOrWhiteSpace(color) ? "#000000" : color.Trim());
        var svg = $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"144\" height=\"144\"><rect width=\"144\" height=\"144\" fill=\"{fill}\"/></svg>";
        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }

    // ---- VTube Studio ---------------------------------------------------------------------------------------------

    public bool VTubeStudioTriggerHotkeyById(string hotkeyId) => Vts("vtubestudio.triggerHotkey", new { hotkey = hotkeyId ?? "", itemInstanceId = "" });
    public bool VTubeStudioTriggerHotkeyByName(string hotkeyName) => Vts("vtubestudio.triggerHotkey", new { hotkey = hotkeyName ?? "", itemInstanceId = "" });
    public bool VTubeStudioLoadModelById(string modelId) => Vts("vtubestudio.loadModel", new { model = modelId ?? "" });
    public bool VTubeStudioLoadModelByName(string modelName) => Vts("vtubestudio.loadModel", new { model = modelName ?? "" });

    /// <summary>Turns an expression (by its .exp3.json file) on.</summary>
    public bool VTubeStudioActivateExpression(string expressionFile) => Vts("vtubestudio.setExpression", new { expression = expressionFile ?? "", state = "On" });

    public bool VTubeStudioDeactivateExpression(string expressionFile) => Vts("vtubestudio.setExpression", new { expression = expressionFile ?? "", state = "Off" });

    /// <summary>The model's position, or null when VTube Studio did not answer.</summary>
    public VTSModelPosition? VTubeStudioGetModelPosition()
    {
        if (TryCall("vtubestudio.getCurrentModel", null) is not { ValueKind: JsonValueKind.Object } model) return null;
        return new VTSModelPosition
        {
            PositionX = DoubleProp(model, "positionX"),
            PositionY = DoubleProp(model, "positionY"),
            Rotation = DoubleProp(model, "rotation"),
            Size = DoubleProp(model, "size"),
        };
    }

    /// <summary>Moves the model over <paramref name="seconds"/> (0-2); null values keep their value.</summary>
    public bool VTubeStudioMoveModel(double seconds, bool relative, double? posX = null, double? posY = null, double? rotation = null, double? size = null) =>
        Vts("vtubestudio.moveModel", new { time = seconds, relative, positionX = posX, positionY = posY, rotation, size });

    // Colour tints: "#RRGGBB" or "#RRGGBBAA"; mixWithSceneLighting 0-1.
    public bool VTubeStudioColorTintAll(string hexColor, double mixWithSceneLighting = 0) => Tint(hexColor, mixWithSceneLighting, new { tintAll = true });

    public bool VTubeStudioColorTintByNames(string hexColor, double mixWithSceneLighting, List<string> filterValues) =>
        Tint(hexColor, mixWithSceneLighting, new { tintAll = false, nameExact = filterValues ?? [] });

    public bool VTubeStudioColorTintByNameContains(string hexColor, double mixWithSceneLighting, List<string> filterValues) =>
        Tint(hexColor, mixWithSceneLighting, new { tintAll = false, nameContains = filterValues ?? [] });

    public bool VTubeStudioColorTintByTags(string hexColor, double mixWithSceneLighting, List<string> filterValues) =>
        Tint(hexColor, mixWithSceneLighting, new { tintAll = false, tagExact = filterValues ?? [] });

    public bool VTubeStudioColorTintByTagContains(string hexColor, double mixWithSceneLighting, List<string> filterValues) =>
        Tint(hexColor, mixWithSceneLighting, new { tintAll = false, tagContains = filterValues ?? [] });

    public bool VTubeStudioColorTintByNumber(string hexColor, double mixWithSceneLighting, List<int> artMeshNumbers) =>
        Tint(hexColor, mixWithSceneLighting, new { tintAll = false, artMeshNumber = artMeshNumbers ?? [] });

    /// <summary>Tints the whole model one random colour.</summary>
    public bool VTubeStudioRandomColorTint() =>
        Tint(Invariant($"#{Random.Shared.Next(256):X2}{Random.Shared.Next(256):X2}{Random.Shared.Next(256):X2}"), 0, new { tintAll = true });

    public bool VTubeStudioResetAllColorTints() => Tint("#FFFFFF", 0, new { tintAll = true });

    /// <summary>Sends any VTube Studio API request (<paramref name="data"/> is its JSON data); returns the response data as JSON ("" on failure).</summary>
    public string VTubeStudioSendRawRequest(string requestType, string data)
    {
        object? payload = string.IsNullOrWhiteSpace(data) ? null : JsonDocument.Parse(data).RootElement.Clone();
        return TryCall("vtubestudio.request", new { messageType = requestType ?? "", data = payload }) is { } result ? Json(result) : "";
    }

    private bool Tint(string hexColor, double mix, object matcher, [CallerMemberName] string method = "")
    {
        var hex = (hexColor ?? "").Trim().TrimStart('#');
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgba))
        {
            LogWarn($"{method}: '{hexColor}' is not a #RRGGBB colour");
            return false;
        }
        if (hex.Length == 6) rgba = (rgba << 8) | 0xFF;
        var colorTint = new
        {
            colorR = (int)(rgba >> 24),
            colorG = (int)(rgba >> 16) & 0xFF,
            colorB = (int)(rgba >> 8) & 0xFF,
            colorA = (int)rgba & 0xFF,
            mixWithSceneLightingColor = Math.Clamp(mix, 0, 1),
        };
        return Try(() => Call("vtubestudio.request", new { messageType = "ColorTintRequest", data = new { colorTint, artMeshMatcher = matcher } }, method), method);
    }

    private bool Vts(string function, object args, [CallerMemberName] string method = "") => Try(() => Call(function, args, method), method);

    private static double DoubleProp(JsonElement element, string name) =>
        double.TryParse(Prop(element, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    // ---- Voicemod -------------------------------------------------------------------------------------------------

    public void VoiceModSelectVoice(string voiceId) => Try(() => Call("voicemod.selectVoice", new { voice = voiceId ?? "" }));

    /// <summary>The selected voice's id; "" when Voicemod did not answer.</summary>
    public string VoiceModGetCurrentVoice() => TryCall("voicemod.getCurrentVoice", null) is { } voice ? Prop(voice, "id") : "";

    public bool VoiceModVoiceChangerOn() => VoicemodSwitch("voicemod.setVoiceChanger", "On");
    public bool VoiceModVoiceChangerOff() => VoicemodSwitch("voicemod.setVoiceChanger", "Off");
    public bool VoiceModHearMyVoiceOn() => VoicemodSwitch("voicemod.setHearMyself", "On");
    public bool VoiceModHearMyVoiceOff() => VoicemodSwitch("voicemod.setHearMyself", "Off");
    public bool VoiceModBackgroundEffectOn() => VoicemodSwitch("voicemod.setBackgroundEffects", "On");
    public bool VoiceModBackgroundEffectOff() => VoicemodSwitch("voicemod.setBackgroundEffects", "Off");

    /// <summary>Turns the bad-language beep on.</summary>
    public void VoiceModCensorOn() => Try(() => Call("voicemod.setCensorBeep", new { state = "On" }));
    public void VoiceModCensorOff() => Try(() => Call("voicemod.setCensorBeep", new { state = "Off" }));

    /// <summary>Whether the voice changer is on, as Voicemod last reported (false when unknown).</summary>
    public bool VoiceModGetVoiceChangerStatus() => TryCall("voicemod.getState", new { @switch = "VoiceChanger" }) is { ValueKind: JsonValueKind.True };

    /// <summary>Whether "hear myself" is on, as Voicemod last reported (false when unknown).</summary>
    public bool VoiceModGetHearMyselfStatus() => TryCall("voicemod.getState", new { @switch = "HearMyself" }) is { ValueKind: JsonValueKind.True };

    /// <summary>Sets a switch; true when it ended up in the requested state.</summary>
    private bool VoicemodSwitch(string function, string state, [CallerMemberName] string method = "") =>
        TryCall(function, new { state }, method) is { } now && (now.ValueKind == JsonValueKind.True) == (state == "On");

    // ---- Wave Link (inputs and microphones by identifier or name; mixer "Local"/"Monitor" or "Stream") ------------

    /// <summary>An input's (channel's) identifier by name; "" when there is none.</summary>
    public string WaveLinkGetInputIdentifier(string inputName) => Prop(WaveLinkChannel(inputName), "id");

    /// <summary>A microphone's (input device's) identifier by name; "" when there is none.</summary>
    public string WaveLinkGetMicrophoneIdentifier(string microphoneName) => Prop(WaveLinkDevice(microphoneName), "id");

    public void WaveLinkInputMute(string identifier, string mixer) => WaveLink("wavelink.setChannelMute", new { channel = identifier, mix = mixer ?? "", state = "Muted" });
    public void WaveLinkInputUnmute(string identifier, string mixer) => WaveLink("wavelink.setChannelMute", new { channel = identifier, mix = mixer ?? "", state = "NotMuted" });
    public void WaveLinkInputToggleMute(string identifier, string mixer) => WaveLink("wavelink.setChannelMute", new { channel = identifier, mix = mixer ?? "", state = "Toggle" });

    /// <summary>Sets an input's volume (0-100) in a mixer.</summary>
    public void WaveLinkInputSetVolume(string inputIdentifier, string mixer, int volume) =>
        WaveLink("wavelink.setChannelVolume", new { channel = inputIdentifier, volume = Math.Clamp(volume, 0, 100), mix = mixer ?? "", relative = false });

    /// <summary>An input's volume (0-100) in a mixer; 0 when unknown.</summary>
    public long WaveLinkInputGetVolume(string inputIdentifier, string mixer)
    {
        var channel = WaveLinkChannel(inputIdentifier);
        var mix = Child(channel, "mixes") is { ValueKind: JsonValueKind.Array } mixes ? mixes.EnumerateArray().FirstOrDefault(m => MixIs(m, mixer)) : default;
        var volume = DoubleProp(mix.ValueKind == JsonValueKind.Object ? mix : channel, "volume");
        return (long)Math.Round(volume);
    }

    // Filters (Wave Link 1.x/2.x; effects on Wave Link 3), by identifier.
    public void WaveLinkInputFilterEnable(string inputIdentifier, string filterIdentifier) => WaveLinkFilter(inputIdentifier, filterIdentifier, "Enabled");
    public void WaveLinkInputFilterDisable(string inputIdentifier, string filterIdentifier) => WaveLinkFilter(inputIdentifier, filterIdentifier, "Disabled");
    public void WaveLinkInputFilterToggle(string inputIdentifier, string filterIdentifier) => WaveLinkFilter(inputIdentifier, filterIdentifier, "Toggle");

    /// <summary>A filter's identifier by name on an input; "" when there is none.</summary>
    public string WaveLinkInputGetFilterIdentifier(string inputIdentifier, string filterName) =>
        Prop(WaveLinkEffect(inputIdentifier, filterName), "id");

    public bool WaveLinkInputGetFilterState(string inputIdentifier, string filterIdentifier) =>
        Prop(WaveLinkEffect(inputIdentifier, filterIdentifier), "enabled") == "True";

    // Filter bypass per mixer: "Bypassed" skips every filter of the input, "Enabled" runs them.
    public void WaveLinkInputFilterBypassBypassed(string inputIdentifier, string mixer) => WaveLinkBypass(inputIdentifier, mixer, "Bypassed");
    public void WaveLinkInputFilterBypassEnabled(string inputIdentifier, string mixer) => WaveLinkBypass(inputIdentifier, mixer, "NotBypassed");
    public void WaveLinkInputFilterBypassToggle(string inputIdentifier, string mixer) => WaveLinkBypass(inputIdentifier, mixer, "Toggle");

    // The same as the three above; Streamer.bot lists both spellings on its "Set Filter Bypass State" page.
    public void WaveLinkInputSetFilterBypassBypassed(string inputIdentifier, string mixer) => WaveLinkBypass(inputIdentifier, mixer, "Bypassed");
    public void WaveLinkInputSetFilterBypassEnabled(string inputIdentifier, string mixer) => WaveLinkBypass(inputIdentifier, mixer, "NotBypassed");
    public void WaveLinkInputSetFilterBypassToggle(string inputIdentifier, string mixer) => WaveLinkBypass(inputIdentifier, mixer, "Toggle");

    // Microphones (input devices).
    public void WaveLinkMicrophoneMute(string microphoneIdentifier) => WaveLink("wavelink.setInputMute", new { device = microphoneIdentifier, state = "Muted" });
    public void WaveLinkMicrophoneUnmute(string microphoneIdentifier) => WaveLink("wavelink.setInputMute", new { device = microphoneIdentifier, state = "NotMuted" });
    public void WaveLinkMicrophoneToggleMute(string microphoneIdentifier) => WaveLink("wavelink.setInputMute", new { device = microphoneIdentifier, state = "Toggle" });

    /// <summary>Sets the microphone's gain (Wave Link 1.x/2.x: its own scale; Wave Link 3: percent).</summary>
    public void WaveLinkMicrophoneSetVolume(string microphoneIdentifier, double volume) =>
        WaveLink("wavelink.setInputGain", new { device = microphoneIdentifier, gain = volume, relative = false });

    /// <summary>The microphone's gain; 0 when unknown.</summary>
    public double WaveLinkMicrophoneGetVolume(string microphoneIdentifier)
    {
        var device = WaveLinkDevice(microphoneIdentifier);
        return Child(device, "inputs") is { ValueKind: JsonValueKind.Array } inputs && inputs.GetArrayLength() > 0
            ? DoubleProp(inputs[0], "gain")
            : 0;
    }

    // Outputs (mixes).
    public void WaveLinkOutputMute(string mixer) => WaveLink("wavelink.setMixMute", new { mix = mixer ?? "", state = "Muted" });
    public void WaveLinkOutputUnmute(string mixer) => WaveLink("wavelink.setMixMute", new { mix = mixer ?? "", state = "NotMuted" });
    public void WaveLinkOutputToggleMute(string mixer) => WaveLink("wavelink.setMixMute", new { mix = mixer ?? "", state = "Toggle" });

    public void WaveLinkSetOutputVolume(string mixer, int volume) =>
        WaveLink("wavelink.setMixVolume", new { mix = mixer ?? "", volume = Math.Clamp(volume, 0, 100), relative = false });

    private void WaveLinkFilter(string input, string filter, string state, [CallerMemberName] string method = "") =>
        WaveLink("wavelink.setChannelEffect", new { channel = input, effect = filter ?? "", state }, method);

    private void WaveLinkBypass(string input, string mixer, string state, [CallerMemberName] string method = "") =>
        WaveLink("wavelink.setFilterBypass", new { channel = input, mix = mixer ?? "", state }, method);

    private void WaveLink(string function, object args, [CallerMemberName] string method = "") => Try(() => Call(function, args, method), method);

    private JsonElement WaveLinkChannel(string idOrName, [CallerMemberName] string method = "") =>
        Call("wavelink.getChannels", null, method) is { ValueKind: JsonValueKind.Array } channels
            ? channels.EnumerateArray().FirstOrDefault(c => IdOrName(c, idOrName))
            : default;

    private JsonElement WaveLinkDevice(string idOrName, [CallerMemberName] string method = "") =>
        Call("wavelink.getInputDevices", null, method) is { ValueKind: JsonValueKind.Array } devices
            ? devices.EnumerateArray().FirstOrDefault(d => IdOrName(d, idOrName)
                || Child(d, "inputs") is { ValueKind: JsonValueKind.Array } inputs && inputs.EnumerateArray().Any(i => IdOrName(i, idOrName)))
            : default;

    private JsonElement WaveLinkEffect(string input, string idOrName, [CallerMemberName] string method = "") =>
        Child(WaveLinkChannel(input, method), "effects") is { ValueKind: JsonValueKind.Array } effects
            ? effects.EnumerateArray().FirstOrDefault(e => IdOrName(e, idOrName))
            : default;

    private static bool IdOrName(JsonElement element, string? idOrName)
    {
        var text = idOrName?.Trim() ?? "";
        return text.Length > 0 && (Prop(element, "id") == text || string.Equals(Prop(element, "name"), text, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a mix matches Streamer.bot's mixer name ("Local"/"Monitor" or "Stream"), or its own name or id.</summary>
    private static bool MixIs(JsonElement mix, string? mixer)
    {
        var text = mixer?.Trim() ?? "";
        if (IdOrName(mix, text)) return true;
        var label = (Prop(mix, "id") + " " + Prop(mix, "name")).ToLowerInvariant();
        return text.ToLowerInvariant() switch
        {
            "stream" => label.Contains("stream", StringComparison.Ordinal),
            "local" or "monitor" => label.Contains("local", StringComparison.Ordinal) || label.Contains("monitor", StringComparison.Ordinal),
            _ => false,
        };
    }

    // ---- Lumia Stream ---------------------------------------------------------------------------------------------

    /// <summary>Runs a Lumia Stream chat command.</summary>
    public void LumiaSendCommand(string command)
    {
        if (!string.IsNullOrWhiteSpace(command)) Call("lumia.sendCommand", new { command = command.Trim(), type = "chat-command", hold = false });
    }

    /// <summary>Returns Lumia's lights to their default state.</summary>
    public void LumiaSetToDefault() => Call("lumia.setToDefault", null);

    // ---- MIDI (devices are Cuebot's MIDI Out connections, by id or name; channels 1-16, 0 taken as 1) -------------

    public void MidiSendNoteOn(Guid deviceId, int channel, int note, int velocity, int duration = 0, bool sendNoteOff = false) =>
        MidiSendNoteOnByName(deviceId.ToString(), channel, note, velocity, duration, sendNoteOff);

    /// <summary>Sends Note On; with <paramref name="sendNoteOff"/>, Note Off after <paramref name="duration"/> milliseconds.</summary>
    public void MidiSendNoteOnByName(string name, int channel, int note, int velocity, int duration = 0, bool sendNoteOff = false) =>
        Call("midi.sendNoteOn", new
        {
            device = name ?? "",
            channel = Num(MidiChannel(channel)),
            note = Num(Math.Clamp(note, 0, 127)),
            velocity = Math.Clamp(velocity, 0, 127),
            duration = Math.Max(0, duration),
            noteOff = sendNoteOff,
        });

    public void MidiSendControlChange(Guid deviceId, int channel, int controller, int value) =>
        MidiSendControlChangeByName(deviceId.ToString(), channel, controller, value);

    public void MidiSendControlChangeByName(string name, int channel, int controller, int value) =>
        Call("midi.sendControlChange", new
        {
            device = name ?? "",
            channel = Num(MidiChannel(channel)),
            controller = Math.Clamp(controller, 0, 127),
            value = Math.Clamp(value, 0, 127),
        });

    public void MidiSendRaw(Guid deviceId, int command, int channel, int data1, int data2) =>
        MidiSendRawByName(deviceId.ToString(), command, channel, data1, data2);

    /// <summary>Sends one channel message: <paramref name="command"/> is its status (0x80 Note Off ... 0xE0 Pitch Bend).</summary>
    public void MidiSendRawByName(string name, int command, int channel, int data1, int data2)
    {
        var status = (command & 0xF0) | ((MidiChannel(channel) - 1) & 0x0F);
        Call("midi.sendRaw", new { device = name ?? "", data = Invariant($"{status:X2} {data1 & 0x7F:X2} {data2 & 0x7F:X2}") });
    }

    private static int MidiChannel(int channel) => Math.Clamp(channel <= 0 ? 1 : channel, 1, 16);

    // ---- T.I.T.S. (the Throwing System) ---------------------------------------------------------------------------

    /// <summary>Throws an item <paramref name="amount"/> times, <paramref name="delay"/> seconds apart. True when T.I.T.S. threw it.</summary>
    public bool ThrowingSystemThrowItemByName(string itemName, double delay = 0.05, int amount = 1) =>
        !string.IsNullOrWhiteSpace(itemName)
        && TryCall("tits.throwItemByName", new { itemName = itemName.Trim(), delay = Math.Max(0, delay), amount = Math.Max(1, amount) })
            is { ValueKind: JsonValueKind.True };

    public bool ThrowingSystemActivateTriggerByName(string triggerName) =>
        !string.IsNullOrWhiteSpace(triggerName) && Try(() => Call("tits.activateTriggerByName", new { triggerName = triggerName.Trim() }));
}
