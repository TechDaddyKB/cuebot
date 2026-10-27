using Cuebot.Core.Logging;

namespace Cuebot.Speech;

/// <summary>
/// The application's audio output and input: decodes WAV/MP3/Ogg Vorbis/FLAC, plays clips mixed on any output
/// device, and meters microphone levels. The backend (native miniaudio, or a fallback) is created on first use.
/// </summary>
public sealed class AudioOutput : IAudioClipPlayer, IAudioInput, IDisposable
{
    private const string LogSource = "Audio";
    private readonly Lazy<IAudioBackend> _backend;
    private readonly AppLog? _log;
    private readonly HashSet<string> _warnedDevices = new(StringComparer.OrdinalIgnoreCase);

    internal AudioOutput(Func<IAudioBackend> backendFactory, AppLog? log = null)
    {
        _backend = new Lazy<IAudioBackend>(backendFactory, LazyThreadSafetyMode.ExecutionAndPublication);
        _log = log;
    }

    internal AudioOutput(IAudioBackend backend, AppLog? log = null) : this(() => backend, log)
    {
    }

    private IAudioBackend Backend => _backend.Value;

    public string BackendName => Backend.Name;

    /// <summary>False when the fallback player is active and a new sound may cut off another one.</summary>
    public bool CanMix => Backend.CanMix;

    /// <summary>
    /// Loads the backend and lists devices in the background, so the first sound doesn't wait for the native
    /// library (which can take a second or more with some drivers).
    /// </summary>
    public Task WarmUpAsync() => Task.Run(() => GetOutputDevices());

    public IReadOnlyList<AudioDevice> GetOutputDevices() => SafeList(() => Backend.GetOutputDevices());

    public IReadOnlyList<AudioDevice> GetInputDevices() => SafeList(() => Backend.GetInputDevices());

    public AudioClip Decode(byte[] audio)
    {
        ArgumentNullException.ThrowIfNull(audio);
        var format = AudioFormatDetector.Detect(audio);
        switch (format)
        {
            case AudioFileFormat.Wav:
                try
                {
                    return WavDecoder.Decode(audio);
                }
                catch (NotSupportedException) when (Backend.TryDecode(audio) is { } native)
                {
                    return native;
                }
            case AudioFileFormat.OggVorbis:
                return OggVorbisDecoder.Decode(audio);
            case AudioFileFormat.Mp3:
            case AudioFileFormat.Flac:
                return Backend.TryDecode(audio) ?? throw new NotSupportedException(
                    $"{format.ToString().ToUpperInvariant()} files need the native audio library, which is not available ({Backend.Name}). Use WAV or Ogg Vorbis instead.");
            case AudioFileFormat.OggOpus:
                throw new NotSupportedException("Ogg Opus files are not supported. Use Ogg Vorbis, MP3, FLAC or WAV.");
            default:
                throw new NotSupportedException("Unknown audio format. Supported formats are WAV, MP3, Ogg Vorbis and FLAC.");
        }
    }

    public Task PlayAsync(byte[] audio, PlaybackOptions options, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return PlayClipAsync(Decode(audio), options, ct);
    }

    public async Task PlayClipAsync(AudioClip clip, PlaybackOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(clip);
        ct.ThrowIfCancellationRequested();
        var voice = Backend.Play(clip, ResolveDevice(options.DeviceId), PcmConverter.Gain(options.Volume));
        using (ct.Register(voice.Stop))
        {
            await voice.Completion.ConfigureAwait(false);
        }
        ct.ThrowIfCancellationRequested();
    }

    public IDisposable MonitorLevel(string? deviceId, Action<double> onLevelDb)
    {
        ArgumentNullException.ThrowIfNull(onLevelDb);
        AudioLevelMeter? meter = null;
        var capture = Backend.StartCapture(ResolveInputDevice(deviceId), samples => meter?.Process(samples));
        if (capture is null)
        {
            _log?.Warn(LogSource, $"Microphone level monitoring is not available with {Backend.Name}.");
            return new EmptyDisposable();
        }
        meter = new AudioLevelMeter(capture.SampleRate, onLevelDb);
        return capture;
    }

    /// <summary>The device to use: null (default) when unset, or when the device is gone (warned once).</summary>
    private string? ResolveDevice(string? requested)
    {
        if (AudioDeviceSelector.IsDefault(requested)) return null;
        var device = AudioDeviceSelector.Resolve(GetOutputDevices(), requested);
        if (device is not null) return device.Id;
        WarnMissing("Output", requested!);
        return null;
    }

    private string? ResolveInputDevice(string? requested)
    {
        if (AudioDeviceSelector.IsDefault(requested)) return null;
        var device = AudioDeviceSelector.Resolve(GetInputDevices(), requested);
        if (device is not null) return device.Id;
        WarnMissing("Input", requested!);
        return null;
    }

    private void WarnMissing(string kind, string requested)
    {
        bool first;
        lock (_warnedDevices) first = _warnedDevices.Add(kind + "|" + requested);
        if (first) _log?.Warn(LogSource, $"{kind} device \"{requested}\" was not found; using the default device.");
    }

    private IReadOnlyList<AudioDevice> SafeList(Func<IReadOnlyList<AudioDevice>> list)
    {
        try
        {
            return list();
        }
        catch (Exception ex)
        {
            _log?.Error(LogSource, "Could not list audio devices", ex);
            return [];
        }
    }

    public void Dispose()
    {
        if (_backend.IsValueCreated) _backend.Value.Dispose();
    }

    private sealed class EmptyDisposable : IDisposable
    {
        public void Dispose() { }
    }
}
