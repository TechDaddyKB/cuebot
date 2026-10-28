namespace Cuebot.Speech;

/// <summary>Receives captured mono samples (float, -1..1) on the audio thread. Must return quickly.</summary>
public delegate void CaptureCallback(ReadOnlySpan<float> samples);

/// <summary>
/// The native (or fallback) layer under <see cref="AudioOutput"/>: device lists, playback voices, capture and
/// optional native decoding. Kept narrow so tests can replace it with a fake.
/// </summary>
internal interface IAudioBackend : IDisposable
{
    /// <summary>Shown in the log and the UI, e.g. "miniaudio (PulseAudio)".</summary>
    string Name { get; }

    /// <summary>True when several voices play at once; false when a new sound may cut off the previous one.</summary>
    bool CanMix { get; }

    IReadOnlyList<AudioDevice> GetOutputDevices();
    IReadOnlyList<AudioDevice> GetInputDevices();

    /// <summary>Decodes formats the managed decoders don't handle (MP3, FLAC, compressed WAV). Null when unsupported.</summary>
    AudioClip? TryDecode(byte[] data);

    /// <summary>Starts a voice on <paramref name="deviceId"/> (null = default) at a linear gain of 0..1.</summary>
    IAudioVoice Play(AudioClip clip, string? deviceId, float gain);

    /// <summary>Starts capturing mono samples, or returns null when the backend can't capture.</summary>
    IAudioCapture? StartCapture(string? deviceId, CaptureCallback onSamples);
}

/// <summary>One sound playing on a backend.</summary>
internal interface IAudioVoice
{
    /// <summary>Completes when the sound ends or is stopped; faults when playback fails.</summary>
    Task Completion { get; }

    /// <summary>Stops the sound promptly. Safe to call more than once and after completion.</summary>
    void Stop();
}

/// <summary>A running microphone capture (see <see cref="IAudioInput.CaptureSamples"/>); dispose to stop.</summary>
public interface IAudioInputStream : IDisposable
{
    /// <summary>Rate of the mono samples handed to the <see cref="CaptureCallback"/>.</summary>
    int SampleRate { get; }
}

/// <summary>A running input capture on a backend; dispose to stop.</summary>
internal interface IAudioCapture : IAudioInputStream;

/// <summary>Maps a stored device id or name to a device the backend currently lists.</summary>
public static class AudioDeviceSelector
{
    /// <summary>Values that mean "the system default device".</summary>
    private static readonly string[] DefaultNames = ["", "default", "system default", "application default"];

    public static bool IsDefault(string? requested) =>
        requested is null || DefaultNames.Contains(requested.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>The matching device (by id, then by name), or null for the default or an unknown device.</summary>
    public static AudioDevice? Resolve(IReadOnlyList<AudioDevice> devices, string? requested)
    {
        if (IsDefault(requested)) return null;
        var wanted = requested!.Trim();
        return devices.FirstOrDefault(d => string.Equals(d.Id, wanted, StringComparison.Ordinal))
            ?? devices.FirstOrDefault(d => string.Equals(d.Id, wanted, StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(d => string.Equals(d.Name, wanted, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Gives devices stable, unique ids from their names (native ids are pointers that change between runs):
    /// a second "Speakers" becomes "Speakers (2)".
    /// </summary>
    public static IReadOnlyList<AudioDevice> WithUniqueIds(IEnumerable<(string Name, bool IsDefault)> devices)
    {
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var result = new List<AudioDevice>();
        foreach (var (rawName, isDefault) in devices)
        {
            var name = string.IsNullOrWhiteSpace(rawName) ? "Unnamed device" : rawName.Trim();
            var count = seen[name] = seen.GetValueOrDefault(name) + 1;
            result.Add(new AudioDevice(count == 1 ? name : $"{name} ({count})", name, isDefault));
        }
        return result;
    }
}

/// <summary>Turns captured samples into dBFS readings at a fixed rate (RMS over each window).</summary>
public sealed class AudioLevelMeter
{
    public const double SilenceDb = -96;
    private readonly Action<double> _onLevel;
    private readonly int _windowSamples;
    private double _sumOfSquares;
    private int _count;

    public AudioLevelMeter(int sampleRate, Action<double> onLevel, double windowSeconds = 0.05)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        _onLevel = onLevel;
        _windowSamples = Math.Max(1, (int)(sampleRate * windowSeconds));
    }

    public void Process(ReadOnlySpan<float> samples)
    {
        foreach (var sample in samples)
        {
            _sumOfSquares += (double)sample * sample;
            if (++_count < _windowSamples) continue;
            var level = ToDbfs(Math.Sqrt(_sumOfSquares / _count));
            _sumOfSquares = 0;
            _count = 0;
            _onLevel(level);
        }
    }

    /// <summary>Converts a linear amplitude (1.0 = full scale) to dBFS, floored at <see cref="SilenceDb"/>.</summary>
    public static double ToDbfs(double amplitude) =>
        amplitude <= 0 ? SilenceDb : Math.Clamp(20 * Math.Log10(amplitude), SilenceDb, 0);
}
