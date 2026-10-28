namespace Cuebot.WaveLink.Model;

// One model for both API generations. Volumes, levels, gains and balances are percentages (0-100) unless noted:
// Wave Link 3 sends 0-1 and is scaled; Wave Link 1.x/2.x already uses 0-100 for volumes.
// Wave Link 1.x/2.x maps as: each input is a channel with two mixes (Monitor = com.elgato.mix.local,
// Stream = com.elgato.mix.stream); the two output mixers are the mixes; the microphone(s) are input devices;
// the selectable monitor devices are output devices.

/// <summary>A software or hardware effect (Wave Link 3 effects / dspEffects, 1.x/2.x VST filters).</summary>
public sealed record WaveLinkEffect(string Id, string Name, bool Enabled, bool Supported = true, string PluginId = "");

/// <summary>A channel's settings in one mix.</summary>
public sealed record WaveLinkChannelMix(string MixId, string MixName, bool Muted, double Volume, bool FilterBypassed = false);

public sealed record WaveLinkApp(string Id, string Name);

/// <summary>An audio channel (Wave Link 3) or input (1.x/2.x).</summary>
public sealed record WaveLinkChannel(
    string Id,
    string Name,
    string Type,
    bool Muted,
    double Volume,
    IReadOnlyList<WaveLinkChannelMix> Mixes,
    IReadOnlyList<WaveLinkEffect> Effects,
    IReadOnlyList<WaveLinkApp> Apps,
    bool Available = true)
{
    public WaveLinkChannelMix? Mix(string mixId) => Mixes.FirstOrDefault(m => string.Equals(m.MixId, mixId, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A mix (Wave Link 3) or one of the two output mixers (1.x/2.x).</summary>
public sealed record WaveLinkMix(string Id, string Name, bool Muted, double Volume);

/// <summary>One input of an input device (a microphone capsule, an XLR input...).</summary>
public sealed record WaveLinkInput(
    string Id,
    string Name,
    bool Muted,
    double? Gain,
    double? GainMaxDb,
    double? RawGain,
    bool? GainLocked,
    double? MicPcMix,
    double? OutputVolume,
    bool? LowCut,
    bool? ClipGuard,
    IReadOnlyList<WaveLinkEffect> Effects,
    IReadOnlyList<WaveLinkEffect> DspEffects)
{
    /// <summary>The gain in dB when Wave Link reports a maximum (Wave Link 3: value x max).</summary>
    public double? GainDb => Gain is { } g && GainMaxDb is { } max ? Math.Round(g / 100 * max, 1) : null;
}

public sealed record WaveLinkInputDevice(string Id, string Name, string Type, bool IsWaveDevice, IReadOnlyList<WaveLinkInput> Inputs)
{
    public WaveLinkInput? Primary => Inputs.Count > 0 ? Inputs[0] : null;
}

/// <summary>An output of an output device; <see cref="MixId"/> is the mix it plays (Wave Link 3), when reported.</summary>
public sealed record WaveLinkOutput(string Id, string Name, bool Muted, double Volume, string MixId = "");

public sealed record WaveLinkOutputDevice(string Id, string Name, bool IsWaveDevice, IReadOnlyList<WaveLinkOutput> Outputs)
{
    public WaveLinkOutput? Primary => Outputs.Count > 0 ? Outputs[0] : null;
}

public sealed record WaveLinkMainOutput(string OutputDeviceId, string OutputId);

public sealed record WaveLinkFocusedApp(string Id, string Name, string ChannelId);

/// <summary>One level meter reading (0-100 per side). Type is channel, mix, input or output.</summary>
public sealed record WaveLinkLevel(string Type, string Id, string SubId, string Name, double Left, double Right)
{
    public string Key => $"{Type}:{Id}:{SubId}";

    public double Peak => Math.Max(Left, Right);
}

/// <summary>Everything Cuebot knows about the connected Wave Link (immutable; replaced on every change).</summary>
public sealed record WaveLinkSnapshot(
    IReadOnlyList<WaveLinkChannel> Channels,
    IReadOnlyList<WaveLinkMix> Mixes,
    IReadOnlyList<WaveLinkInputDevice> InputDevices,
    IReadOnlyList<WaveLinkOutputDevice> OutputDevices,
    WaveLinkMainOutput? MainOutput,
    string MonitoredMixId,
    WaveLinkFocusedApp? FocusedApp)
{
    public static WaveLinkSnapshot Empty { get; } = new([], [], [], [], null, "", null);

    public WaveLinkMix? MixById(string id) => Mixes.FirstOrDefault(m => string.Equals(m.Id, id, StringComparison.OrdinalIgnoreCase));

    public string MixName(string id) => MixById(id)?.Name ?? id;

    public WaveLinkOutputDevice? MainOutputDevice =>
        MainOutput is { } main ? OutputDevices.FirstOrDefault(d => d.Id == main.OutputDeviceId) : null;

    public WaveLinkChannel FindChannel(string text) => Find(Channels, text, c => c.Id, c => c.Name, "channel");

    public WaveLinkMix FindMix(string text) => Find(Mixes, text, m => m.Id, m => m.Name, "mix");

    public WaveLinkInputDevice FindInputDevice(string text) =>
        Find(InputDevices, text, d => d.Id, d => d.Name, "input device", d => d.Inputs.SelectMany(i => new[] { i.Id, i.Name }));

    public WaveLinkOutputDevice FindOutputDevice(string text) =>
        Find(OutputDevices, text, d => d.Id, d => d.Name, "output device", d => d.Outputs.SelectMany(o => new[] { o.Id, o.Name }));

    /// <summary>Exact id, then case-insensitive name, then extra aliases; empty text picks the only item when there is one.</summary>
    internal static T Find<T>(IReadOnlyList<T> items, string text, Func<T, string> id, Func<T, string> name, string what,
        Func<T, IEnumerable<string>>? aliases = null) where T : class
    {
        var wanted = text.Trim();
        if (wanted.Length == 0)
        {
            if (items.Count == 1) return items[0];
            throw new ArgumentException($"Choose a {what}.");
        }
        return items.FirstOrDefault(i => id(i) == wanted)
            ?? items.FirstOrDefault(i => string.Equals(name(i), wanted, StringComparison.OrdinalIgnoreCase))
            ?? items.FirstOrDefault(i => string.Equals(id(i), wanted, StringComparison.OrdinalIgnoreCase))
            ?? (aliases is null ? null : items.FirstOrDefault(i => aliases(i).Any(a => string.Equals(a, wanted, StringComparison.OrdinalIgnoreCase))))
            ?? throw new WaveLinkRequestException($"Wave Link has no {what} named '{wanted}'.");
    }
}
