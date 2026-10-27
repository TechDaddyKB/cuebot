namespace Cuebot.Speech;

/// <summary>Estimates the playing time of synthesized audio from its headers.</summary>
internal static class AudioDuration
{
    private static readonly int[] Mpeg1Layer3Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
    private static readonly int[] Mpeg2Layer3Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];

    /// <summary>The duration, or <see cref="TimeSpan.Zero"/> when it cannot be told.</summary>
    public static TimeSpan Estimate(SynthesizedAudio audio)
    {
        try
        {
            return audio.Format switch
            {
                AudioFormat.Wav => FromWav(audio.Data),
                AudioFormat.Mp3 => FromMp3(audio.Data),
                _ => TimeSpan.Zero,
            };
        }
        catch (InvalidDataException)
        {
            return TimeSpan.Zero;
        }
    }

    private static TimeSpan FromWav(byte[] data)
    {
        var info = Wav.Parse(data);
        var bytesPerSecond = (long)info.Format.SampleRate * info.Format.Channels * Math.Max(8, info.Format.BitsPerSample) / 8;
        return bytesPerSecond <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(info.DataLength / (double)bytesPerSecond);
    }

    /// <summary>Constant-bitrate estimate from the first MPEG layer III frame header.</summary>
    private static TimeSpan FromMp3(byte[] data)
    {
        var offset = 0;
        if (data.Length >= 10 && data[0] == 'I' && data[1] == 'D' && data[2] == '3')
            offset = 10 + ((data[6] & 0x7F) << 21 | (data[7] & 0x7F) << 14 | (data[8] & 0x7F) << 7 | (data[9] & 0x7F));
        for (var i = offset; i + 3 < data.Length; i++)
        {
            if (data[i] != 0xFF || (data[i + 1] & 0xE0) != 0xE0) continue;
            var version = (data[i + 1] >> 3) & 0x03; // 3 = MPEG1, 2 = MPEG2, 0 = MPEG2.5
            var layer = (data[i + 1] >> 1) & 0x03;    // 1 = layer III
            var bitrateIndex = data[i + 2] >> 4;
            if (layer != 1 || version == 1 || bitrateIndex is 0 or 15) continue;
            var kbps = (version == 3 ? Mpeg1Layer3Bitrates : Mpeg2Layer3Bitrates)[bitrateIndex];
            return TimeSpan.FromSeconds((data.Length - i) * 8.0 / (kbps * 1000.0));
        }
        return TimeSpan.Zero;
    }
}
