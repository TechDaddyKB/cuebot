using Cuebot.GoXlr.Model;
using Cuebot.GoXlr.Protocol;

namespace Cuebot.GoXlr;

/// <summary>
/// Bot event types published by <see cref="GoXlrService"/>. Device events come from comparing the utility's status before
/// and after each JSON patch, so a change made on the hardware, in the utility's UI or by Cuebot fires the same event.
/// </summary>
public static class GoXlrEvents
{
    public const string Connected = "goxlr.connected";
    public const string Disconnected = "goxlr.disconnected";
    public const string DeviceConnected = "goxlr.deviceConnected";
    public const string DeviceDisconnected = "goxlr.deviceDisconnected";
    public const string VolumeChanged = "goxlr.volumeChanged";
    public const string FaderAssignmentChanged = "goxlr.faderAssignmentChanged";
    public const string FaderMuteChanged = "goxlr.faderMuteChanged";
    public const string CoughMuteChanged = "goxlr.coughMuteChanged";
    public const string ProfileChanged = "goxlr.profileChanged";
    public const string MicProfileChanged = "goxlr.micProfileChanged";
    public const string EffectStateChanged = "goxlr.effectStateChanged";
    public const string EffectPresetChanged = "goxlr.effectPresetChanged";
    public const string SamplerBankChanged = "goxlr.samplerBankChanged";
    public const string SamplePlaybackChanged = "goxlr.samplePlaybackChanged";
    public const string ButtonPressed = "goxlr.buttonPressed";
    public const string ButtonReleased = "goxlr.buttonReleased";
    public const string RoutingChanged = "goxlr.routingChanged";

    /// <summary>Every event type, for the module test.</summary>
    public static readonly string[] All =
    [
        Connected, Disconnected, DeviceConnected, DeviceDisconnected, VolumeChanged, FaderAssignmentChanged, FaderMuteChanged,
        CoughMuteChanged, ProfileChanged, MicProfileChanged, EffectStateChanged, EffectPresetChanged, SamplerBankChanged,
        SamplePlaybackChanged, ButtonPressed, ButtonReleased, RoutingChanged,
    ];
}

/// <summary>Builds trigger/sub-action arguments (goXlr*).</summary>
internal static class GoXlrArgs
{
    public static Dictionary<string, object?> New() => new(StringComparer.OrdinalIgnoreCase);

    public static Dictionary<string, object?> Device(GoXlrMixer m)
    {
        var a = New();
        a["goXlrSerial"] = m.Serial;
        a["goXlrDeviceType"] = m.DeviceType;
        a["goXlrProfile"] = m.ProfileName;
        a["goXlrMicProfile"] = m.MicProfileName;
        return a;
    }

    /// <summary>Everything "Get status" exposes: per-fader, per-channel, cough, effects and sampler variables.</summary>
    public static void Status(IDictionary<string, object?> a, GoXlrMixer m)
    {
        foreach (var (k, v) in Device(m)) a[k] = v;
        a["goXlrMicType"] = m.MicType;
        foreach (var f in m.Faders.Values)
        {
            a[$"goXlrFader{f.Name}Channel"] = f.Channel;
            a[$"goXlrFader{f.Name}Muted"] = f.Muted;
            a[$"goXlrFader{f.Name}MuteState"] = f.MuteState;
            a[$"goXlrFader{f.Name}MuteFunction"] = f.MuteFunction;
            a[$"goXlrFader{f.Name}Volume"] = GoXlrNames.Percent(m.Volume(f.Channel));
        }
        foreach (var (channel, raw) in m.Volumes)
        {
            a[$"goXlrVolume{channel}"] = GoXlrNames.Percent(raw);
            a[$"goXlrVolume{channel}Raw"] = raw;
        }
        a["goXlrCoughMuted"] = m.Cough.Muted;
        a["goXlrCoughMuteState"] = m.Cough.MuteState;
        a["goXlrCoughIsToggle"] = m.Cough.IsToggle;
        a["goXlrHasEffects"] = m.Effects is not null;
        if (m.Effects is { } e)
        {
            a["goXlrFxEnabled"] = e.FxEnabled;
            a["goXlrMegaphoneEnabled"] = e.MegaphoneEnabled;
            a["goXlrRobotEnabled"] = e.RobotEnabled;
            a["goXlrHardTuneEnabled"] = e.HardTuneEnabled;
            Preset(a, e, e.ActivePreset);
        }
        if (m.Sampler is { } s) a["goXlrBank"] = s.ActiveBank;
    }

    public static void Preset(IDictionary<string, object?> a, GoXlrEffects e, string preset)
    {
        a["goXlrPreset"] = preset;
        a["goXlrPresetNumber"] = preset.StartsWith("Preset", StringComparison.Ordinal) && int.TryParse(preset[6..], out var n) ? n : 0;
        a["goXlrPresetName"] = e.PresetName(preset);
    }
}
