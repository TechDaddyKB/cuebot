namespace Cuebot.Speech;

public enum AudioFileFormat { Unknown, Wav, Mp3, OggVorbis, OggOpus, Flac }

/// <summary>Identifies audio files by their content (magic bytes), not by their extension.</summary>
public static class AudioFormatDetector
{
    /// <summary>Extensions of the formats Cuebot can play, used for folder scans and file pickers.</summary>
    public static IReadOnlyList<string> Extensions { get; } = [".wav", ".mp3", ".ogg", ".oga", ".flac"];

    public static bool HasSupportedExtension(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static AudioFileFormat Detect(ReadOnlySpan<byte> data)
    {
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data[8..12].SequenceEqual("WAVE"u8))
            return AudioFileFormat.Wav;
        if (data.Length >= 4 && data[..4].SequenceEqual("fLaC"u8)) return AudioFileFormat.Flac;
        if (data.Length >= 4 && data[..4].SequenceEqual("OggS"u8)) return DetectOgg(data);
        if (data.Length >= 3 && data[..3].SequenceEqual("ID3"u8)) return AudioFileFormat.Mp3;
        return IsMpegFrameHeader(data) ? AudioFileFormat.Mp3 : AudioFileFormat.Unknown;
    }

    /// <summary>The first page of an Ogg stream carries the codec's identification header.</summary>
    private static AudioFileFormat DetectOgg(ReadOnlySpan<byte> data)
    {
        var head = data[..Math.Min(data.Length, 512)];
        if (head.IndexOf("\u0001vorbis"u8) >= 0) return AudioFileFormat.OggVorbis;
        if (head.IndexOf("OpusHead"u8) >= 0) return AudioFileFormat.OggOpus;
        if (head.IndexOf("\u007FFLAC"u8) >= 0) return AudioFileFormat.Flac;
        return AudioFileFormat.Unknown;
    }

    /// <summary>An MPEG audio frame sync (11 set bits) with a valid layer, bitrate and sample rate.</summary>
    private static bool IsMpegFrameHeader(ReadOnlySpan<byte> data)
    {
        if (data.Length < 4 || data[0] != 0xFF || (data[1] & 0xE0) != 0xE0) return false;
        var version = (data[1] >> 3) & 0x3;
        var layer = (data[1] >> 1) & 0x3;
        var bitrate = data[2] >> 4;
        var sampleRate = (data[2] >> 2) & 0x3;
        return version != 1 && layer != 0 && bitrate != 0xF && sampleRate != 0x3;
    }
}
