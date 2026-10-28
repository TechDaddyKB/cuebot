using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.WaveLink.Common;
using Cuebot.WaveLink.Model;
using E = Cuebot.WaveLink.WaveLinkEvents;
using L = Cuebot.WaveLink.WaveLinkEvents.Legacy;
using Event = (string Type, System.Collections.Generic.Dictionary<string, object?> Args);

namespace Cuebot.WaveLink.Protocol;

/// <summary>
/// Wave Link 1.x/2.x (interface revisions up to 7): getInputConfigs/getOutputConfig/getMicrophoneConfig/getOutputs/
/// getSwitchState, setInputConfig/setOutputConfig/setMicrophoneConfig/setFilter/setFilterBypass/setSelectedOutput/
/// switchOutput, and notifications such as inputMuteChanged. Inputs become channels with a Monitor and a Stream mix.
/// </summary>
internal sealed class WaveLinkLegacyProtocol(JsonRpcPeer rpc, IWaveLinkHost host) : WaveLinkProtocol(rpc, host)
{
    public const string LocalMixer = "com.elgato.mix.local";
    public const string StreamMixer = "com.elgato.mix.stream";
    public const string AllMixers = "com.elgato.mix.all";
    public const string LocalMixName = "Monitor Mix";
    public const string StreamMixName = "Stream Mix";

    // setMicrophoneConfig / microphoneConfigChanged property names.
    public const string MicGain = "Microphone Gain";
    public const string MicOutputVolume = "Microphone Output Volume";
    public const string MicBalance = "Microphone DirectMonitor Volume";
    public const string MicMute = "Microphone Mute";
    public const string MicLowCut = "Microphone LowCut";
    public const string MicClipGuard = "Microphone ClipGuard";
    public const string MicLowCutType = "XLR Low Cut Type";
    public const string MicGainLock = "Microphone GainLock";

    private readonly List<LegacyInput> _inputs = [];
    private readonly List<LegacyMic> _mics = [];
    private readonly List<(string Id, string Name)> _outputs = [];
    private MixState _local = new(false, 100);
    private MixState _stream = new(false, 100);
    private string _selectedOutput = "";
    private string _switchState = "";
    private bool _initialized;

    public override WaveLinkApi Api => WaveLinkApi.Legacy;

    private sealed class LegacyInput
    {
        public string Id = "";
        public string Name = "";
        public string Type = "";
        public bool Available = true;
        public MixState Local = new(false, 100);
        public MixState Stream = new(false, 100);
        public bool LocalBypass;
        public bool StreamBypass;
        public List<WaveLinkEffect> Filters = [];
    }

    private sealed class LegacyMic
    {
        public string Id = "";
        public string Name = "";
        public bool IsWaveLink;
        public bool IsWaveXlr;
        public double? Gain;
        public double? OutputVolume;
        public double? Balance;
        public bool? LowCut;
        public bool? ClipGuard;
        public bool? GainLocked;
        public bool Muted;
        public string LowCutType = "";
    }

    private readonly record struct MixState(bool Muted, double Volume);

    public override async Task InitializeAsync(CancellationToken ct)
    {
        await ReloadAsync(ct).ConfigureAwait(false);
        _initialized = true;
    }

    private async Task ReloadAsync(CancellationToken ct)
    {
        var mics = await TryCallAsync("getMicrophoneConfig", ct).ConfigureAwait(false);
        var switchState = await TryCallAsync("getSwitchState", ct).ConfigureAwait(false);
        var outputConfig = await TryCallAsync("getOutputConfig", ct).ConfigureAwait(false);
        var outputs = await TryCallAsync("getOutputs", ct).ConfigureAwait(false);
        var inputs = await CallAsync("getInputConfigs", null, ct).ConfigureAwait(false);
        lock (Gate)
        {
            _mics.Clear();
            foreach (var m in mics.Items()) _mics.Add(ParseMic(m));
            _switchState = switchState.Str("value");
            _local = MixOf(outputConfig.Prop("localMixer"));
            _stream = MixOf(outputConfig.Prop("streamMixer"));
            ParseOutputs(outputs);
            _inputs.Clear();
            foreach (var i in inputs.Items()) _inputs.Add(ParseInput(i));
            Snapshot = Build();
        }
    }

    /// <summary>Getters a given Wave Link version may lack (e.g. getOutputs before 1.6); missing data stays empty.</summary>
    private async Task<JsonElement> TryCallAsync(string method, CancellationToken ct)
    {
        try
        {
            return await CallAsync(method, null, ct).ConfigureAwait(false);
        }
        catch (WaveLinkRequestException ex)
        {
            Host.Log($"Wave Link does not answer {method}: {ex.Message}");
            return default;
        }
    }

    private static MixState MixOf(JsonElement pair) => pair.ValueKind == JsonValueKind.Array
        ? new MixState(pair.GetArrayLength() > 0 && pair[0].ValueKind == JsonValueKind.True,
            pair.GetArrayLength() > 1 && pair[1].ValueKind == JsonValueKind.Number ? pair[1].GetDouble() : 100)
        : new MixState(false, 100);

    private static LegacyInput ParseInput(JsonElement i)
    {
        var local = i.Prop("localMixer");
        var stream = i.Prop("streamMixer");
        return new LegacyInput
        {
            Id = i.Str("identifier"),
            Name = i.Str("name"),
            Type = i.Str("inputType"),
            Available = i.Bool("isAvailable") ?? true,
            Local = MixOf(local),
            Stream = MixOf(stream),
            LocalBypass = local.ValueKind == JsonValueKind.Array && local.GetArrayLength() > 2 && local[2].ValueKind == JsonValueKind.True,
            StreamBypass = stream.ValueKind == JsonValueKind.Array && stream.GetArrayLength() > 2 && stream[2].ValueKind == JsonValueKind.True,
            Filters = i.Prop("filters").Items().Select(Filter).ToList(),
        };
    }

    private static WaveLinkEffect Filter(JsonElement f) =>
        new(f.Str("filterID"), f.Str("name"), f.Bool("isActive") ?? false, true, f.Str("pluginID"));

    private static LegacyMic ParseMic(JsonElement m) => new()
    {
        Id = m.Str("identifier"),
        Name = m.Str("name"),
        IsWaveLink = m.Bool("isWaveLink") ?? false,
        IsWaveXlr = m.Bool("isWaveXLR") ?? false,
        Gain = m.Num("gain"),
        OutputVolume = m.Num("outputVolume"),
        Balance = m.Num("balance"),
        LowCut = m.Bool("isLowCutOn"),
        ClipGuard = m.Bool("isClipGuardOn"),
        GainLocked = m.Bool("isGainLocked"),
        Muted = m.Bool("isMicMuted") ?? false,
        LowCutType = m.Str("lowCutType"),
    };

    private void ParseOutputs(JsonElement result)
    {
        _outputs.Clear();
        var outputs = result.Prop("outputs");
        // Interface revision 7 splits the lists per mixer; older ones send one list.
        var list = outputs.ValueKind == JsonValueKind.Object ? outputs.Prop("localMixer") : outputs;
        foreach (var o in list.Items()) _outputs.Add((o.Str("identifier"), o.Str("name")));
        var selected = result.Prop("selectedOutput");
        _selectedOutput = selected.ValueKind == JsonValueKind.Object ? selected.Str("localMixer") : result.Str("selectedOutput");
    }

    // ---- Model ----

    private WaveLinkSnapshot Build()
    {
        var mixes = new[] { new WaveLinkMix(LocalMixer, LocalMixName, _local.Muted, _local.Volume), new WaveLinkMix(StreamMixer, StreamMixName, _stream.Muted, _stream.Volume) };
        var channels = _inputs.Select(i => new WaveLinkChannel(i.Id, i.Name, i.Type, i.Local.Muted && i.Stream.Muted, i.Local.Volume,
            [
                new WaveLinkChannelMix(LocalMixer, LocalMixName, i.Local.Muted, i.Local.Volume, i.LocalBypass),
                new WaveLinkChannelMix(StreamMixer, StreamMixName, i.Stream.Muted, i.Stream.Volume, i.StreamBypass),
            ],
            i.Filters.ToArray(), [], i.Available)).ToArray();
        var devices = _mics.Select(m => new WaveLinkInputDevice(m.Id, m.Name, m.IsWaveXlr ? "Wave XLR" : m.IsWaveLink ? "Wave" : "", m.IsWaveLink || m.IsWaveXlr,
            [new WaveLinkInput(m.Id, m.Name, m.Muted, m.Gain, null, m.Gain, m.GainLocked, m.Balance is { } b ? Math.Round(b * 100, 1) : null,
                m.OutputVolume, m.LowCut, m.ClipGuard, [], [])])).ToArray();
        var outputs = _outputs.Select(o => new WaveLinkOutputDevice(o.Id, o.Name, false, [new WaveLinkOutput(o.Id, o.Name, false, 100, LocalMixer)])).ToArray();
        var main = _selectedOutput.Length > 0 ? new WaveLinkMainOutput(_selectedOutput, _selectedOutput) : null;
        return new WaveLinkSnapshot(channels, mixes, devices, outputs, main, MonitoredMix(_switchState), null);
    }

    /// <summary>getSwitchState / outputSwitched values ("LocalMix" / "StreamMix") as a mix id.</summary>
    internal static string MonitoredMix(string switchState) =>
        switchState.Length == 0 ? "" : switchState.Contains("stream", StringComparison.OrdinalIgnoreCase) ? StreamMixer : LocalMixer;

    private static string MixerName(string mixerId) => mixerId == StreamMixer ? "Stream" : mixerId == LocalMixer ? "Local" : "All";

    // ---- Notifications ----

    public override void OnNotification(string method, JsonElement p)
    {
        if (!_initialized) return;
        if (method == "realTimeChanges")
        {
            Host.OfferLevels(ParseLevels(p));
            return;
        }
        if (method is "inputsChanged" or "profileChanged")
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await ReloadAsync(CancellationToken.None).ConfigureAwait(false);
                    var args = Args(method, null);
                    args["waveLinkChannelsCount"] = Snapshot.Channels.Count;
                    Flush([(E.ChannelsChanged, args)]);
                }
                catch (Exception ex)
                {
                    Host.Log($"Could not reload Wave Link inputs: {ex.Message}");
                }
            });
            return;
        }
        Apply(method, p, method);
    }

    private static Dictionary<string, object?> Args(string source, string? legacyEvent)
    {
        var a = WaveLinkArgs.New();
        a["waveLinkEvent"] = source;
        if (legacyEvent is not null) a["waveLinkLegacyEvent"] = legacyEvent;
        return a;
    }

    /// <summary>Applies a notification, or a command's own change written in the notification's shape.</summary>
    private void Apply(string method, JsonElement p, string source)
    {
        var events = new List<Event>();
        lock (Gate)
        {
            var id = p.Str("identifier");
            var mixer = p.Str("mixerID");
            var input = _inputs.FirstOrDefault(i => i.Id == id);
            switch (method)
            {
                case "inputMuteChanged" when input is not null:
                    var muted = p.Bool("value") ?? false;
                    if ((mixer == StreamMixer ? input.Stream.Muted : input.Local.Muted) == muted) break;
                    if (mixer == StreamMixer) input.Stream = input.Stream with { Muted = muted };
                    else input.Local = input.Local with { Muted = muted };
                    events.Add((E.ChannelMuteChanged, InputArgs(source, L.InputMuteChanged, input, mixer, a => a["waveLinkMuted"] = muted)));
                    break;
                case "inputVolumeChanged" when input is not null:
                    var volume = p.Num("value") ?? 0;
                    var before = mixer == StreamMixer ? input.Stream.Volume : input.Local.Volume;
                    if (before == volume) break;
                    if (mixer == StreamMixer) input.Stream = input.Stream with { Volume = volume };
                    else input.Local = input.Local with { Volume = volume };
                    events.Add((E.ChannelVolumeChanged, InputArgs(source, L.InputVolumeChanged, input, mixer, a =>
                    {
                        a["waveLinkVolume"] = volume;
                        a["waveLinkPreviousVolume"] = before;
                    })));
                    break;
                case "inputNameChanged" when input is not null:
                    var oldName = input.Name;
                    input.Name = p.Str("value");
                    events.Add((E.ChannelUpdated, InputArgs(source, L.InputNameChanged, input, "", a => a["waveLinkPreviousName"] = oldName)));
                    break;
                case "inputEnabled" or "inputDisabled" when input is not null:
                    input.Available = method == "inputEnabled";
                    events.Add((E.ChannelUpdated, InputArgs(source, null, input, "", a => a["waveLinkAvailable"] = input.Available)));
                    break;
                case "filterBypassStateChanged" when input is not null:
                    var bypassed = p.Bool("value") ?? false;
                    if ((mixer == StreamMixer ? input.StreamBypass : input.LocalBypass) == bypassed) break;
                    if (mixer == StreamMixer) input.StreamBypass = bypassed;
                    else input.LocalBypass = bypassed;
                    events.Add((E.ChannelEffectsChanged, InputArgs(source, L.FilterBypassStateChanged, input, mixer, a => a["waveLinkFilterBypassed"] = bypassed)));
                    break;
                case "filterAdded" when input is not null:
                    var added = Filter(p);
                    input.Filters.RemoveAll(f => f.Id == added.Id);
                    input.Filters.Add(added);
                    events.Add((E.ChannelEffectsChanged, InputArgs(source, L.FilterAdded, input, "", a => FilterArgs(a, added))));
                    break;
                case "filterChanged" when input is not null:
                    var index = input.Filters.FindIndex(f => f.Id == p.Str("filterID"));
                    if (index < 0 || input.Filters[index].Enabled == (p.Bool("value") ?? false)) break;
                    var changed = input.Filters[index] with { Enabled = p.Bool("value") ?? false };
                    input.Filters[index] = changed;
                    events.Add((E.ChannelEffectsChanged, InputArgs(source, L.FilterChanged, input, "", a => FilterArgs(a, changed))));
                    break;
                case "filterRemoved" when input is not null:
                    var removed = input.Filters.FirstOrDefault(f => f.Id == p.Str("filterID"));
                    if (removed is null) break;
                    input.Filters.Remove(removed);
                    events.Add((E.ChannelEffectsChanged, InputArgs(source, L.FilterDeleted, input, "", a => FilterArgs(a, removed))));
                    break;
                case "microphoneConfigChanged":
                    OnMicrophone(p, source, events);
                    break;
                case "outputMuteChanged":
                    var outMuted = p.Bool("value") ?? false;
                    if ((mixer == StreamMixer ? _stream.Muted : _local.Muted) == outMuted) break;
                    if (mixer == StreamMixer) _stream = _stream with { Muted = outMuted };
                    else _local = _local with { Muted = outMuted };
                    events.Add((E.MixUpdated, MixArgs(source, L.OutputMuteChanged, mixer, a =>
                    {
                        a["waveLinkMuteChanged"] = true;
                        a["waveLinkVolumeChanged"] = false;
                    })));
                    break;
                case "outputVolumeChanged":
                    var outVolume = p.Num("value") ?? 0;
                    var previous = mixer == StreamMixer ? _stream.Volume : _local.Volume;
                    if (previous == outVolume) break;
                    if (mixer == StreamMixer) _stream = _stream with { Volume = outVolume };
                    else _local = _local with { Volume = outVolume };
                    events.Add((E.MixUpdated, MixArgs(source, L.OutputVolumeChanged, mixer, a =>
                    {
                        a["waveLinkPreviousVolume"] = previous;
                        a["waveLinkMuteChanged"] = false;
                        a["waveLinkVolumeChanged"] = true;
                    })));
                    break;
                case "outputSwitched":
                    var oldMix = MonitoredMix(_switchState);
                    var newMix = MonitoredMix(p.Str("value"));
                    if (newMix == oldMix) break;
                    _switchState = p.Str("value");
                    var switched = Args(source, L.OutputSwitched);
                    var mainDevice = _outputs.FirstOrDefault(o => o.Id == _selectedOutput);
                    switched["waveLinkOutputDeviceId"] = _selectedOutput;
                    switched["waveLinkOutputDeviceName"] = mainDevice.Name ?? "";
                    switched["waveLinkOutputMixId"] = newMix;
                    switched["waveLinkOutputMixName"] = newMix == StreamMixer ? StreamMixName : LocalMixName;
                    switched["waveLinkPreviousMixId"] = oldMix;
                    switched["switchState"] = _switchState;
                    events.Add((E.OutputMixChanged, switched));
                    break;
                case "selectedOutputChanged":
                    // Revision 7 sends the output id as identifier (with mixerID); older ones send it as value.
                    var selected = p.Str("identifier") is { Length: > 0 } ident ? ident : p.Str("value");
                    // The stream mixer's output is not the monitoring device.
                    if (mixer == StreamMixer || selected == _selectedOutput) break;
                    var oldSelected = _selectedOutput;
                    _selectedOutput = selected;
                    var output = _outputs.FirstOrDefault(o => o.Id == selected);
                    var sel = Args(source, L.SelectedOutputChanged);
                    sel["waveLinkOutputDeviceId"] = selected;
                    sel["waveLinkOutputDeviceName"] = output.Name ?? p.Str("name");
                    sel["waveLinkOutputId"] = selected;
                    sel["waveLinkPreviousOutputDeviceId"] = oldSelected;
                    sel["output.identifier"] = selected;
                    sel["output.name"] = output.Name ?? p.Str("name");
                    events.Add((E.MainOutputChanged, sel));
                    break;
                default:
                    return;
            }
            Snapshot = Build();
        }
        Flush(events);
    }

    private static void FilterArgs(Dictionary<string, object?> a, WaveLinkEffect f)
    {
        a["waveLinkEffectId"] = f.Id;
        a["waveLinkEffectName"] = f.Name;
        a["waveLinkEffectEnabled"] = f.Enabled;
        a["filter.id"] = f.Id;
        a["filter.name"] = f.Name;
        a["filter.active"] = f.Enabled;
        a["filter.pluginId"] = f.PluginId;
    }

    private Dictionary<string, object?> InputArgs(string source, string? legacyEvent, LegacyInput input, string mixer, Action<Dictionary<string, object?>> extra)
    {
        var a = Args(source, legacyEvent);
        var snapshot = Build();
        var channel = snapshot.Channels.First(c => c.Id == input.Id);
        WaveLinkArgs.Channel(a, channel, snapshot);
        if (mixer.Length > 0 && channel.Mix(mixer) is { } mix)
        {
            WaveLinkArgs.ChannelMix(a, mix, snapshot);
            a["waveLinkScope"] = "mix";
        }
        else a["waveLinkScope"] = "channel";
        // Streamer.bot's legacy variable names (as in Get Input Information).
        a["input.identifier"] = input.Id;
        a["input.name"] = input.Name;
        a["input.local.muted"] = input.Local.Muted;
        a["input.local.volume"] = (int)Math.Round(input.Local.Volume);
        a["input.local.filterBypass"] = input.LocalBypass;
        a["input.stream.muted"] = input.Stream.Muted;
        a["input.stream.volume"] = (int)Math.Round(input.Stream.Volume);
        a["input.stream.filterBypass"] = input.StreamBypass;
        if (mixer.Length > 0)
        {
            a["mixerId"] = mixer;
            a["mixer"] = MixerName(mixer);
        }
        extra(a);
        return a;
    }

    private Dictionary<string, object?> MixArgs(string source, string legacyEvent, string mixer, Action<Dictionary<string, object?>> extra)
    {
        var a = Args(source, legacyEvent);
        var mix = Build().MixById(mixer == StreamMixer ? StreamMixer : LocalMixer)!;
        WaveLinkArgs.Mix(a, mix);
        a["mixerId"] = mix.Id;
        a["mixer"] = MixerName(mix.Id);
        a["localVolume"] = (int)Math.Round(_local.Volume);
        a["streamVolume"] = (int)Math.Round(_stream.Volume);
        extra(a);
        return a;
    }

    private void OnMicrophone(JsonElement p, string source, List<Event> events)
    {
        var mic = _mics.FirstOrDefault(m => m.Id == p.Str("identifier")) ?? _mics.FirstOrDefault();
        if (mic is null) return;
        var property = p.Str("property");
        var value = p.Prop("value");
        if (value.ValueKind == JsonValueKind.Undefined) value = p.Prop("boolValue");
        var previous = Build().InputDevices.First(d => d.Id == mic.Id).Primary!;
        var previousLowCutType = mic.LowCutType;
        bool Bool() => value.ValueKind == JsonValueKind.True || (value.ValueKind == JsonValueKind.Number && value.GetDouble() != 0);
        double? Num() => value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
        string legacy;
        var type = E.InputDeviceUpdated;
        switch (property)
        {
            case MicGain:
                mic.Gain = Num() ?? mic.Gain;
                legacy = L.MicrophoneGainChanged;
                type = E.InputGainChanged;
                break;
            case MicOutputVolume:
                mic.OutputVolume = Num() ?? mic.OutputVolume;
                legacy = L.MicrophoneOutputVolumeChanged;
                break;
            case MicBalance:
                mic.Balance = Num() ?? mic.Balance;
                legacy = L.MicrophoneBalanceChanged;
                break;
            case MicMute:
                mic.Muted = Bool();
                legacy = L.MicrophoneMuteChanged;
                break;
            case MicLowCut:
                mic.LowCut = Bool();
                legacy = L.MicrophoneSettingChanged;
                type = E.InputDspEffectsChanged;
                break;
            case MicClipGuard:
                mic.ClipGuard = Bool();
                legacy = L.MicrophoneSettingChanged;
                type = E.InputDspEffectsChanged;
                break;
            case MicGainLock:
                mic.GainLocked = Bool();
                legacy = L.MicrophoneSettingChanged;
                break;
            case MicLowCutType:
                mic.LowCutType = value.ToString();
                legacy = L.MicrophoneSettingChanged;
                break;
            default:
                legacy = L.MicrophoneSettingChanged;
                break;
        }
        var device = Build().InputDevices.First(d => d.Id == mic.Id);
        if (WaveLinkV3Protocol.Same(previous, device.Primary!) && previousLowCutType == mic.LowCutType
            && property is MicGain or MicOutputVolume or MicBalance or MicMute or MicLowCut or MicClipGuard or MicGainLock or MicLowCutType) return;
        var a = Args(source, legacy);
        WaveLinkArgs.InputDevice(a, device);
        a["waveLinkMicrophoneProperty"] = property;
        a["waveLinkMicrophoneValue"] = value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.Undefined or JsonValueKind.Null => null,
            _ => value.ToString(),
        };
        MicArgs(a, mic);
        events.Add((type, a));
    }

    private static void MicArgs(Dictionary<string, object?> a, LegacyMic mic)
    {
        a["microphone.identifier"] = mic.Id;
        a["microphone.name"] = mic.Name;
        a["microphone.gain"] = mic.Gain;
        a["microphone.outputVolume"] = mic.OutputVolume;
        a["microphone.balance"] = mic.Balance is { } b ? Math.Round(b * 100, 1) : null;
        a["microphone.lowCut"] = mic.LowCut;
        a["microphone.clipGuard"] = mic.ClipGuard;
        a["microphone.gainLocked"] = mic.GainLocked;
        a["microphone.muted"] = mic.Muted;
    }

    /// <summary>Microphone variables for "Get Microphone Information".</summary>
    internal void AddMicrophoneArgs(Dictionary<string, object?> a, string microphone)
    {
        lock (Gate)
        {
            var device = Snapshot.FindInputDevice(microphone);
            if (_mics.FirstOrDefault(m => m.Id == device.Id) is { } mic) MicArgs(a, mic);
        }
    }

    private List<WaveLinkLevel> ParseLevels(JsonElement p)
    {
        var levels = new List<WaveLinkLevel>();
        var s = Snapshot;
        // Interface revision 7 calls the list mixerList; 6 called it MixerList.
        var list = p.Prop("mixerList") is { ValueKind: JsonValueKind.Array } l ? l : p.Prop("MixerList");
        static double Pct(JsonElement side, string name) => Steps.Percent(side.Num(name) ?? 0);
        foreach (var input in list.Items())
        {
            var id = input.Str("identifier");
            var name = s.Channels.FirstOrDefault(c => c.Id == id)?.Name ?? "";
            foreach (var (mixer, sub) in new[] { ("localMixer", LocalMixer), ("streamMixer", StreamMixer) })
                if (input.Prop(mixer) is { ValueKind: JsonValueKind.Object } side)
                    levels.Add(new WaveLinkLevel("channel", id, sub, name, Pct(side, "levelLeft"), Pct(side, "levelRight")));
        }
        foreach (var (mixer, id, name) in new[] { ("localMixer", LocalMixer, LocalMixName), ("streamMixer", StreamMixer, StreamMixName) })
            if (p.Prop(mixer) is { ValueKind: JsonValueKind.Object } side)
                levels.Add(new WaveLinkLevel("mix", id, "", name, Pct(side, "levelLeft"), Pct(side, "levelRight")));
        if (p.Prop("microphoneLevel") is { ValueKind: JsonValueKind.Object } mic && s.InputDevices.FirstOrDefault() is { } device)
            levels.Add(new WaveLinkLevel("input", device.Id, "", device.Name, Pct(mic, "levelLeft"), Pct(mic, "levelRight")));
        return levels;
    }

    // ---- Commands ----

    private static string ResolveMixer(string mix, WaveLinkSnapshot s)
    {
        var text = mix.Trim();
        if (text.Length == 0 || text.Equals("both", StringComparison.OrdinalIgnoreCase) || text.Equals("all", StringComparison.OrdinalIgnoreCase)
            || text == AllMixers) return "";
        var lower = text.ToLowerInvariant();
        if (lower.Contains("stream")) return StreamMixer;
        if (lower.Contains("local") || lower.Contains("monitor")) return LocalMixer;
        return s.FindMix(text).Id;
    }

    private async Task SetAsync(string method, JsonObject parameters, string notification, JsonObject local, CancellationToken ct)
    {
        await CallAsync(method, parameters, ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(local.ToJsonString());
        Apply(notification, doc.RootElement.Clone(), "local");
    }

    private Task SetInputConfigAsync(WaveLinkChannel c, string mixer, string property, JsonNode value, string notification, CancellationToken ct) =>
        SetAsync("setInputConfig",
            new JsonObject { ["property"] = property, ["identifier"] = c.Id, ["mixerID"] = mixer, ["value"] = value, ["forceLink"] = false },
            notification, new JsonObject { ["identifier"] = c.Id, ["mixerID"] = mixer, ["value"] = value.DeepClone() }, ct);

    public override async Task<bool> SetChannelMuteAsync(string channel, string mix, SwitchMode mode, CancellationToken ct)
    {
        var s = Snapshot;
        var c = s.FindChannel(channel);
        var mixer = ResolveMixer(mix, s);
        var desired = Steps.Desired(mode, mixer.Length == 0 ? c.Muted : c.Mix(mixer)!.Muted);
        foreach (var target in mixer.Length == 0 ? [LocalMixer, StreamMixer] : new[] { mixer })
            await SetInputConfigAsync(c, target, "Mute", desired, "inputMuteChanged", ct).ConfigureAwait(false);
        return desired;
    }

    public override async Task<double> SetChannelVolumeAsync(string channel, string mix, double value, bool relative, CancellationToken ct)
    {
        var s = Snapshot;
        var c = s.FindChannel(channel);
        var mixer = ResolveMixer(mix, s);
        double result = 0;
        foreach (var target in mixer.Length == 0 ? [LocalMixer, StreamMixer] : new[] { mixer })
        {
            result = Math.Round(Apply(c.Mix(target)!.Volume, value, relative));
            await SetInputConfigAsync(c, target, "Volume", (int)result, "inputVolumeChanged", ct).ConfigureAwait(false);
        }
        return result;
    }

    public override async Task<bool> SetChannelEffectAsync(string channel, string effect, SwitchMode mode, CancellationToken ct)
    {
        var c = Snapshot.FindChannel(channel);
        var f = WaveLinkSnapshot.Find(c.Effects, effect, x => x.Id, x => x.Name, $"filter on input '{c.Name}'");
        var desired = Steps.Desired(mode, f.Enabled);
        await SetAsync("setFilter", new JsonObject { ["identifier"] = c.Id, ["filterID"] = f.Id, ["value"] = desired },
            "filterChanged", new JsonObject { ["identifier"] = c.Id, ["filterID"] = f.Id, ["value"] = desired }, ct).ConfigureAwait(false);
        return desired;
    }

    public override async Task<bool> SetFilterBypassAsync(string channel, string mix, SwitchMode mode, CancellationToken ct)
    {
        var s = Snapshot;
        var c = s.FindChannel(channel);
        var mixer = ResolveMixer(mix, s);
        var current = mixer.Length == 0 ? c.Mixes.All(m => m.FilterBypassed) : c.Mix(mixer)!.FilterBypassed;
        var desired = Steps.Desired(mode, current);
        foreach (var target in mixer.Length == 0 ? [LocalMixer, StreamMixer] : new[] { mixer })
            await SetAsync("setFilterBypass", new JsonObject { ["identifier"] = c.Id, ["mixerID"] = target, ["value"] = desired },
                "filterBypassStateChanged", new JsonObject { ["identifier"] = c.Id, ["mixerID"] = target, ["value"] = desired }, ct).ConfigureAwait(false);
        return desired;
    }

    private string RequireMixer(string mix)
    {
        var mixer = ResolveMixer(mix, Snapshot);
        return mixer.Length > 0 ? mixer : throw new ArgumentException("Choose the Monitor (local) or the Stream mix.");
    }

    public override async Task<bool> SetMixMuteAsync(string mix, SwitchMode mode, CancellationToken ct)
    {
        var mixer = RequireMixer(mix);
        var desired = Steps.Desired(mode, Snapshot.MixById(mixer)!.Muted);
        await SetAsync("setOutputConfig", new JsonObject { ["property"] = "Output Mute", ["mixerID"] = mixer, ["value"] = desired, ["forceLink"] = false },
            "outputMuteChanged", new JsonObject { ["mixerID"] = mixer, ["value"] = desired }, ct).ConfigureAwait(false);
        return desired;
    }

    public override async Task<double> SetMixVolumeAsync(string mix, double value, bool relative, CancellationToken ct)
    {
        var mixer = RequireMixer(mix);
        var volume = Math.Round(Apply(Snapshot.MixById(mixer)!.Volume, value, relative));
        await SetAsync("setOutputConfig", new JsonObject { ["property"] = "Output Level", ["mixerID"] = mixer, ["value"] = (int)volume, ["forceLink"] = false },
            "outputVolumeChanged", new JsonObject { ["mixerID"] = mixer, ["value"] = (int)volume }, ct).ConfigureAwait(false);
        return volume;
    }

    private async Task SetMicAsync(WaveLinkInputDevice d, string property, JsonNode value, bool isBool, CancellationToken ct) =>
        await SetAsync("setMicrophoneConfig",
            new JsonObject { ["identifier"] = d.Id, ["property"] = property, [isBool ? "boolValue" : "value"] = value },
            "microphoneConfigChanged", new JsonObject { ["identifier"] = d.Id, ["property"] = property, ["value"] = value.DeepClone() }, ct).ConfigureAwait(false);

    public override async Task<bool> SetInputMuteAsync(string device, SwitchMode mode, CancellationToken ct)
    {
        var d = Snapshot.FindInputDevice(device);
        var desired = Steps.Desired(mode, d.Primary?.Muted ?? false);
        await SetMicAsync(d, MicMute, desired, true, ct).ConfigureAwait(false);
        return desired;
    }

    public override async Task<double> SetInputGainAsync(string device, double value, bool relative, CancellationToken ct)
    {
        var d = Snapshot.FindInputDevice(device);
        var gain = relative ? (d.Primary?.Gain ?? 0) + value : value;
        await SetMicAsync(d, MicGain, gain, false, ct).ConfigureAwait(false);
        return gain;
    }

    public override async Task<double> SetMicOutputVolumeAsync(string device, double value, bool relative, CancellationToken ct)
    {
        var d = Snapshot.FindInputDevice(device);
        var volume = relative ? (d.Primary?.OutputVolume ?? 0) + value : value;
        await SetMicAsync(d, MicOutputVolume, volume, false, ct).ConfigureAwait(false);
        return volume;
    }

    public override async Task<double> SetMicPcMixAsync(string device, double value, bool relative, CancellationToken ct)
    {
        var d = Snapshot.FindInputDevice(device);
        var balance = Apply(d.Primary?.MicPcMix ?? 50, value, relative);
        await SetMicAsync(d, MicBalance, balance / 100, false, ct).ConfigureAwait(false);
        return balance;
    }

    public override async Task<bool> SetInputGainLockAsync(string device, SwitchMode mode, CancellationToken ct)
    {
        var d = Snapshot.FindInputDevice(device);
        var desired = Steps.Desired(mode, d.Primary?.GainLocked ?? false);
        await SetMicAsync(d, MicGainLock, desired, true, ct).ConfigureAwait(false);
        return desired;
    }

    public override async Task<bool> SetInputLowCutAsync(string device, SwitchMode mode, CancellationToken ct)
    {
        var d = Snapshot.FindInputDevice(device);
        var desired = Steps.Desired(mode, d.Primary?.LowCut ?? false);
        await SetMicAsync(d, MicLowCut, desired, true, ct).ConfigureAwait(false);
        return desired;
    }

    public override async Task<bool> SetInputClipGuardAsync(string device, SwitchMode mode, CancellationToken ct)
    {
        var d = Snapshot.FindInputDevice(device);
        var desired = Steps.Desired(mode, d.Primary?.ClipGuard ?? false);
        await SetMicAsync(d, MicClipGuard, desired, true, ct).ConfigureAwait(false);
        return desired;
    }

    public override async Task<WaveLinkOutputDevice> SetMainOutputAsync(string device, CancellationToken ct)
    {
        var d = Snapshot.FindOutputDevice(device);
        await SetAsync("setSelectedOutput", new JsonObject { ["identifier"] = d.Id, ["name"] = d.Name, ["mixerID"] = LocalMixer },
            "selectedOutputChanged", new JsonObject { ["identifier"] = d.Id, ["name"] = d.Name, ["mixerID"] = LocalMixer }, ct).ConfigureAwait(false);
        return d;
    }

    public override async Task<WaveLinkMix> SetMonitoredMixAsync(string mix, CancellationToken ct)
    {
        var mixer = RequireMixer(mix);
        var target = Snapshot.MixById(mixer)!;
        if (Snapshot.MonitoredMixId == mixer) return target;
        // Wave Link 1.x/2.x only toggles between the two mixes.
        await SetAsync("switchOutput", new JsonObject(), "outputSwitched",
            new JsonObject { ["value"] = mixer == StreamMixer ? "StreamMix" : "LocalMix" }, ct).ConfigureAwait(false);
        return target;
    }

    /// <summary>Toggles monitoring between the Monitor and the Stream mix.</summary>
    public async Task<WaveLinkMix> SwitchMonitoringAsync(CancellationToken ct)
    {
        var next = Snapshot.MonitoredMixId == StreamMixer ? LocalMixer : StreamMixer;
        return await SetMonitoredMixAsync(next, ct).ConfigureAwait(false);
    }
}
