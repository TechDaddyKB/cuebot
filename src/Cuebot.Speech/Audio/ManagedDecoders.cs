using System.Buffers.Binary;
using NVorbis;

namespace Cuebot.Speech;

/// <summary>Decodes PCM (8/16/24/32-bit) and IEEE float (32/64-bit) WAV files, including WAVE_FORMAT_EXTENSIBLE.</summary>
public static class WavDecoder
{
    private const int FormatPcm = 1;
    private const int FormatFloat = 3;
    private const int FormatExtensible = 0xFFFE;

    /// <exception cref="InvalidDataException">Not a WAV file, or a truncated one.</exception>
    /// <exception cref="NotSupportedException">A compressed WAV (ADPCM, A-law, ...).</exception>
    public static AudioClip Decode(ReadOnlySpan<byte> wav)
    {
        var info = Wav.Parse(wav);
        var format = info.Format;
        var code = format.AudioFormat;
        if (code == FormatExtensible) code = ReadExtensibleSubFormat(wav);
        if (format.Channels <= 0 || format.SampleRate <= 0) throw new InvalidDataException("WAV format chunk is invalid.");

        var bytesPerSample = format.BitsPerSample / 8;
        var data = wav.Slice(info.DataOffset, info.DataLength);
        var count = bytesPerSample == 0 ? 0 : data.Length / bytesPerSample / format.Channels * format.Channels;
        var samples = new float[count];
        switch (code, format.BitsPerSample)
        {
            case (FormatPcm, 8):
                for (var i = 0; i < count; i++) samples[i] = (data[i] - 128) / 128f;
                break;
            case (FormatPcm, 16):
                for (var i = 0; i < count; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]) / 32768f;
                break;
            case (FormatPcm, 24):
                for (var i = 0; i < count; i++)
                {
                    var o = i * 3;
                    var value = data[o] | (data[o + 1] << 8) | ((sbyte)data[o + 2] << 16);
                    samples[i] = value / 8388608f;
                }
                break;
            case (FormatPcm, 32):
                for (var i = 0; i < count; i++) samples[i] = BinaryPrimitives.ReadInt32LittleEndian(data[(i * 4)..]) / 2147483648f;
                break;
            case (FormatFloat, 32):
                for (var i = 0; i < count; i++) samples[i] = BinaryPrimitives.ReadSingleLittleEndian(data[(i * 4)..]);
                break;
            case (FormatFloat, 64):
                for (var i = 0; i < count; i++) samples[i] = (float)BinaryPrimitives.ReadDoubleLittleEndian(data[(i * 8)..]);
                break;
            default:
                throw new NotSupportedException($"WAV encoding {code} with {format.BitsPerSample}-bit samples is not supported.");
        }
        return new AudioClip(samples, format.Channels, format.SampleRate);
    }

    /// <summary>The first two bytes of the sub-format GUID hold the real format code.</summary>
    private static int ReadExtensibleSubFormat(ReadOnlySpan<byte> wav)
    {
        var position = 12;
        while (position + 8 <= wav.Length)
        {
            var size = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(wav[(position + 4)..]), int.MaxValue);
            if (wav.Slice(position, 4).SequenceEqual("fmt "u8))
            {
                if (size < 40 || position + 8 + 26 > wav.Length) throw new InvalidDataException("WAV extensible format chunk is truncated.");
                return BinaryPrimitives.ReadUInt16LittleEndian(wav[(position + 8 + 24)..]);
            }
            position += 8 + size + (size & 1);
        }
        throw new InvalidDataException("WAV file has no format chunk.");
    }
}

/// <summary>Decodes Ogg Vorbis with the managed NVorbis decoder.</summary>
public static class OggVorbisDecoder
{
    /// <exception cref="InvalidDataException">The stream is not valid Ogg Vorbis.</exception>
    public static AudioClip Decode(byte[] data)
    {
        try
        {
            using var reader = new VorbisReader(new MemoryStream(data, writable: false), closeOnDispose: true);
            var channels = reader.Channels;
            var expected = reader.TotalSamples > 0 ? reader.TotalSamples * channels : 0;
            var samples = new List<float>((int)Math.Clamp(expected, 0, 64 * 1024 * 1024));
            var buffer = new float[4096 * channels];
            int read;
            while ((read = reader.ReadSamples(buffer, 0, buffer.Length)) > 0) samples.AddRange(buffer.AsSpan(0, read));
            return new AudioClip([.. samples], channels, reader.SampleRate);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or EndOfStreamException)
        {
            throw new InvalidDataException($"Not a valid Ogg Vorbis file: {ex.Message}", ex);
        }
    }
}

/// <summary>Converts sample rate and channel count, and applies gain, in plain managed code.</summary>
public static class PcmConverter
{
    /// <summary>Returns the clip resampled (linear interpolation) and remixed to the target format.</summary>
    public static AudioClip Convert(AudioClip clip, int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        var remixed = clip.Channels == channels ? clip : Remix(clip, channels);
        return remixed.SampleRate == sampleRate ? remixed : Resample(remixed, sampleRate);
    }

    private static AudioClip Remix(AudioClip clip, int channels)
    {
        var frames = clip.Frames;
        var source = clip.Samples;
        var inCh = clip.Channels;
        var output = new float[frames * channels];
        for (var f = 0; f < frames; f++)
        {
            var i = f * inCh;
            var o = f * channels;
            if (channels == 1)
            {
                var sum = 0f;
                for (var c = 0; c < inCh; c++) sum += source[i + c];
                output[o] = sum / inCh;
            }
            else
            {
                // Mono is copied to every speaker; otherwise channels map by position (surround keeps front L/R).
                for (var c = 0; c < channels; c++) output[o + c] = source[i + (inCh == 1 ? 0 : c % inCh)];
            }
        }
        return new AudioClip(output, channels, clip.SampleRate);
    }

    private static AudioClip Resample(AudioClip clip, int sampleRate)
    {
        var channels = clip.Channels;
        var inFrames = clip.Frames;
        var outFrames = (int)Math.Max(0, (long)inFrames * sampleRate / clip.SampleRate);
        var output = new float[outFrames * channels];
        var step = (double)clip.SampleRate / sampleRate;
        var source = clip.Samples;
        for (var f = 0; f < outFrames; f++)
        {
            var position = f * step;
            var index = (int)position;
            var fraction = (float)(position - index);
            var next = Math.Min(index + 1, inFrames - 1);
            for (var c = 0; c < channels; c++)
            {
                var a = source[index * channels + c];
                var b = source[next * channels + c];
                output[f * channels + c] = a + (b - a) * fraction;
            }
        }
        return new AudioClip(output, channels, sampleRate);
    }

    /// <summary>Linear gain for a 0-100 volume.</summary>
    public static float Gain(int volumePercent) => Math.Clamp(volumePercent, 0, 100) / 100f;
}
