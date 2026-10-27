using System.Buffers.Binary;

namespace Cuebot.Speech;

/// <summary>Decoded audio: interleaved 32-bit float samples in the range -1..1.</summary>
public sealed class AudioClip
{
    public AudioClip(float[] samples, int channels, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        Samples = samples;
        Channels = channels;
        SampleRate = sampleRate;
    }

    public float[] Samples { get; }
    public int Channels { get; }
    public int SampleRate { get; }

    /// <summary>Number of sample frames (one sample per channel).</summary>
    public int Frames => Samples.Length / Channels;

    public TimeSpan Duration => TimeSpan.FromSeconds((double)Frames / SampleRate);

    /// <summary>Encodes the clip as a 16-bit PCM WAV, scaled by <paramref name="volumePercent"/> (0-100).</summary>
    public byte[] ToWav16(int volumePercent = 100)
    {
        var gain = Math.Clamp(volumePercent, 0, 100) / 100f;
        var count = Frames * Channels;
        var pcm = new byte[count * 2];
        for (var i = 0; i < count; i++)
        {
            var value = Math.Clamp(Samples[i] * gain, -1f, 1f);
            BinaryPrimitives.WriteInt16LittleEndian(pcm.AsSpan(i * 2), (short)MathF.Round(value * short.MaxValue));
        }
        return Wav.FromPcm16(pcm, SampleRate, Channels);
    }
}

/// <summary>An audio player that can decode once and play decoded clips (used to report durations).</summary>
public interface IAudioClipPlayer : IAudioPlayer
{
    /// <summary>Human-readable name of the active backend, e.g. "miniaudio (Wasapi)".</summary>
    string BackendName { get; }

    /// <summary>Decodes WAV, MP3, Ogg Vorbis or FLAC data.</summary>
    /// <exception cref="NotSupportedException">The format is unknown or no decoder is available for it.</exception>
    /// <exception cref="InvalidDataException">The data is corrupt.</exception>
    AudioClip Decode(byte[] audio);

    /// <summary>Plays a decoded clip; other clips keep playing (mixed) where the backend can mix.</summary>
    Task PlayClipAsync(AudioClip clip, PlaybackOptions options, CancellationToken ct);
}
