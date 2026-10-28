using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;
using Cuebot.WaveLink.Common;
using Cuebot.WaveLink.Model;
using E = Cuebot.WaveLink.WaveLinkEvents;
using L = Cuebot.WaveLink.WaveLinkEvents.Legacy;

namespace Cuebot.WaveLink;

/// <summary>
/// Wave Link triggers. The "Wave Link" category holds Streamer.bot's Wave Link 3 triggers; they fire for both API
/// generations wherever the change means the same thing. The "Wave Link (legacy)" category holds Streamer.bot's 1.x/2.x
/// events, each listening to the shared event and filtering on <c>waveLinkLegacyEvent</c>. Type ids are stored in
/// actions.json and must never change.
/// </summary>
internal sealed class WaveLinkTriggers(WaveLinkService waveLink) : ITriggerProvider
{
    public const string Category = "Wave Link";
    public const string LegacyCategory = "Wave Link (legacy)";

    /// <summary>Streamer.bot's legacy events → (type id, display name, shared event types).</summary>
    internal static readonly (string Name, string TypeId, string Display, string[] Events)[] LegacyTriggers =
    [
        (L.FilterAdded, "wavelink.legacy.filterAdded", "Filter added", [E.ChannelEffectsChanged]),
        (L.FilterBypassStateChanged, "wavelink.legacy.filterBypassStateChanged", "Filter bypass state changed", [E.ChannelEffectsChanged]),
        (L.FilterChanged, "wavelink.legacy.filterChanged", "Filter changed", [E.ChannelEffectsChanged]),
        (L.FilterDeleted, "wavelink.legacy.filterDeleted", "Filter deleted", [E.ChannelEffectsChanged]),
        (L.InputLevelMeterChanged, "wavelink.legacy.inputLevelMeterChanged", "Input level meter changed", [E.LevelMeter]),
        (L.InputMuteChanged, "wavelink.legacy.inputMuteChanged", "Input mute changed", [E.ChannelMuteChanged]),
        (L.InputNameChanged, "wavelink.legacy.inputNameChanged", "Input name changed", [E.ChannelUpdated]),
        (L.InputVolumeChanged, "wavelink.legacy.inputVolumeChanged", "Input volume changed", [E.ChannelVolumeChanged]),
        (L.MicrophoneBalanceChanged, "wavelink.legacy.microphoneBalanceChanged", "Microphone balance changed", [E.InputDeviceUpdated]),
        (L.MicrophoneGainChanged, "wavelink.legacy.microphoneGainChanged", "Microphone gain changed", [E.InputGainChanged]),
        (L.MicrophoneMuteChanged, "wavelink.legacy.microphoneMuteChanged", "Microphone mute changed", [E.InputDeviceUpdated]),
        (L.MicrophoneOutputVolumeChanged, "wavelink.legacy.microphoneOutputVolumeChanged", "Microphone output volume changed", [E.InputDeviceUpdated]),
        (L.MicrophoneSettingChanged, "wavelink.legacy.microphoneSettingChanged", "Microphone setting changed",
            [E.InputDeviceUpdated, E.InputGainChanged, E.InputDspEffectsChanged]),
        (L.OutputLevelMeterChanged, "wavelink.legacy.outputLevelMeterChanged", "Output level meter changed", [E.LevelMeter]),
        (L.OutputMuteChanged, "wavelink.legacy.outputMuteChanged", "Output mute changed", [E.MixUpdated]),
        (L.OutputSwitched, "wavelink.legacy.outputSwitched", "Output switched", [E.OutputMixChanged]),
        (L.OutputVolumeChanged, "wavelink.legacy.outputVolumeChanged", "Output volume changed", [E.MixUpdated]),
        (L.SelectedOutputChanged, "wavelink.legacy.selectedOutputChanged", "Selected output changed", [E.MainOutputChanged]),
    ];

    private WaveLinkSnapshot S => waveLink.Snapshot;

    public IEnumerable<ITriggerType> GetTriggers()
    {
        ParameterDescriptor Channel() => new("channel", "Channels")
        {
            Help = "Comma-separated channel names or ids (Wave Link 1.x/2.x: inputs); leave empty for any",
            Suggestions = () => S.Channels.Select(c => c.Name),
        };
        ParameterDescriptor Mix(string help = "Comma-separated mix names or ids; leave empty for any (including the channel itself)") =>
            new("mix", "Mixes") { Help = help, Suggestions = () => S.Mixes.Select(m => m.Name) };
        ParameterDescriptor InputDevice() => new("inputDevice", "Input devices")
        {
            Help = "Comma-separated input device names or ids (Wave Link 1.x/2.x: microphones); leave empty for any",
            Suggestions = () => S.InputDevices.Select(d => d.Name),
        };
        ParameterDescriptor OutputDevice() => new("outputDevice", "Output devices")
        {
            Help = "Comma-separated output device names or ids; leave empty for any",
            Suggestions = () => S.OutputDevices.Select(d => d.Name),
        };
        ParameterDescriptor State(string on, string off) =>
            new("stateFilter", "State", ParameterKind.Choice) { DefaultValue = "Any", Choices = ["Any", on, off] };
        ParameterDescriptor LevelType() => new("levelType", "Meter", ParameterKind.Choice)
        {
            DefaultValue = "Any",
            Choices = ["Any", "Channel", "Mix", "Input", "Output"],
            Help = "Level meters are off until enabled on the Wave Link page (they are throttled to the rate set there)",
        };
        ParameterDescriptor LevelName() => new("levelName", "Meter names") { Help = "Comma-separated channel/mix/device names or ids; leave empty for any" };
        ParameterDescriptor MinLevel() => new("minLevel", "Minimum level (0-100)", ParameterKind.Number) { Help = "Only when the meter is at least this loud; empty for any" };

        bool ChannelMatch(BotEvent e, ParameterValues s) =>
            Steps.MatchesList(s["channel"], e.Text("waveLinkChannelName"), e.Text("waveLinkChannelId"));
        bool MixMatch(BotEvent e, ParameterValues s) =>
            Steps.MatchesList(s["mix"], e.Text("waveLinkMixName"), e.Text("waveLinkMixId"));
        bool InputMatch(BotEvent e, ParameterValues s) =>
            Steps.MatchesList(s["inputDevice"], e.Text("waveLinkInputDeviceName"), e.Text("waveLinkInputDeviceId"), e.Text("waveLinkInputName"));
        bool OutputMatch(BotEvent e, ParameterValues s) =>
            Steps.MatchesList(s["outputDevice"], e.Text("waveLinkOutputDeviceName"), e.Text("waveLinkOutputDeviceId"), e.Text("waveLinkOutputName"));
        static bool StateMatch(BotEvent e, ParameterValues s, string key, string on) => s["stateFilter"] switch
        {
            "Any" or "" => true,
            var v when v == on => e.Flag(key) == true,
            _ => e.Flag(key) == false,
        };

        ITriggerType T(string type, string display, IReadOnlyList<ParameterDescriptor>? parameters = null, Func<BotEvent, ParameterValues, bool>? filter = null) =>
            new WaveLinkTrigger(type, Category, display, [type], parameters, filter is null ? null : (e, s, _) => filter(e, s));

        var triggers = new List<ITriggerType>
        {
            T(E.Connected, "Connected"),
            T(E.Disconnected, "Disconnected"),
            T(E.ChannelEffectsChanged, "Channel effects changed", [Channel()], ChannelMatch),
            T(E.ChannelMuteChanged, "Channel mute changed", [Channel(), Mix(), State("Muted", "Not muted")],
                (e, s) => ChannelMatch(e, s) && MixMatch(e, s) && StateMatch(e, s, "waveLinkMuted", "Muted")),
            T(E.ChannelUpdated, "Channel updated", [Channel()], ChannelMatch),
            T(E.ChannelVolumeChanged, "Channel volume changed", [Channel(), Mix()], (e, s) => ChannelMatch(e, s) && MixMatch(e, s)),
            T(E.ChannelsChanged, "Channels changed"),
            T(E.InputDspEffectsChanged, "DSP effects changed", [InputDevice()], InputMatch),
            T(E.InputDeviceUpdated, "Input device updated", [InputDevice()], InputMatch),
            T(E.InputDevicesChanged, "Input devices changed"),
            T(E.InputGainChanged, "Input gain changed", [InputDevice()], InputMatch),
            T(E.MixUpdated, "Mix updated", [Mix("Comma-separated mix names or ids; leave empty for any")], MixMatch),
            T(E.MixesChanged, "Mixes changed"),
            T(E.MainOutputChanged, "Main output device changed", [OutputDevice()], OutputMatch),
            T(E.OutputDeviceUpdated, "Output device updated", [OutputDevice()], OutputMatch),
            T(E.OutputMixChanged, "Output mix changed", [OutputDevice(), Mix("Comma-separated names or ids of the new mix; leave empty for any")],
                (e, s) => OutputMatch(e, s) && Steps.MatchesList(s["mix"], e.Text("waveLinkOutputMixName"), e.Text("waveLinkOutputMixId"))),
            T(E.CreateProfileRequested, "Create profile requested"),
            T(E.FocusedAppChanged, "Focused app changed",
                [new("app", "Applications") { Help = "Comma-separated application names or ids; leave empty for any" }],
                (e, s) => Steps.MatchesList(s["app"], e.Text("waveLinkAppName"), e.Text("waveLinkAppId"))),
            new WaveLinkTrigger(E.LevelMeter, Category, "Level meter updated", [E.LevelMeter], [LevelType(), LevelName(), MinLevel()],
                (e, s, args) => PickLevel(e, s["levelType"], s["levelName"], s["minLevel"], args)),
        };

        foreach (var (name, typeId, display, events) in LegacyTriggers)
        {
            var legacyName = name;
            IReadOnlyList<ParameterDescriptor> parameters;
            Func<BotEvent, ParameterValues, IDictionary<string, object?>, bool> filter;
            switch (legacyName)
            {
                case L.InputLevelMeterChanged or L.OutputLevelMeterChanged:
                    var kind = legacyName == L.InputLevelMeterChanged ? "Channel" : "Mix";
                    parameters = [LevelName(), MinLevel()];
                    filter = (e, s, args) => e.Text("waveLinkApi") == "legacy" && PickLevel(e, kind, s["levelName"], s["minLevel"], args);
                    break;
                case L.MicrophoneSettingChanged:
                    parameters = [InputDevice()];
                    filter = (e, s, _) => e.Text("waveLinkLegacyEvent").StartsWith("Microphone", StringComparison.Ordinal) && InputMatch(e, s);
                    break;
                case L.InputMuteChanged:
                    parameters = [Channel(), Mix(), State("Muted", "Not muted")];
                    filter = (e, s, _) => Legacy(e, legacyName) && ChannelMatch(e, s) && MixMatch(e, s) && StateMatch(e, s, "waveLinkMuted", "Muted");
                    break;
                case L.InputNameChanged or L.InputVolumeChanged or L.FilterAdded or L.FilterChanged or L.FilterDeleted or L.FilterBypassStateChanged:
                    parameters = [Channel()];
                    filter = (e, s, _) => Legacy(e, legacyName) && ChannelMatch(e, s);
                    break;
                case L.MicrophoneBalanceChanged or L.MicrophoneGainChanged or L.MicrophoneMuteChanged or L.MicrophoneOutputVolumeChanged:
                    parameters = [InputDevice()];
                    filter = (e, s, _) => Legacy(e, legacyName) && InputMatch(e, s);
                    break;
                case L.OutputMuteChanged or L.OutputVolumeChanged:
                    parameters = [Mix("Monitor Mix or Stream Mix; leave empty for both")];
                    filter = (e, s, _) => Legacy(e, legacyName) && MixMatch(e, s);
                    break;
                default:
                    parameters = [];
                    filter = (e, _, _) => Legacy(e, legacyName);
                    break;
            }
            triggers.Add(new WaveLinkTrigger(typeId, LegacyCategory, display, events, parameters, filter));
        }
        return triggers;
    }

    private static bool Legacy(BotEvent e, string name) => e.Text("waveLinkLegacyEvent") == name;

    /// <summary>
    /// Finds the loudest meter in a level event that matches the filters and writes its waveLinkLevel* arguments.
    /// </summary>
    internal static bool PickLevel(BotEvent e, string type, string names, string minLevel, IDictionary<string, object?> args)
    {
        var json = e.Text("waveLinkLevels");
        if (json.Length == 0) return false;
        var wantedType = type.Trim().ToLowerInvariant() is "" or "any" ? "" : type.Trim().ToLowerInvariant();
        var min = double.TryParse(minLevel, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var m) ? m : 0;
        WaveLinkLevel? best = null;
        using var doc = JsonDocument.Parse(json);
        foreach (var item in doc.RootElement.Items())
        {
            var level = new WaveLinkLevel(item.Str("type"), item.Str("id"), item.Str("subId"), item.Str("name"),
                item.Num("left") ?? 0, item.Num("right") ?? 0);
            if (wantedType.Length > 0 && level.Type != wantedType) continue;
            if (!Steps.MatchesList(names, level.Name, level.Id)) continue;
            if (level.Peak < min) continue;
            if (best is null || level.Peak > best.Peak) best = level;
        }
        if (best is null) return false;
        WaveLinkService.LevelArgs(args, best);
        return true;
    }
}
