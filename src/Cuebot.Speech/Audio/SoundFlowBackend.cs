using Cuebot.Core.Logging;
using SoundFlow.Abstracts.Devices;
using SoundFlow.Backends.MiniAudio;
using SoundFlow.Backends.MiniAudio.Devices;
using SoundFlow.Components;
using SoundFlow.Enums;
using SoundFlow.Providers;
using SoundFlow.Structs;
using MaBackend = SoundFlow.Backends.MiniAudio.Enums.MiniAudioBackend;
using SfLogLevel = SoundFlow.Utils.LogLevel;

namespace Cuebot.Speech;

/// <summary>
/// Native backend: SoundFlow over miniaudio (WASAPI on Windows; PulseAudio/PipeWire, then ALSA and JACK on Linux).
/// Each output device is opened once (48 kHz stereo float) and its mixer plays any number of voices at the same time;
/// a device closes itself after a while without sounds.
/// </summary>
internal sealed class SoundFlowBackend : IAudioBackend
{
    internal const int SampleRate = 48000;
    internal const int Channels = 2;
    private const string LogSource = "Audio";
    private static readonly TimeSpan IdleClose = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan DeviceCacheTime = TimeSpan.FromSeconds(2);

    private static readonly AudioFormat OutputFormat = new()
    {
        Format = SampleFormat.F32, Channels = Channels, SampleRate = SampleRate, Layout = ChannelLayout.Stereo,
    };

    private static readonly AudioFormat CaptureFormat = new()
    {
        Format = SampleFormat.F32, Channels = 1, SampleRate = SampleRate, Layout = ChannelLayout.Mono,
    };

    private readonly MiniAudioEngine _engine;
    private readonly AppLog? _log;
    private readonly object _gate = new();
    private readonly Dictionary<string, OutputDevice> _outputs = new(StringComparer.Ordinal);
    private DateTime _devicesListedAt = DateTime.MinValue;
    private IReadOnlyList<(AudioDevice Device, DeviceInfo Info)> _playback = [];
    private IReadOnlyList<(AudioDevice Device, DeviceInfo Info)> _capture = [];
    private readonly Action<SoundFlow.Utils.LogEntry>? _logForwarder;
    private bool _disposed;

    private SoundFlowBackend(MiniAudioEngine engine, AppLog? log, Action<SoundFlow.Utils.LogEntry>? logForwarder)
    {
        _engine = engine;
        _log = log;
        _logForwarder = logForwarder;
        Name = $"miniaudio ({engine.ActiveBackend})";
    }

    public string Name { get; }
    public bool CanMix => true;

    /// <summary>Loads the native library and initialises a context; throws when that isn't possible.</summary>
    public static SoundFlowBackend Create(AppLog? log)
    {
        MaBackend[] priority = OperatingSystem.IsWindows()
            ? [MaBackend.Wasapi, MaBackend.DirectSound, MaBackend.WinMm]
            : [MaBackend.PulseAudio, MaBackend.Alsa, MaBackend.Jack];
        var forwarder = log is null ? null : ForwardLog(log);
        if (forwarder is not null) SoundFlow.Utils.Log.OnLog += forwarder;
        MiniAudioEngine? engine = null;
        try
        {
            engine = new MiniAudioEngine(priority);
            engine.UpdateAudioDevicesInfo();
            return new SoundFlowBackend(engine, log, forwarder);
        }
        catch
        {
            engine?.Dispose();
            if (forwarder is not null) SoundFlow.Utils.Log.OnLog -= forwarder;
            throw;
        }
    }

    private static Action<SoundFlow.Utils.LogEntry> ForwardLog(AppLog log) => entry =>
    {
        if (entry.Level >= SfLogLevel.Error) log.Error(LogSource, $"miniaudio: {entry.Message}");
        else if (entry.Level == SfLogLevel.Warning) log.Warn(LogSource, $"miniaudio: {entry.Message}");
    };

    public IReadOnlyList<AudioDevice> GetOutputDevices()
    {
        lock (_gate)
        {
            RefreshDevices(force: false);
            return _playback.Select(p => p.Device).ToArray();
        }
    }

    public IReadOnlyList<AudioDevice> GetInputDevices()
    {
        lock (_gate)
        {
            RefreshDevices(force: false);
            return _capture.Select(p => p.Device).ToArray();
        }
    }

    private void RefreshDevices(bool force)
    {
        if (!force && DateTime.UtcNow - _devicesListedAt < DeviceCacheTime) return;
        _engine.UpdateAudioDevicesInfo();
        _playback = Pair(_engine.PlaybackDevices);
        _capture = Pair(_engine.CaptureDevices);
        _devicesListedAt = DateTime.UtcNow;

        static IReadOnlyList<(AudioDevice, DeviceInfo)> Pair(DeviceInfo[] infos) =>
            AudioDeviceSelector.WithUniqueIds(infos.Select(i => (i.Name, i.IsDefault))).Zip(infos).ToArray();
    }

    /// <summary>The native info for a device id, re-enumerating once when the cached list doesn't have it.</summary>
    private DeviceInfo? FindInfo(bool playback, string? deviceId)
    {
        if (deviceId is null) return null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            RefreshDevices(force: attempt > 0);
            foreach (var (device, info) in playback ? _playback : _capture)
            {
                if (device.Id == deviceId) return info;
            }
        }
        return null;
    }

    public AudioClip? TryDecode(byte[] data)
    {
        try
        {
            using var stream = new MemoryStream(data, writable: false);
            var decoder = _engine.CreateDecoder(stream, out _, OutputFormat);
            try
            {
                var samples = new List<float>(decoder.Length > 0 ? Math.Min(decoder.Length, 64 * 1024 * 1024) : 0);
                var buffer = new float[8192];
                int read;
                while ((read = decoder.Decode(buffer)) > 0) samples.AddRange(buffer.AsSpan(0, read));
                return samples.Count == 0 ? null : new AudioClip([.. samples], decoder.Channels, decoder.SampleRate);
            }
            finally
            {
                (decoder as IDisposable)?.Dispose();
            }
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }

    public IAudioVoice Play(AudioClip clip, string? deviceId, float gain)
    {
        var converted = PcmConverter.Convert(clip, SampleRate, Channels);
        OutputDevice output;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = deviceId ?? "";
            if (!_outputs.TryGetValue(key, out output!))
            {
                output = new OutputDevice(this, key, OpenPlayback(deviceId));
                _outputs[key] = output;
            }
            output.Active++;
        }
        return new Voice(this, output, converted, gain);
    }

    private AudioPlaybackDevice OpenPlayback(string? deviceId)
    {
        var info = FindInfo(playback: true, deviceId);
        if (deviceId is not null && info is null) _log?.Warn(LogSource, $"Output device \"{deviceId}\" disappeared; using the default device.");
        var device = _engine.InitializePlaybackDevice(info, OutputFormat, new MiniAudioDeviceConfig
        {
            PeriodSizeInMilliseconds = 10,
            Pulse = new PulseSettings { StreamNamePlayback = "Cuebot" },
        });
        device.Start();
        _log?.Info(LogSource, $"Opened output device {info?.Name ?? "(default)"} via {Name}");
        return device;
    }

    /// <summary>Called when a voice ends: closes the device after <see cref="IdleClose"/> without sounds.</summary>
    private void Release(OutputDevice output)
    {
        lock (_gate)
        {
            output.Active--;
            if (output.Active > 0 || _disposed) return;
            output.IdleTimer ??= new Timer(_ => CloseIfIdle(output));
            output.IdleTimer.Change(IdleClose, Timeout.InfiniteTimeSpan);
        }
    }

    private void CloseIfIdle(OutputDevice output)
    {
        lock (_gate)
        {
            if (output.Active > 0 || !_outputs.Remove(output.Key)) return;
        }
        output.Close();
    }

    public IAudioCapture? StartCapture(string? deviceId, CaptureCallback onSamples)
    {
        AudioCaptureDevice device;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            device = _engine.InitializeCaptureDevice(FindInfo(playback: false, deviceId), CaptureFormat, new MiniAudioDeviceConfig
            {
                Pulse = new PulseSettings { StreamNameCapture = "Cuebot level meter" },
            });
        }
        return new Capture(device, onSamples);
    }

    public void Dispose()
    {
        OutputDevice[] outputs;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            outputs = [.. _outputs.Values];
            _outputs.Clear();
        }
        foreach (var output in outputs) output.Close();
        _engine.Dispose();
        if (_logForwarder is not null) SoundFlow.Utils.Log.OnLog -= _logForwarder;
    }

    private sealed class OutputDevice(SoundFlowBackend owner, string key, AudioPlaybackDevice device)
    {
        public string Key => key;
        public AudioPlaybackDevice Device => device;
        public SoundFlowBackend Owner => owner;
        public int Active { get; set; }
        public Timer? IdleTimer { get; set; }

        public void Close()
        {
            IdleTimer?.Dispose();
            try
            {
                device.Stop();
                device.Dispose();
            }
            catch (Exception ex)
            {
                owner._log?.Error(LogSource, "Could not close the output device", ex);
            }
        }
    }

    /// <summary>A SoundPlayer on a device mixer. A watchdog ends it if the device stalls (e.g. unplugged).</summary>
    private sealed class Voice : IAudioVoice
    {
        private static readonly TimeSpan WatchdogSlack = TimeSpan.FromSeconds(3);
        private readonly OutputDevice _output;
        private readonly SoundPlayer _player;
        private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Timer _watchdog;
        private int _finished;

        public Voice(SoundFlowBackend owner, OutputDevice output, AudioClip clip, float gain)
        {
            _output = output;
            _player = new SoundPlayer(owner._engine, OutputFormat, new RawDataProvider(clip.Samples, SampleRate)) { Volume = gain };
            _player.PlaybackEnded += (_, _) => Finish();
            _watchdog = new Timer(_ => Finish(), null, clip.Duration + WatchdogSlack, Timeout.InfiniteTimeSpan);
            try
            {
                output.Device.MasterMixer.AddComponent(_player);
                _player.Play();
            }
            catch
            {
                Finish();
                throw;
            }
        }

        public Task Completion => _done.Task;

        public void Stop() => Finish();

        private void Finish()
        {
            if (Interlocked.Exchange(ref _finished, 1) == 1) return;
            _watchdog.Dispose();
            // Leave the audio thread (PlaybackEnded is raised there) before touching the mixer.
            _ = Task.Run(() =>
            {
                try
                {
                    _player.Stop();
                    _output.Device.MasterMixer.RemoveComponent(_player);
                    _player.Dispose();
                }
                catch (Exception ex)
                {
                    _output.Owner._log?.Error(LogSource, "Could not release a sound", ex);
                }
                finally
                {
                    _output.Owner.Release(_output);
                    _done.TrySetResult();
                }
            });
        }
    }

    private sealed class Capture : IAudioCapture
    {
        private readonly AudioCaptureDevice _device;
        private readonly AudioProcessCallbackHolder _holder;

        public Capture(AudioCaptureDevice device, CaptureCallback onSamples)
        {
            _device = device;
            _holder = new AudioProcessCallbackHolder(onSamples);
            device.OnAudioProcessed += _holder.Handle;
            device.Start();
        }

        public int SampleRate => SoundFlowBackend.SampleRate;

        public void Dispose()
        {
            _device.OnAudioProcessed -= _holder.Handle;
            _device.Stop();
            _device.Dispose();
        }

        private sealed class AudioProcessCallbackHolder(CaptureCallback callback)
        {
            public void Handle(Span<float> samples, Capability capability) => callback(samples);
        }
    }
}
