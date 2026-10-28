using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.GoXlr.Protocol;

namespace Cuebot.GoXlr.Model;

/// <summary>One fader (A-D): the channel on it, its mute state and what its mute button mutes.</summary>
public sealed record GoXlrFader(string Name, string Channel, string MuteState, string MuteFunction, string ScribbleText)
{
    public bool Muted => GoXlrNames.IsMuted(MuteState);
}

/// <summary>The cough (Mini: mic mute) button.</summary>
public sealed record GoXlrCough(string MuteState, bool IsToggle, string MuteFunction)
{
    public bool Muted => GoXlrNames.IsMuted(MuteState);
}

/// <summary>Effects state (full-size GoXLR only).</summary>
public sealed record GoXlrEffects(bool FxEnabled, bool MegaphoneEnabled, bool RobotEnabled, bool HardTuneEnabled, string ActivePreset,
    IReadOnlyDictionary<string, string> PresetNames)
{
    public bool IsEnabled(string effect) => effect switch
    {
        "FX" => FxEnabled,
        "Megaphone" => MegaphoneEnabled,
        "Robot" => RobotEnabled,
        "HardTune" => HardTuneEnabled,
        _ => false,
    };

    public string PresetName(string preset) => PresetNames.TryGetValue(preset, out var n) && n.Length > 0 ? n : GoXlrNames.Label(preset);
}

/// <summary>Sampler state (full-size GoXLR only). Keys of <see cref="Playing"/> are "Bank/Button" (e.g. "A/TopLeft").</summary>
public sealed record GoXlrSampler(string ActiveBank, IReadOnlyDictionary<string, bool> Playing, IReadOnlyDictionary<string, int> SampleCounts);

/// <summary>One GoXLR as the utility's status object describes it (the parts Cuebot uses).</summary>
public sealed record GoXlrMixer(
    string Serial,
    string DeviceType,
    string ProfileName,
    string MicProfileName,
    IReadOnlyDictionary<string, GoXlrFader> Faders,
    IReadOnlyDictionary<string, int> Volumes,
    GoXlrCough Cough,
    GoXlrEffects? Effects,
    GoXlrSampler? Sampler,
    IReadOnlyDictionary<string, bool> ButtonsDown,
    IReadOnlyDictionary<string, bool> Routing,
    string MicType)
{
    /// <summary>Full or Mini ("Unknown" until the utility knows).</summary>
    public bool IsMini => DeviceType == "Mini";

    /// <summary>The fader a channel sits on, or null.</summary>
    public GoXlrFader? FaderFor(string channel) => Faders.Values.FirstOrDefault(f => f.Channel == channel);

    public int Volume(string channel) => Volumes.TryGetValue(channel, out var v) ? v : 0;

    public bool Routed(string input, string output) => Routing.TryGetValue($"{input}/{output}", out var on) && on;
}

/// <summary>The utility's whole status, reduced to what Cuebot uses.</summary>
public sealed record GoXlrStatus(
    string DaemonVersion,
    IReadOnlyList<GoXlrMixer> Mixers,
    IReadOnlyList<string> Profiles,
    IReadOnlyList<string> MicProfiles,
    IReadOnlyList<string> Presets,
    IReadOnlyList<string> Samples)
{
    public static GoXlrStatus Empty { get; } = new("", [], [], [], [], []);

    /// <summary>
    /// The mixer with that serial; with an empty serial the configured default (when connected), else the first one.
    /// Throws when there is none.
    /// </summary>
    public GoXlrMixer Find(string serial, string fallbackSerial = "")
    {
        var wanted = serial.Trim().Length > 0 ? serial.Trim() : fallbackSerial.Trim();
        if (wanted.Length > 0)
            return Mixers.FirstOrDefault(m => string.Equals(m.Serial, wanted, StringComparison.OrdinalIgnoreCase))
                ?? throw new GoXlrRequestException($"No GoXLR with serial '{wanted}' is connected to the utility.");
        return Mixers.FirstOrDefault() ?? throw new GoXlrRequestException("The GoXLR Utility reports no connected GoXLR.");
    }
}

/// <summary>Reads the utility's status JSON (DaemonStatus, serialized by serde with snake_case field names).</summary>
internal static class GoXlrStatusParser
{
    public static GoXlrStatus Parse(JsonNode? status)
    {
        if (status is not JsonObject root) return GoXlrStatus.Empty;
        var mixers = new List<GoXlrMixer>();
        if (root["mixers"] is JsonObject mixerMap)
            foreach (var (serial, node) in mixerMap.OrderBy(m => m.Key, StringComparer.Ordinal))
                if (node is JsonObject mixer) mixers.Add(ParseMixer(serial, mixer));
        var files = root["files"];
        return new GoXlrStatus(
            Str(root["config"]?["daemon_version"]),
            mixers,
            List(files?["profiles"]),
            List(files?["mic_profiles"]),
            List(files?["presets"]),
            files?["samples"] is JsonObject samples ? samples.Select(s => s.Key).Order(StringComparer.OrdinalIgnoreCase).ToArray() : []);
    }

    internal static GoXlrMixer ParseMixer(string serial, JsonObject m)
    {
        var faders = new Dictionary<string, GoXlrFader>();
        foreach (var name in GoXlrNames.Faders)
        {
            var f = m["fader_status"]?[name];
            if (f is null) continue;
            faders[name] = new GoXlrFader(name, Str(f["channel"]), Str(f["mute_state"]), Str(f["mute_type"]), Str(f["scribble"]?["bottom_text"]));
        }

        var volumes = new Dictionary<string, int>();
        if (m["levels"]?["volumes"] is JsonObject vols)
            foreach (var (channel, v) in vols)
                volumes[channel] = Int(v);

        var cough = m["cough_button"];
        var effects = m["effects"] is JsonObject e
            ? new GoXlrEffects(
                Bool(e["is_enabled"]),
                Bool(e["current"]?["megaphone"]?["is_enabled"]),
                Bool(e["current"]?["robot"]?["is_enabled"]),
                Bool(e["current"]?["hard_tune"]?["is_enabled"]),
                Str(e["active_preset"]),
                e["preset_names"] is JsonObject names ? names.ToDictionary(n => n.Key, n => Str(n.Value)) : new Dictionary<string, string>())
            : null;

        GoXlrSampler? sampler = null;
        if (m["sampler"] is JsonObject s)
        {
            var playing = new Dictionary<string, bool>();
            var counts = new Dictionary<string, int>();
            if (s["banks"] is JsonObject banks)
                foreach (var (bank, buttons) in banks)
                    if (buttons is JsonObject buttonMap)
                        foreach (var (button, state) in buttonMap)
                        {
                            playing[$"{bank}/{button}"] = Bool(state?["is_playing"]);
                            counts[$"{bank}/{button}"] = state?["samples"] is JsonArray list ? list.Count : 0;
                        }
            sampler = new GoXlrSampler(Str(s["active_bank"]), playing, counts);
        }

        var buttonsDown = new Dictionary<string, bool>();
        if (m["button_down"] is JsonObject down)
            foreach (var (button, v) in down)
                buttonsDown[button] = Bool(v);

        var routing = new Dictionary<string, bool>();
        if (m["router"] is JsonObject router)
            foreach (var (input, outputs) in router)
                if (outputs is JsonObject outputMap)
                    foreach (var (output, v) in outputMap)
                        routing[$"{input}/{output}"] = Bool(v);

        return new GoXlrMixer(
            serial,
            Str(m["hardware"]?["device_type"]) is { Length: > 0 } type ? type : "Unknown",
            Str(m["profile_name"]),
            Str(m["mic_profile_name"]),
            faders,
            volumes,
            new GoXlrCough(Str(cough?["state"]), Bool(cough?["is_toggle"]), Str(cough?["mute_type"])),
            effects,
            sampler,
            buttonsDown,
            routing,
            Str(m["mic_status"]?["mic_type"]));
    }

    private static string Str(JsonNode? node) => node switch
    {
        null => "",
        JsonValue v when v.GetValueKind() == JsonValueKind.String => v.GetValue<string>(),
        JsonValue v => v.ToJsonString(),
        _ => "",
    };

    private static bool Bool(JsonNode? node) => node is JsonValue v && v.GetValueKind() == JsonValueKind.True;

    private static int Int(JsonNode? node) =>
        node is JsonValue v && v.GetValueKind() == JsonValueKind.Number
        && double.TryParse(v.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d)
            ? (int)Math.Round(d)
            : 0;

    private static string[] List(JsonNode? node) =>
        node is JsonArray arr ? arr.Select(Str).Where(s => s.Length > 0).Order(StringComparer.OrdinalIgnoreCase).ToArray() : [];
}
