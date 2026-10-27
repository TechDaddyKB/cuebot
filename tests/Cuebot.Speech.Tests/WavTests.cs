using System.Buffers.Binary;

namespace Cuebot.Speech.Tests;

public class WavTests
{
    private static byte[] Pcm(params short[] samples)
    {
        var bytes = new byte[samples.Length * 2];
        for (var i = 0; i < samples.Length; i++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), samples[i]);
        return bytes;
    }

    private static short[] Samples(byte[] wav)
    {
        var info = Wav.Parse(wav);
        var data = wav.AsSpan(info.DataOffset, info.DataLength);
        var samples = new short[data.Length / 2];
        for (var i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadInt16LittleEndian(data[(i * 2)..]);
        return samples;
    }

    [Fact]
    public void FromPcm16_writes_a_header_that_parses_back()
    {
        var wav = Wav.FromPcm16(Pcm(1, -2, 3, -4), sampleRate: 22050, channels: 2);

        var info = Wav.Parse(wav);

        Assert.Equal(44 + 8, wav.Length);
        Assert.Equal(new WavFormat(WavFormat.PcmFormat, 2, 22050, 16), info.Format);
        Assert.True(info.Format.IsPcm16);
        Assert.Equal(44, info.DataOffset);
        Assert.Equal(8, info.DataLength);
        Assert.Equal(22050 * 2 * 2, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(28)));
        Assert.Equal(new short[] { 1, -2, 3, -4 }, Samples(wav));
    }

    [Fact]
    public void FromPcm16_drops_a_trailing_half_sample()
    {
        var wav = Wav.FromPcm16(new byte[] { 1, 0, 7 }, 8000, 1);

        Assert.Equal(2, Wav.Parse(wav).DataLength);
    }

    [Fact]
    public void Parse_skips_unknown_chunks_and_clamps_oversized_data()
    {
        var wav = Wav.FromPcm16(Pcm(5, 6), 8000, 1).ToList();
        // Insert an odd-sized "LIST" chunk (with pad byte) between fmt and data.
        wav.InsertRange(36, [.. "LIST"u8.ToArray(), 3, 0, 0, 0, 9, 9, 9, 0]);
        var bytes = wav.ToArray();
        // Streaming encoders write a placeholder data size.
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(36 + 12 + 4), 0xFFFFFFFF);

        var info = Wav.Parse(bytes);

        Assert.Equal(56, info.DataOffset);
        Assert.Equal(4, info.DataLength);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 })]
    [InlineData(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'A', (byte)'V', (byte)'I', (byte)' ' })]
    public void Parse_rejects_non_wav_data(byte[] data) => Assert.Throws<InvalidDataException>(() => Wav.Parse(data));

    [Fact]
    public void Parse_rejects_a_wav_without_data()
    {
        var header = Wav.FromPcm16([], 8000, 1)[..36];

        Assert.Throws<InvalidDataException>(() => Wav.Parse(header));
    }

    [Theory]
    [InlineData(100, 1000, -1000)]
    [InlineData(50, 500, -500)]
    [InlineData(0, 0, 0)]
    [InlineData(250, 1000, -1000)]
    [InlineData(-5, 0, 0)]
    public void ApplyVolume_scales_and_clamps(int volume, short expectedPositive, short expectedNegative)
    {
        var wav = Wav.FromPcm16(Pcm(1000, -1000), 8000, 1);

        Assert.Equal(new[] { expectedPositive, expectedNegative }, Samples(Wav.ApplyVolume(wav, volume)));
    }

    [Fact]
    public void ApplyVolume_does_not_modify_the_input()
    {
        var wav = Wav.FromPcm16(Pcm(1000), 8000, 1);

        Wav.ApplyVolume(wav, 10);

        Assert.Equal(new short[] { 1000 }, Samples(wav));
    }

    [Fact]
    public void ApplyVolume_leaves_other_formats_untouched()
    {
        var wav = Wav.FromPcm16(Pcm(1000), 8000, 1);
        BinaryPrimitives.WriteInt16LittleEndian(wav.AsSpan(34), 8); // pretend 8-bit

        Assert.Same(wav, Wav.ApplyVolume(wav, 10));
    }

    [Fact]
    public void Normalize_fixes_placeholder_sizes()
    {
        var wav = Wav.FromPcm16(Pcm(1, 2), 8000, 1);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(4), int.MaxValue);
        BinaryPrimitives.WriteInt32LittleEndian(wav.AsSpan(40), int.MaxValue);

        var normalized = Wav.Normalize(wav);

        Assert.Equal(36 + 4, BinaryPrimitives.ReadInt32LittleEndian(normalized.AsSpan(4)));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(normalized.AsSpan(40)));
    }
}
