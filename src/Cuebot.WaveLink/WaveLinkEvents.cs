using Cuebot.WaveLink.Model;

namespace Cuebot.WaveLink;

/// <summary>
/// Bot event types published by <see cref="WaveLinkService"/>. Both API generations publish the same types where the
/// change means the same thing (a Wave Link 1.x/2.x input mute is a channel mute in a mix); Wave Link 1.x/2.x events
/// also carry <c>waveLinkLegacyEvent</c> (Streamer.bot's legacy event name) so the legacy triggers can tell them apart.
/// </summary>
public static class WaveLinkEvents
{
    public const string Connected = "wavelink.connected";
    public const string Disconnected = "wavelink.disconnected";
    public const string ChannelsChanged = "wavelink.channelsChanged";
    public const string ChannelUpdated = "wavelink.channelUpdated";
    public const string ChannelMuteChanged = "wavelink.channelMuteChanged";
    public const string ChannelVolumeChanged = "wavelink.channelVolumeChanged";
    public const string ChannelEffectsChanged = "wavelink.channelEffectsChanged";
    public const string InputDevicesChanged = "wavelink.inputDevicesChanged";
    public const string InputDeviceUpdated = "wavelink.inputDeviceUpdated";
    public const string InputGainChanged = "wavelink.inputGainChanged";
    public const string InputDspEffectsChanged = "wavelink.inputDspEffectsChanged";
    public const string MixesChanged = "wavelink.mixesChanged";
    public const string MixUpdated = "wavelink.mixUpdated";
    public const string MainOutputChanged = "wavelink.mainOutputChanged";
    public const string OutputDeviceUpdated = "wavelink.outputDeviceUpdated";
    public const string OutputMixChanged = "wavelink.outputMixChanged";
    public const string CreateProfileRequested = "wavelink.createProfileRequested";
    public const string FocusedAppChanged = "wavelink.focusedAppChanged";
    public const string LevelMeter = "wavelink.levelMeter";

    /// <summary>Streamer.bot's Wave Link 1.x/2.x event names (values of <c>waveLinkLegacyEvent</c>).</summary>
    public static class Legacy
    {
        public const string FilterAdded = "FilterAdded";
        public const string FilterBypassStateChanged = "FilterBypassStateChanged";
        public const string FilterChanged = "FilterChanged";
        public const string FilterDeleted = "FilterDeleted";
        public const string InputLevelMeterChanged = "InputLevelMeterChanged";
        public const string InputMuteChanged = "InputMuteChanged";
        public const string InputNameChanged = "InputNameChanged";
        public const string InputVolumeChanged = "InputVolumeChanged";
        public const string MicrophoneBalanceChanged = "MicrophoneBalanceChanged";
        public const string MicrophoneGainChanged = "MicrophoneGainChanged";
        public const string MicrophoneMuteChanged = "MicrophoneMuteChanged";
        public const string MicrophoneOutputVolumeChanged = "MicrophoneOutputVolumeChanged";
        public const string MicrophoneSettingChanged = "MicrophoneSettingChanged";
        public const string OutputLevelMeterChanged = "OutputLevelMeterChanged";
        public const string OutputMuteChanged = "OutputMuteChanged";
        public const string OutputSwitched = "OutputSwitched";
        public const string OutputVolumeChanged = "OutputVolumeChanged";
        public const string SelectedOutputChanged = "SelectedOutputChanged";
    }

    /// <summary>Every event type, for the module test.</summary>
    public static readonly string[] All =
    [
        Connected, Disconnected, ChannelsChanged, ChannelUpdated, ChannelMuteChanged, ChannelVolumeChanged, ChannelEffectsChanged,
        InputDevicesChanged, InputDeviceUpdated, InputGainChanged, InputDspEffectsChanged, MixesChanged, MixUpdated,
        MainOutputChanged, OutputDeviceUpdated, OutputMixChanged, CreateProfileRequested, FocusedAppChanged, LevelMeter,
    ];
}

/// <summary>Builds trigger/sub-action arguments with Streamer.bot's Wave Link 3 variable names (waveLinkChannel*, ...).</summary>
internal static class WaveLinkArgs
{
    public static Dictionary<string, object?> New() => new(StringComparer.OrdinalIgnoreCase);

    public static void Channel(Dictionary<string, object?> a, WaveLinkChannel c, WaveLinkSnapshot s)
    {
        a["waveLinkChannelId"] = c.Id;
        a["waveLinkChannelName"] = c.Name;
        a["waveLinkChannelType"] = c.Type;
        a["waveLinkChannelMuted"] = c.Muted;
        a["waveLinkChannelVolume"] = c.Volume;
        a["waveLinkChannelLevel"] = c.Volume;
        a["waveLinkChannelMixesCount"] = c.Mixes.Count;
        a["waveLinkChannelEffectsCount"] = c.Effects.Count;
        a["waveLinkChannelAppsCount"] = c.Apps.Count;
        for (var i = 0; i < c.Mixes.Count; i++)
        {
            var m = c.Mixes[i];
            a[$"waveLinkChannelMix.{i}.id"] = m.MixId;
            a[$"waveLinkChannelMix.{i}.name"] = m.MixName.Length > 0 ? m.MixName : s.MixName(m.MixId);
            a[$"waveLinkChannelMix.{i}.muted"] = m.Muted;
            a[$"waveLinkChannelMix.{i}.level"] = m.Volume;
        }
        for (var i = 0; i < c.Effects.Count; i++)
        {
            a[$"waveLinkChannelEffect.{i}.id"] = c.Effects[i].Id;
            a[$"waveLinkChannelEffect.{i}.name"] = c.Effects[i].Name;
            a[$"waveLinkChannelEffect.{i}.enabled"] = c.Effects[i].Enabled;
        }
    }

    public static void ChannelMix(Dictionary<string, object?> a, WaveLinkChannelMix m, WaveLinkSnapshot s)
    {
        a["waveLinkMixId"] = m.MixId;
        a["waveLinkMixName"] = m.MixName.Length > 0 ? m.MixName : s.MixName(m.MixId);
        a["waveLinkMixMuted"] = m.Muted;
        a["waveLinkMixVolume"] = m.Volume;
    }

    public static void Mix(Dictionary<string, object?> a, WaveLinkMix m)
    {
        a["waveLinkMixId"] = m.Id;
        a["waveLinkMixName"] = m.Name;
        a["waveLinkMixMuted"] = m.Muted;
        a["waveLinkMixVolume"] = m.Volume;
        a["waveLinkMixLevel"] = m.Volume;
    }

    public static void InputDevice(Dictionary<string, object?> a, WaveLinkInputDevice d)
    {
        var i = d.Primary;
        a["waveLinkInputDeviceId"] = d.Id;
        a["waveLinkInputDeviceName"] = d.Name;
        a["waveLinkInputDeviceType"] = d.Type;
        a["waveLinkInputDeviceIsWaveDevice"] = d.IsWaveDevice;
        a["waveLinkInputDeviceChannelCount"] = d.Inputs.Count;
        a["waveLinkInputDeviceMuted"] = i?.Muted ?? false;
        a["waveLinkInputDeviceGain"] = i?.Gain;
        a["waveLinkInputDeviceGainDb"] = i?.GainDb;
        a["waveLinkInputDeviceGainLocked"] = i?.GainLocked;
        a["waveLinkInputDeviceMicPcMix"] = i?.MicPcMix;
        a["waveLinkInputDeviceClipGuard"] = i?.ClipGuard;
        a["waveLinkInputDeviceLowCutFilter"] = i?.LowCut;
        a["waveLinkInputDeviceOutputVolume"] = i?.OutputVolume;
        if (i is null) return;
        a["waveLinkInputId"] = i.Id;
        a["waveLinkInputName"] = i.Name;
        for (var n = 0; n < i.DspEffects.Count; n++)
        {
            a[$"waveLinkInputDspEffect.{n}.id"] = i.DspEffects[n].Id;
            a[$"waveLinkInputDspEffect.{n}.name"] = i.DspEffects[n].Name;
            a[$"waveLinkInputDspEffect.{n}.enabled"] = i.DspEffects[n].Enabled;
        }
    }

    public static void OutputDevice(Dictionary<string, object?> a, WaveLinkOutputDevice d, WaveLinkOutput? o, WaveLinkSnapshot s)
    {
        a["waveLinkOutputDeviceId"] = d.Id;
        a["waveLinkOutputDeviceName"] = d.Name;
        a["waveLinkOutputDeviceIsWaveDevice"] = d.IsWaveDevice;
        o ??= d.Primary;
        if (o is null) return;
        a["waveLinkOutputId"] = o.Id;
        a["waveLinkOutputName"] = o.Name;
        a["waveLinkOutputMuted"] = o.Muted;
        a["waveLinkOutputVolume"] = o.Volume;
        a["waveLinkOutputMixId"] = o.MixId;
        a["waveLinkOutputMixName"] = o.MixId.Length > 0 ? s.MixName(o.MixId) : "";
    }
}
