using System.Collections.Concurrent;

namespace Cuebot.Speech.Tests.Audio;

/// <summary>Backend whose voices play until the test ends or stops them; records everything.</summary>
internal sealed class FakeAudioBackend : IAudioBackend
{
    public string Name => "fake";
    public bool CanMix => true;
    public List<AudioDevice> Outputs { get; } = [new("Speakers", "Speakers", true), new("Headset", "Headset", false)];
    public List<AudioDevice> Inputs { get; } = [new("Mic", "Mic", true)];
    public ConcurrentQueue<FakeVoice> Voices { get; } = new();
    public Func<byte[], AudioClip?> Decoder { get; set; } = _ => null;
    public int DecodeCalls { get; private set; }
    public FakeCapture? Capture { get; private set; }
    public bool SupportsCapture { get; set; } = true;
    public bool Disposed { get; private set; }

    public IReadOnlyList<AudioDevice> GetOutputDevices() => Outputs;
    public IReadOnlyList<AudioDevice> GetInputDevices() => Inputs;

    public AudioClip? TryDecode(byte[] data)
    {
        DecodeCalls++;
        return Decoder(data);
    }

    public IAudioVoice Play(AudioClip clip, string? deviceId, float gain)
    {
        var voice = new FakeVoice(clip, deviceId, gain);
        Voices.Enqueue(voice);
        return voice;
    }

    public IAudioCapture? StartCapture(string? deviceId, CaptureCallback onSamples)
    {
        if (!SupportsCapture) return null;
        return Capture = new FakeCapture(deviceId, onSamples);
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeVoice(AudioClip clip, string? deviceId, float gain) : IAudioVoice
{
    private readonly TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public AudioClip Clip => clip;
    public string? DeviceId => deviceId;
    public float Gain => gain;
    public bool Stopped { get; private set; }
    public Task Completion => _done.Task;

    public void Stop()
    {
        Stopped = true;
        _done.TrySetResult();
    }

    public void Finish() => _done.TrySetResult();
}

internal sealed class FakeCapture(string? deviceId, CaptureCallback callback) : IAudioCapture
{
    public string? DeviceId => deviceId;
    public int SampleRate => 1000;
    public bool Disposed { get; private set; }
    public void Feed(float[] samples) => callback(samples);
    public void Dispose() => Disposed = true;
}

internal static class TestAudio
{
    /// <summary>A 16-bit PCM WAV of <paramref name="frames"/> mono frames at 1 kHz with a constant sample value.</summary>
    public static byte[] Wav16(int frames, short value = 1000, int sampleRate = 1000, int channels = 1)
    {
        var pcm = new byte[frames * channels * 2];
        for (var i = 0; i < frames * channels; i++) BitConverter.TryWriteBytes(pcm.AsSpan(i * 2), value);
        return Wav.FromPcm16(pcm, sampleRate, channels);
    }

    public static async Task<T> Within<T>(Task<T> task) => await task.WaitAsync(TimeSpan.FromSeconds(5));
    public static async Task Within(Task task) => await task.WaitAsync(TimeSpan.FromSeconds(5));

    /// <summary>Waits until the backend has started <paramref name="count"/> voices.</summary>
    public static async Task<FakeVoice[]> VoicesAsync(FakeAudioBackend backend, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (backend.Voices.Count < count)
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException($"Expected {count} voices, got {backend.Voices.Count}");
            await Task.Delay(10);
        }
        return [.. backend.Voices];
    }
}
