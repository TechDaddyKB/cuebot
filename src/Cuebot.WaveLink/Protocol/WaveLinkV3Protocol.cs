using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.WaveLink.Common;
using Cuebot.WaveLink.Model;
using E = Cuebot.WaveLink.WaveLinkEvents;
using Event = (string Type, System.Collections.Generic.Dictionary<string, object?> Args);

namespace Cuebot.WaveLink.Protocol;

/// <summary>
/// Wave Link 3.x: getChannels/getMixes/getInputDevices/getOutputDevices, set* with partial objects, and
/// *Changed notifications (lists replace, single items are partial updates merged by id). See PROTOCOL.md.
/// </summary>
internal sealed class WaveLinkV3Protocol(JsonRpcPeer rpc, IWaveLinkHost host) : WaveLinkProtocol(rpc, host)
{
    private JsonArray _channels = [];
    private JsonArray _mixes = [];
    private JsonArray _inputDevices = [];
    private JsonArray _outputDevices = [];
    private JsonObject? _mainOutput;
    private WaveLinkFocusedApp? _focused;
    private bool _initialized;

    public override WaveLinkApi Api => WaveLinkApi.V3;

    public override async Task InitializeAsync(CancellationToken ct)
    {
        var channels = await CallAsync("getChannels", null, ct).ConfigureAwait(false);
        var mixes = await CallAsync("getMixes", null, ct).ConfigureAwait(false);
        var inputs = await CallAsync("getInputDevices", null, ct).ConfigureAwait(false);
        var outputs = await CallAsync("getOutputDevices", null, ct).ConfigureAwait(false);
        lock (Gate)
        {
            _channels = ArrayOf(channels, "channels");
            _mixes = ArrayOf(mixes, "mixes");
            _inputDevices = ArrayOf(inputs, "inputDevices");
            _outputDevices = ArrayOf(outputs, "outputDevices");
            _mainOutput = ObjectOf(outputs, "mainOutput");
            Snapshot = Build();
            _initialized = true;
        }
        await UpdateSubscriptionsAsync(ct).ConfigureAwait(false);
    }

    public override async Task UpdateSubscriptionsAsync(CancellationToken ct)
    {
        var s = Host.Settings;
        var type = s.LevelMeterType.Trim().ToLowerInvariant() is "all" or "any" ? "" : s.LevelMeterType.Trim().ToLowerInvariant();
        var parameters = new JsonObject
        {
            ["focusedAppChanged"] = new JsonObject { ["isEnabled"] = s.FocusedApp },
            ["levelMeterChanged"] = new JsonObject { ["isEnabled"] = s.LevelMeters, ["type"] = type, ["id"] = "all" },
        };
        try
        {
            await CallAsync("setSubscription", parameters, ct).ConfigureAwait(false);
        }
        catch (WaveLinkRequestException ex)
        {
            Host.Log($"Wave Link refused the notification subscription: {ex.Message}");
        }
    }

    private static JsonArray ArrayOf(JsonElement result, string name) =>
        result.Prop(name) is { ValueKind: JsonValueKind.Array } a ? (JsonArray)JsonNode.Parse(a.GetRawText())! : [];

    private static JsonObject? ObjectOf(JsonElement result, string name) =>
        result.Prop(name) is { ValueKind: JsonValueKind.Object } o ? (JsonObject)JsonNode.Parse(o.GetRawText())! : null;

    private static JsonObject? AsObject(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object ? (JsonObject)JsonNode.Parse(e.GetRawText())! : null;

    // ---- Notifications ----

    public override void OnNotification(string method, JsonElement p)
    {
        if (!_initialized) return;
        switch (method)
        {
            case "levelMeterChanged":
                Host.OfferLevels(ParseLevels(p));
                return;
            case "focusedAppChanged":
                OnFocusedApp(p);
                return;
            case "createProfileRequested":
                OnCreateProfile(p);
                return;
        }
        Apply(method, p.ValueKind == JsonValueKind.Object ? AsObject(p) : null, method);
    }

    /// <summary>Applies a notification (or a command's own patch, with the same method name) and publishes what changed.</summary>
    private void Apply(string method, JsonObject? p, string source)
    {
        if (p is null) return;
        var events = new List<Event>();
        lock (Gate)
        {
            var before = Snapshot;
            var listChange = "";
            switch (method)
            {
                case "channelsChanged":
                    _channels = p["channels"] as JsonArray ?? [];
                    listChange = E.ChannelsChanged;
                    break;
                case "channelChanged":
                    MergeItem(_channels, p, "id");
                    break;
                case "mixesChanged":
                    _mixes = p["mixes"] as JsonArray ?? [];
                    listChange = E.MixesChanged;
                    break;
                case "mixChanged":
                    MergeItem(_mixes, p, "id");
                    break;
                case "inputDevicesChanged":
                    _inputDevices = p["inputDevices"] as JsonArray ?? [];
                    listChange = E.InputDevicesChanged;
                    break;
                case "inputDeviceChanged":
                    MergeItem(_inputDevices, p, "id");
                    break;
                case "outputDevicesChanged":
                    if (p["outputDevices"] is JsonArray devices) _outputDevices = devices;
                    if (p["mainOutput"] is JsonObject main) _mainOutput = main;
                    break;
                case "outputDeviceChanged":
                    MergeItem(_outputDevices, p, "id");
                    break;
                case "mainOutputChanged":
                    _mainOutput = p["mainOutput"] as JsonObject ?? p;
                    break;
                default:
                    return;
            }
            // Detach the nodes we keep from the notification's tree.
            _channels = Detach(_channels);
            _mixes = Detach(_mixes);
            _inputDevices = Detach(_inputDevices);
            _outputDevices = Detach(_outputDevices);
            if (_mainOutput is not null) _mainOutput = (JsonObject)_mainOutput.DeepClone();
            Snapshot = Build();
            Diff(before, Snapshot, listChange, source, events);
        }
        Flush(events);
    }

    private static JsonArray Detach(JsonArray array) => array.Parent is null ? array : (JsonArray)array.DeepClone();

    private void OnFocusedApp(JsonElement p)
    {
        var app = new WaveLinkFocusedApp(p.Str("id"), p.Str("name"), p.Prop("channel").Str("id"));
        WaveLinkSnapshot snapshot;
        lock (Gate)
        {
            _focused = app;
            Snapshot = snapshot = Snapshot with { FocusedApp = app };
        }
        var args = WaveLinkArgs.New();
        args["waveLinkEvent"] = "focusedAppChanged";
        args["waveLinkAppId"] = app.Id;
        args["waveLinkAppName"] = app.Name;
        args["waveLinkChannelId"] = app.ChannelId;
        args["waveLinkChannelName"] = snapshot.Channels.FirstOrDefault(c => c.Id == app.ChannelId)?.Name ?? "";
        Flush([(E.FocusedAppChanged, args)]);
    }

    private void OnCreateProfile(JsonElement p)
    {
        var mixes = p.Prop("mixes").Items().Select(m => m.ValueKind == JsonValueKind.String ? m.GetString() ?? "" : m.Str("name")).ToArray();
        var args = WaveLinkArgs.New();
        args["waveLinkEvent"] = "createProfileRequested";
        args["waveLinkDeviceType"] = p.Str("deviceType");
        args["waveLinkProfileMixes"] = string.Join(", ", mixes);
        args["waveLinkProfileMixesCount"] = mixes.Length;
        for (var i = 0; i < mixes.Length; i++) args[$"waveLinkProfileMix.{i}"] = mixes[i];
        Flush([(E.CreateProfileRequested, args)]);
    }

    private List<WaveLinkLevel> ParseLevels(JsonElement p)
    {
        var s = Snapshot;
        var levels = new List<WaveLinkLevel>();
        void Add(string group, string type, Func<string, string> name)
        {
            foreach (var m in p.Prop(group).Items())
            {
                var id = m.Str("id");
                levels.Add(new WaveLinkLevel(type, id, m.Str("subId"), name(id),
                    Math.Round(Math.Clamp(m.Num("levelLeftPercentage") ?? 0, 0, 100), 1),
                    Math.Round(Math.Clamp(m.Num("levelRightPercentage") ?? 0, 0, 100), 1)));
            }
        }
        Add("channels", "channel", id => s.Channels.FirstOrDefault(c => c.Id == id)?.Name ?? "");
        Add("mixes", "mix", id => s.MixById(id)?.Name ?? "");
        Add("inputDevices", "input", id => s.InputDevices.FirstOrDefault(d => d.Id == id)?.Name ?? "");
        Add("outputDevices", "output", id => s.OutputDevices.FirstOrDefault(d => d.Id == id)?.Name ?? "");
        return levels;
    }

    // ---- JSON merging ----

    /// <summary>Arrays merged element-by-element (by their key) instead of replaced.</summary>
    private static string? KeyOf(string property) => property switch
    {
        "mixes" => "mixId",
        "inputs" or "outputs" or "effects" or "dspEffects" => "id",
        _ => null,
    };

    private static void MergeItem(JsonArray list, JsonObject patch, string key)
    {
        var id = patch[key]?.ToString();
        if (string.IsNullOrEmpty(id)) return;
        var target = list.OfType<JsonObject>().FirstOrDefault(o => o[key]?.ToString() == id);
        if (target is null) list.Add(patch.DeepClone());
        else Merge(target, patch);
    }

    internal static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (name, value) in patch)
        {
            if (value is JsonObject po && target[name] is JsonObject to) Merge(to, po);
            else if (value is JsonArray pa && target[name] is JsonArray ta && KeyOf(name) is { } key)
                foreach (var item in pa.OfType<JsonObject>()) MergeItem(ta, item, key);
            else target[name] = value?.DeepClone();
        }
    }

    // ---- Model ----

    private WaveLinkSnapshot Build()
    {
        var mixes = _mixes.OfType<JsonObject>().Select(m => new WaveLinkMix(S(m, "id"), S(m, "name"), B(m, "isMuted"), Pct(m, "level"))).ToArray();
        string MixName(string id) => mixes.FirstOrDefault(m => m.Id == id)?.Name ?? "";
        var channels = _channels.OfType<JsonObject>().Select(c => new WaveLinkChannel(
            S(c, "id"), S(c, "name"), S(c, "type"), B(c, "isMuted"), Pct(c, "level"),
            Arr(c, "mixes").Select(m => new WaveLinkChannelMix(S(m, "mixId"), MixName(S(m, "mixId")), B(m, "isMuted"), Pct(m, "level"))).ToArray(),
            Effects(c, "effects"),
            Arr(c, "apps").Select(a => new WaveLinkApp(S(a, "id"), S(a, "name"))).ToArray())).ToArray();
        var inputDevices = _inputDevices.OfType<JsonObject>().Select(d => new WaveLinkInputDevice(
            S(d, "id"), S(d, "name"), S(d, "type") is { Length: > 0 } t ? t : B(d, "isWaveDevice") ? "Wave" : "", B(d, "isWaveDevice"),
            Arr(d, "inputs").Select(Input).ToArray())).ToArray();
        var outputDevices = _outputDevices.OfType<JsonObject>().Select(d => new WaveLinkOutputDevice(
            S(d, "id"), S(d, "name"), B(d, "isWaveDevice"),
            Arr(d, "outputs").Select(o => new WaveLinkOutput(S(o, "id"), S(o, "name"), B(o, "isMuted"), Pct(o, "level"), S(o, "mixId"))).ToArray())).ToArray();
        var main = _mainOutput is { } mo ? new WaveLinkMainOutput(S(mo, "outputDeviceId"), S(mo, "outputId")) : null;
        var monitored = main is null ? ""
            : outputDevices.FirstOrDefault(d => d.Id == main.OutputDeviceId)?.Outputs.FirstOrDefault(o => o.Id == main.OutputId)?.MixId ?? "";
        return new WaveLinkSnapshot(channels, mixes, inputDevices, outputDevices, main, monitored, _focused);
    }

    private static WaveLinkInput Input(JsonObject i)
    {
        var dsp = Effects(i, "dspEffects");
        var gain = i["gain"] as JsonObject;
        var mix = i["micPcMix"] as JsonObject;
        return new WaveLinkInput(S(i, "id"), S(i, "name"), B(i, "isMuted"),
            gain is null ? null : Pct(gain, "value"), gain?["max"] is JsonValue max && max.TryGetValue<double>(out var m) ? m : null,
            gain?["value"] is JsonValue raw && raw.TryGetValue<double>(out var r) ? r : null,
            i["isGainLockOn"] is JsonValue lockOn && lockOn.TryGetValue<bool>(out var l) ? l : null,
            mix is null ? null : Pct(mix, "value"), null,
            FindDsp(dsp, IsLowCut)?.Enabled, FindDsp(dsp, IsClipGuard)?.Enabled,
            Effects(i, "effects"), dsp);
    }

    private static WaveLinkEffect[] Effects(JsonObject o, string name) =>
        Arr(o, name).Select(e => new WaveLinkEffect(S(e, "id"), S(e, "name"), B(e, "isEnabled"), e["isSupported"] is not JsonValue v || !v.TryGetValue<bool>(out var b) || b)).ToArray();

    private static IEnumerable<JsonObject> Arr(JsonObject o, string name) => (o[name] as JsonArray)?.OfType<JsonObject>() ?? [];

    private static string S(JsonObject o, string name) => o[name] is JsonValue v ? v.ToString() : "";

    private static bool B(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static double Pct(JsonObject o, string name) => o[name] is JsonValue v && v.TryGetValue<double>(out var d) ? Steps.Percent(d) : 0;

    private static string Norm(string s) => new(s.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    internal static bool IsClipGuard(WaveLinkEffect e) => Norm(e.Id + " " + e.Name).Contains("clipguard") || Norm(e.Name).Contains("clip");

    internal static bool IsLowCut(WaveLinkEffect e) => Norm(e.Id).Contains("lowcut") || Norm(e.Name).Contains("lowcut") || Norm(e.Name).Contains("highpass");

    private static WaveLinkEffect? FindDsp(IReadOnlyList<WaveLinkEffect> dsp, Func<WaveLinkEffect, bool> match) => dsp.FirstOrDefault(match);

    // ---- Diff → events ----

    private static void Diff(WaveLinkSnapshot before, WaveLinkSnapshot after, string listChange, string source, List<Event> events)
    {
        Dictionary<string, object?> Args()
        {
            var a = WaveLinkArgs.New();
            a["waveLinkEvent"] = source;
            return a;
        }

        foreach (var c in after.Channels)
        {
            var o = before.Channels.FirstOrDefault(x => x.Id == c.Id);
            if (o is null || Same(o, c)) continue;
            if (o.Muted != c.Muted)
            {
                var a = Args();
                WaveLinkArgs.Channel(a, c, after);
                a["waveLinkScope"] = "channel";
                a["waveLinkMuted"] = c.Muted;
                events.Add((E.ChannelMuteChanged, a));
            }
            if (o.Volume != c.Volume)
            {
                var a = Args();
                WaveLinkArgs.Channel(a, c, after);
                a["waveLinkScope"] = "channel";
                a["waveLinkVolume"] = c.Volume;
                a["waveLinkPreviousVolume"] = o.Volume;
                events.Add((E.ChannelVolumeChanged, a));
            }
            foreach (var m in c.Mixes)
            {
                var om = o.Mix(m.MixId);
                if (om is null) continue;
                if (om.Muted != m.Muted)
                {
                    var a = Args();
                    WaveLinkArgs.Channel(a, c, after);
                    WaveLinkArgs.ChannelMix(a, m, after);
                    a["waveLinkScope"] = "mix";
                    a["waveLinkMuted"] = m.Muted;
                    events.Add((E.ChannelMuteChanged, a));
                }
                if (om.Volume != m.Volume)
                {
                    var a = Args();
                    WaveLinkArgs.Channel(a, c, after);
                    WaveLinkArgs.ChannelMix(a, m, after);
                    a["waveLinkScope"] = "mix";
                    a["waveLinkVolume"] = m.Volume;
                    a["waveLinkPreviousVolume"] = om.Volume;
                    events.Add((E.ChannelVolumeChanged, a));
                }
            }
            if (!o.Effects.SequenceEqual(c.Effects))
            {
                var a = Args();
                WaveLinkArgs.Channel(a, c, after);
                if (c.Effects.FirstOrDefault(e => !o.Effects.Contains(e)) is { } changed)
                {
                    a["waveLinkEffectId"] = changed.Id;
                    a["waveLinkEffectName"] = changed.Name;
                    a["waveLinkEffectEnabled"] = changed.Enabled;
                }
                events.Add((E.ChannelEffectsChanged, a));
            }
            var updated = Args();
            WaveLinkArgs.Channel(updated, c, after);
            events.Add((E.ChannelUpdated, updated));
        }
        if (listChange == E.ChannelsChanged || !Ids(before.Channels, x => x.Id).SetEquals(Ids(after.Channels, x => x.Id)))
            events.Add((E.ChannelsChanged, Count(Args(), "waveLinkChannelsCount", after.Channels.Count)));

        foreach (var m in after.Mixes)
        {
            var o = before.MixById(m.Id);
            if (o is null || o == m) continue;
            var a = Args();
            WaveLinkArgs.Mix(a, m);
            a["waveLinkPreviousVolume"] = o.Volume;
            a["waveLinkMuteChanged"] = o.Muted != m.Muted;
            a["waveLinkVolumeChanged"] = o.Volume != m.Volume;
            events.Add((E.MixUpdated, a));
        }
        if (listChange == E.MixesChanged || !Ids(before.Mixes, x => x.Id).SetEquals(Ids(after.Mixes, x => x.Id)))
            events.Add((E.MixesChanged, Count(Args(), "waveLinkMixesCount", after.Mixes.Count)));

        foreach (var d in after.InputDevices)
        {
            var o = before.InputDevices.FirstOrDefault(x => x.Id == d.Id);
            if (o is null || Same(o, d)) continue;
            var (oi, ni) = (o.Primary, d.Primary);
            if (oi is not null && ni is not null && oi.Gain != ni.Gain)
            {
                var a = Args();
                WaveLinkArgs.InputDevice(a, d);
                a["waveLinkPreviousGain"] = oi.Gain;
                events.Add((E.InputGainChanged, a));
            }
            if (oi is not null && ni is not null && !oi.DspEffects.SequenceEqual(ni.DspEffects))
            {
                var a = Args();
                WaveLinkArgs.InputDevice(a, d);
                if (ni.DspEffects.FirstOrDefault(e => !oi.DspEffects.Contains(e)) is { } changed)
                {
                    a["waveLinkEffectId"] = changed.Id;
                    a["waveLinkEffectName"] = changed.Name;
                    a["waveLinkEffectEnabled"] = changed.Enabled;
                }
                events.Add((E.InputDspEffectsChanged, a));
            }
            var updated = Args();
            WaveLinkArgs.InputDevice(updated, d);
            events.Add((E.InputDeviceUpdated, updated));
        }
        if (listChange == E.InputDevicesChanged || !Ids(before.InputDevices, x => x.Id).SetEquals(Ids(after.InputDevices, x => x.Id)))
            events.Add((E.InputDevicesChanged, Count(Args(), "waveLinkInputDevicesCount", after.InputDevices.Count)));

        foreach (var d in after.OutputDevices)
        {
            var o = before.OutputDevices.FirstOrDefault(x => x.Id == d.Id);
            if (o is null || (o.Name == d.Name && o.Outputs.SequenceEqual(d.Outputs))) continue;
            foreach (var output in d.Outputs)
            {
                var old = o.Outputs.FirstOrDefault(x => x.Id == output.Id);
                if (old == output) continue;
                var a = Args();
                WaveLinkArgs.OutputDevice(a, d, output, after);
                events.Add((E.OutputDeviceUpdated, a));
                if (old is not null && old.MixId != output.MixId)
                {
                    var m = Args();
                    WaveLinkArgs.OutputDevice(m, d, output, after);
                    m["waveLinkPreviousMixId"] = old.MixId;
                    m["waveLinkPreviousMixName"] = after.MixName(old.MixId);
                    events.Add((E.OutputMixChanged, m));
                }
            }
        }
        if (after.MainOutput is { } main && before.MainOutput != main && before.MainOutput is not null)
        {
            var a = Args();
            if (after.MainOutputDevice is { } device)
                WaveLinkArgs.OutputDevice(a, device, device.Outputs.FirstOrDefault(x => x.Id == main.OutputId), after);
            else
            {
                a["waveLinkOutputDeviceId"] = main.OutputDeviceId;
                a["waveLinkOutputId"] = main.OutputId;
            }
            a["waveLinkPreviousOutputDeviceId"] = before.MainOutput.OutputDeviceId;
            events.Add((E.MainOutputChanged, a));
        }
    }

    private static Dictionary<string, object?> Count(Dictionary<string, object?> a, string key, int count)
    {
        a[key] = count;
        return a;
    }

    private static HashSet<string> Ids<T>(IEnumerable<T> items, Func<T, string> id) => items.Select(id).ToHashSet();

    private static bool Same(WaveLinkChannel a, WaveLinkChannel b) =>
        a.Name == b.Name && a.Type == b.Type && a.Muted == b.Muted && a.Volume == b.Volume && a.Available == b.Available
        && a.Mixes.SequenceEqual(b.Mixes) && a.Effects.SequenceEqual(b.Effects) && a.Apps.SequenceEqual(b.Apps);

    private static bool Same(WaveLinkInputDevice a, WaveLinkInputDevice b) =>
        a.Name == b.Name && a.Type == b.Type && a.IsWaveDevice == b.IsWaveDevice && a.Inputs.Count == b.Inputs.Count
        && a.Inputs.Zip(b.Inputs).All(p => Same(p.First, p.Second));

    internal static bool Same(WaveLinkInput a, WaveLinkInput b) =>
        a with { Effects = [], DspEffects = [] } == b with { Effects = [], DspEffects = [] }
        && a.Effects.SequenceEqual(b.Effects) && a.DspEffects.SequenceEqual(b.DspEffects);

    // ---- Commands ----

    private async Task SetAsync(string method, JsonObject parameters, string localMethod, JsonObject localPatch, CancellationToken ct)
    {
        await CallAsync(method, parameters, ct).ConfigureAwait(false);
        Apply(localMethod, localPatch, "local");
    }

    private Task SetChannelAsync(JsonObject patch, CancellationToken ct) => SetAsync("setChannel", patch, "channelChanged", (JsonObject)patch.DeepClone(), ct);

    private static JsonObject MixPatch(string channelId, string mixId, string property, JsonNode value) => new()
    {
        ["id"] = channelId,
        ["mixes"] = new JsonArray(new JsonObject { ["mixId"] = mixId, [property] = value }),
    };

    public override async Task<bool> SetChannelMuteAsync(string channel, string mix, SwitchMode mode, CancellationToken ct)
    {
        var s = Snapshot;
        var c = s.FindChannel(channel);
        if (IsAll(mix))
        {
            var desired = Steps.Desired(mode, c.Muted);
            await SetChannelAsync(new JsonObject { ["id"] = c.Id, ["isMuted"] = desired }, ct).ConfigureAwait(false);
            return desired;
        }
        var m = s.FindMix(mix);
        var wanted = Steps.Desired(mode, c.Mix(m.Id)?.Muted ?? false);
        await SetChannelAsync(MixPatch(c.Id, m.Id, "isMuted", wanted), ct).ConfigureAwait(false);
        return wanted;
    }

    public override async Task<double> SetChannelVolumeAsync(string channel, string mix, double value, bool relative, CancellationToken ct)
    {
        var s = Snapshot;
        var c = s.FindChannel(channel);
        if (IsAll(mix))
        {
            var volume = Apply(c.Volume, value, relative);
            await SetChannelAsync(new JsonObject { ["id"] = c.Id, ["level"] = volume / 100 }, ct).ConfigureAwait(false);
            return volume;
        }
        var m = s.FindMix(mix);
        var level = Apply(c.Mix(m.Id)?.Volume ?? 0, value, relative);
        await SetChannelAsync(MixPatch(c.Id, m.Id, "level", level / 100), ct).ConfigureAwait(false);
        return level;
    }

    public override async Task<bool> SetChannelEffectAsync(string channel, string effect, SwitchMode mode, CancellationToken ct)
    {
        var c = Snapshot.FindChannel(channel);
        var e = WaveLinkSnapshot.Find(c.Effects, effect, x => x.Id, x => x.Name, $"effect on channel '{c.Name}'");
        var desired = Steps.Desired(mode, e.Enabled);
        await SetChannelAsync(new JsonObject
        {
            ["id"] = c.Id,
            ["effects"] = new JsonArray(new JsonObject { ["id"] = e.Id, ["isEnabled"] = desired }),
        }, ct).ConfigureAwait(false);
        return desired;
    }

    public override async Task<WaveLinkApp> AddAppToChannelAsync(string channel, string app, CancellationToken ct)
    {
        var s = Snapshot;
        var c = s.FindChannel(channel);
        WaveLinkApp target;
        if (app.Trim().Length == 0)
        {
            target = s.FocusedApp is { Id.Length: > 0 } f
                ? new WaveLinkApp(f.Id, f.Name)
                : throw new WaveLinkRequestException("Wave Link has not reported a focused application yet; name the application.");
        }
        else
        {
            var known = s.Channels.SelectMany(x => x.Apps).Concat(s.FocusedApp is { } f ? [new WaveLinkApp(f.Id, f.Name)] : []).ToArray();
            target = known.FirstOrDefault(a => a.Id == app.Trim())
                ?? known.FirstOrDefault(a => string.Equals(a.Name, app.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? new WaveLinkApp(app.Trim(), app.Trim());
        }
        await CallAsync("addToChannel", new JsonObject { ["appId"] = target.Id, ["channelId"] = c.Id }, ct).ConfigureAwait(false);
        return target;
    }

    public override async Task<bool> SetMixMuteAsync(string mix, SwitchMode mode, CancellationToken ct)
    {
        var m = Snapshot.FindMix(mix);
        var desired = Steps.Desired(mode, m.Muted);
        var patch = new JsonObject { ["id"] = m.Id, ["isMuted"] = desired };
        await SetAsync("setMix", patch, "mixChanged", (JsonObject)patch.DeepClone(), ct).ConfigureAwait(false);
        return desired;
    }

    public override async Task<double> SetMixVolumeAsync(string mix, double value, bool relative, CancellationToken ct)
    {
        var m = Snapshot.FindMix(mix);
        var volume = Apply(m.Volume, value, relative);
        var patch = new JsonObject { ["id"] = m.Id, ["level"] = volume / 100 };
        await SetAsync("setMix", patch, "mixChanged", (JsonObject)patch.DeepClone(), ct).ConfigureAwait(false);
        return volume;
    }

    private (WaveLinkInputDevice Device, WaveLinkInput Input) ResolveInput(string device)
    {
        var d = Snapshot.FindInputDevice(device);
        var input = d.Inputs.FirstOrDefault(i => i.Id == device.Trim() || string.Equals(i.Name, device.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? d.Primary ?? throw new WaveLinkRequestException($"Input device '{d.Name}' has no inputs.");
        return (d, input);
    }

    private Task SetInputAsync(WaveLinkInputDevice d, JsonObject inputPatch, CancellationToken ct)
    {
        var patch = new JsonObject { ["id"] = d.Id, ["inputs"] = new JsonArray(inputPatch) };
        return SetAsync("setInputDevice", patch, "inputDeviceChanged", (JsonObject)patch.DeepClone(), ct);
    }

    public override async Task<bool> SetInputMuteAsync(string device, SwitchMode mode, CancellationToken ct)
    {
        var (d, i) = ResolveInput(device);
        var desired = Steps.Desired(mode, i.Muted);
        await SetInputAsync(d, new JsonObject { ["id"] = i.Id, ["isMuted"] = desired }, ct).ConfigureAwait(false);
        return desired;
    }

    public override async Task<double> SetInputGainAsync(string device, double value, bool relative, CancellationToken ct)
    {
        var (d, i) = ResolveInput(device);
        var gain = Apply(i.Gain ?? 0, value, relative);
        await SetInputAsync(d, new JsonObject { ["id"] = i.Id, ["gain"] = new JsonObject { ["value"] = gain / 100 } }, ct).ConfigureAwait(false);
        return gain;
    }

    public override async Task<bool> SetInputGainLockAsync(string device, SwitchMode mode, CancellationToken ct)
    {
        var (d, i) = ResolveInput(device);
        var desired = Steps.Desired(mode, i.GainLocked ?? false);
        await SetInputAsync(d, new JsonObject { ["id"] = i.Id, ["isGainLockOn"] = desired }, ct).ConfigureAwait(false);
        return desired;
    }

    public override async Task<double> SetMicPcMixAsync(string device, double value, bool relative, CancellationToken ct)
    {
        var (d, i) = ResolveInput(device);
        var mix = Apply(i.MicPcMix ?? 50, value, relative);
        await SetInputAsync(d, new JsonObject { ["id"] = i.Id, ["micPcMix"] = new JsonObject { ["value"] = mix / 100 } }, ct).ConfigureAwait(false);
        return mix;
    }

    public override Task<bool> SetInputLowCutAsync(string device, SwitchMode mode, CancellationToken ct) => SetDspAsync(device, mode, IsLowCut, "low cut filter", ct);

    public override Task<bool> SetInputClipGuardAsync(string device, SwitchMode mode, CancellationToken ct) => SetDspAsync(device, mode, IsClipGuard, "Clipguard", ct);

    private async Task<bool> SetDspAsync(string device, SwitchMode mode, Func<WaveLinkEffect, bool> match, string what, CancellationToken ct)
    {
        var (d, i) = ResolveInput(device);
        var effect = i.DspEffects.FirstOrDefault(match) ?? throw new WaveLinkRequestException($"Input device '{d.Name}' has no {what}.");
        var desired = Steps.Desired(mode, effect.Enabled);
        await SetInputAsync(d, new JsonObject
        {
            ["id"] = i.Id,
            ["dspEffects"] = new JsonArray(new JsonObject { ["id"] = effect.Id, ["isEnabled"] = desired }),
        }, ct).ConfigureAwait(false);
        return desired;
    }

    private (WaveLinkOutputDevice Device, WaveLinkOutput Output) ResolveOutput(string device)
    {
        var d = Snapshot.FindOutputDevice(device);
        var output = d.Outputs.FirstOrDefault(o => o.Id == device.Trim() || string.Equals(o.Name, device.Trim(), StringComparison.OrdinalIgnoreCase))
            ?? d.Primary ?? throw new WaveLinkRequestException($"Output device '{d.Name}' has no outputs.");
        return (d, output);
    }

    private Task SetOutputAsync(WaveLinkOutputDevice d, JsonObject outputPatch, CancellationToken ct)
    {
        var device = new JsonObject { ["id"] = d.Id, ["outputs"] = new JsonArray(outputPatch) };
        return SetAsync("setOutputDevice", new JsonObject { ["outputDevice"] = device }, "outputDeviceChanged", (JsonObject)device.DeepClone(), ct);
    }

    public override async Task<WaveLinkOutputDevice> SetMainOutputAsync(string device, CancellationToken ct)
    {
        var (d, o) = ResolveOutput(device);
        var main = new JsonObject { ["outputDeviceId"] = d.Id, ["outputId"] = o.Id };
        await SetAsync("setOutputDevice", new JsonObject { ["mainOutput"] = main }, "mainOutputChanged", (JsonObject)main.DeepClone(), ct).ConfigureAwait(false);
        return d;
    }

    public override async Task<double> SetOutputVolumeAsync(string device, double value, bool relative, CancellationToken ct)
    {
        var (d, o) = ResolveOutput(device);
        var volume = Apply(o.Volume, value, relative);
        await SetOutputAsync(d, new JsonObject { ["id"] = o.Id, ["level"] = volume / 100 }, ct).ConfigureAwait(false);
        return volume;
    }

    public override async Task<bool> SetOutputMuteAsync(string device, SwitchMode mode, CancellationToken ct)
    {
        var (d, o) = ResolveOutput(device);
        var desired = Steps.Desired(mode, o.Muted);
        await SetOutputAsync(d, new JsonObject { ["id"] = o.Id, ["isMuted"] = desired }, ct).ConfigureAwait(false);
        return desired;
    }

    public override async Task AddOutputToMixAsync(string device, string mix, CancellationToken ct)
    {
        var (d, o) = ResolveOutput(device);
        var m = Snapshot.FindMix(mix);
        await SetOutputAsync(d, new JsonObject { ["id"] = o.Id, ["mixId"] = m.Id }, ct).ConfigureAwait(false);
    }

    public override async Task<WaveLinkMix> SetMonitoredMixAsync(string mix, CancellationToken ct)
    {
        var s = Snapshot;
        var m = s.FindMix(mix);
        var main = s.MainOutput ?? throw new WaveLinkRequestException("Wave Link has not reported a main output device.");
        var device = s.MainOutputDevice ?? throw new WaveLinkRequestException("The main output device is not in Wave Link's device list.");
        var output = device.Outputs.FirstOrDefault(o => o.Id == main.OutputId) ?? device.Primary
            ?? throw new WaveLinkRequestException($"Output device '{device.Name}' has no outputs.");
        await SetOutputAsync(device, new JsonObject { ["id"] = output.Id, ["mixId"] = m.Id }, ct).ConfigureAwait(false);
        return m;
    }
}
