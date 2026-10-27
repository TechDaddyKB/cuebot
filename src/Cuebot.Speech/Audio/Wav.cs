using System.Buffers.Binary;

namespace Cuebot.Speech;

/// <summary>Format of a WAV file as declared by its "fmt " chunk.</summary>
public readonly record struct WavFormat(int AudioFormat, int Channels, int SampleRate, int BitsPerSample)
{
    public const int PcmFormat = 1;

    public bool IsPcm16 => AudioFormat == PcmFormat && BitsPerSample == 16;
}

/// <summary>A parsed WAV file: its format and where the sample data lives.</summary>
public readonly record struct WavInfo(WavFormat Format, int DataOffset, int DataLength);

/// <summary>Pure helpers for building, parsing and adjusting RIFF/WAVE files.</summary>
public static class Wav
{
    private const int HeaderSize = 44;

    /// <summary>Wraps raw little-endian 16-bit PCM samples into a WAV file.</summary>
    public static byte[] FromPcm16(ReadOnlySpan<byte> pcm, int sampleRate, int channels)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sampleRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channels);
        const int bytesPerSample = 2;
        var dataLength = pcm.Length & ~1; // a trailing half sample cannot be played
        var wav = new byte[HeaderSize + dataLength];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], HeaderSize - 8 + dataLength);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], WavFormat.PcmFormat);
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], (short)channels);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * channels * bytesPerSample);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)(channels * bytesPerSample));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataLength);
        pcm[..dataLength].CopyTo(span[HeaderSize..]);
        return wav;
    }

    /// <summary>
    /// Reads the "fmt " and "data" chunks. A data length larger than the file (as written by streaming encoders)
    /// is clamped to the bytes actually present.
    /// </summary>
    /// <exception cref="InvalidDataException">The bytes are not a WAV file.</exception>
    public static WavInfo Parse(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 12 || !wav[..4].SequenceEqual("RIFF"u8) || !wav[8..12].SequenceEqual("WAVE"u8))
            throw new InvalidDataException("Not a WAV file (missing RIFF/WAVE header).");

        WavFormat? format = null;
        var position = 12;
        while (position + 8 <= wav.Length)
        {
            var id = wav.Slice(position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(wav[(position + 4)..]);
            var body = position + 8;
            var available = wav.Length - body;
            if (id.SequenceEqual("fmt "u8))
            {
                if (size < 16 || available < 16) throw new InvalidDataException("WAV format chunk is truncated.");
                var fmt = wav[body..];
                format = new WavFormat(
                    BinaryPrimitives.ReadUInt16LittleEndian(fmt),
                    BinaryPrimitives.ReadUInt16LittleEndian(fmt[2..]),
                    BinaryPrimitives.ReadInt32LittleEndian(fmt[4..]),
                    BinaryPrimitives.ReadUInt16LittleEndian(fmt[14..]));
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (format is null) throw new InvalidDataException("WAV data chunk appears before the format chunk.");
                return new WavInfo(format.Value, body, (int)Math.Min(size, (uint)available));
            }
            // Chunks are padded to an even size.
            var next = body + (long)size + (size & 1);
            if (next > wav.Length) break;
            position = (int)next;
        }
        throw new InvalidDataException(format is null ? "WAV file has no format chunk." : "WAV file has no data chunk.");
    }

    /// <summary>
    /// Returns a well-formed copy of a 16-bit PCM WAV with every sample scaled by <paramref name="volumePercent"/>
    /// (clamped to 0-100). Other sample formats are returned unchanged.
    /// </summary>
    public static byte[] ApplyVolume(byte[] wav, int volumePercent)
    {
        var info = Parse(wav);
        if (!info.Format.IsPcm16) return wav;
        var pcm = wav.AsSpan(info.DataOffset, info.DataLength).ToArray();
        ScalePcm16(pcm, Math.Clamp(volumePercent, 0, 100));
        return FromPcm16(pcm, info.Format.SampleRate, info.Format.Channels);
    }

    /// <summary>Rewrites a 16-bit PCM WAV with exact chunk sizes (streaming encoders write placeholders).</summary>
    public static byte[] Normalize(byte[] wav) => ApplyVolume(wav, 100);

    /// <summary>Scales little-endian 16-bit samples in place.</summary>
    public static void ScalePcm16(Span<byte> pcm, int volumePercent)
    {
        var volume = Math.Clamp(volumePercent, 0, 100);
        if (volume == 100) return;
        for (var i = 0; i + 1 < pcm.Length; i += 2)
        {
            var sample = BinaryPrimitives.ReadInt16LittleEndian(pcm[i..]);
            BinaryPrimitives.WriteInt16LittleEndian(pcm[i..], (short)(sample * volume / 100));
        }
    }
}
