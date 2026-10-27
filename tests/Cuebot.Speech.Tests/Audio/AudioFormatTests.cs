using System.Buffers.Binary;
using Cuebot.Core.Logging;

namespace Cuebot.Speech.Tests.Audio;

public class AudioFormatTests
{
    [Theory]
    [InlineData(new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x41, 0x56, 0x45 }, AudioFileFormat.Wav)]
    [InlineData(new byte[] { 0x66, 0x4C, 0x61, 0x43, 0, 0 }, AudioFileFormat.Flac)]
    [InlineData(new byte[] { 0x49, 0x44, 0x33, 3, 0 }, AudioFileFormat.Mp3)]
    [InlineData(new byte[] { 0xFF, 0xFB, 0x90, 0x64 }, AudioFileFormat.Mp3)] // MPEG-1 layer III, 128 kbit/s, 44.1 kHz
    [InlineData(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF }, AudioFileFormat.Unknown)] // bad bitrate/sample rate
    [InlineData(new byte[] { 0xFF, 0xF9, 0x50, 0x80 }, AudioFileFormat.Unknown)] // ADTS AAC (layer 0)
    [InlineData(new byte[] { 1, 2, 3 }, AudioFileFormat.Unknown)]
    [InlineData(new byte[0], AudioFileFormat.Unknown)]
    public void Detects_formats_by_content(byte[] data, AudioFileFormat expected) =>
        Assert.Equal(expected, AudioFormatDetector.Detect(data));

    [Fact]
    public void Detects_the_codec_inside_ogg()
    {
        Assert.Equal(AudioFileFormat.OggVorbis, AudioFormatDetector.Detect(Ogg("\u0001vorbis"u8)));
        Assert.Equal(AudioFileFormat.OggOpus, AudioFormatDetector.Detect(Ogg("OpusHead"u8)));
        Assert.Equal(AudioFileFormat.Flac, AudioFormatDetector.Detect(Ogg("\u007FFLAC"u8)));
        Assert.Equal(AudioFileFormat.Unknown, AudioFormatDetector.Detect(Ogg("Speex   "u8)));

        static byte[] Ogg(ReadOnlySpan<byte> codec) => [.. "OggS"u8, .. new byte[24], .. codec, 0, 0];
    }

    [Theory]
    [InlineData("a.WAV", true)]
    [InlineData("b.mp3", true)]
    [InlineData("c.ogg", true)]
    [InlineData("d.flac", true)]
    [InlineData("e.txt", false)]
    public void Knows_supported_extensions(string file, bool expected) =>
        Assert.Equal(expected, AudioFormatDetector.HasSupportedExtension(file));

    [Fact]
    public void Decodes_pcm16_wav()
    {
        var clip = WavDecoder.Decode(Wav.FromPcm16([0x00, 0x40, 0x00, 0xC0], 8000, 2));
        Assert.Equal(2, clip.Channels);
        Assert.Equal(8000, clip.SampleRate);
        Assert.Equal([0.5f, -0.5f], clip.Samples);
        Assert.Equal(1, clip.Frames);
    }

    [Theory]
    [InlineData(1, 8, new byte[] { 0xC0 }, 0.5f)]
    [InlineData(1, 24, new byte[] { 0, 0, 0xC0 }, -0.5f)]
    [InlineData(1, 32, new byte[] { 0, 0, 0, 0x40 }, 0.5f)]
    [InlineData(3, 32, new byte[] { 0, 0, 0x80, 0x3E }, 0.25f)]
    [InlineData(3, 64, new byte[] { 0, 0, 0, 0, 0, 0, 0xD0, 0xBF }, -0.25f)]
    public void Decodes_other_sample_formats(int code, int bits, byte[] sample, float expected)
    {
        var clip = WavDecoder.Decode(BuildWav(code, bits, sample));
        Assert.Equal(expected, clip.Samples[0], 4);
    }

    [Fact]
    public void Decodes_wave_format_extensible()
    {
        var clip = WavDecoder.Decode(BuildWav(1, 16, [0x00, 0x40], extensibleSubFormat: 1));
        Assert.Equal(0.5f, clip.Samples[0]);
    }

    [Fact]
    public void Rejects_compressed_and_broken_wav()
    {
        Assert.Throws<NotSupportedException>(() => WavDecoder.Decode(BuildWav(2, 4, [0, 0])));
        Assert.Throws<InvalidDataException>(() => WavDecoder.Decode([1, 2, 3, 4]));
    }

    [Fact]
    public void Invalid_ogg_vorbis_is_reported_as_invalid_data()
    {
        var bogus = new byte[64];
        "OggS"u8.CopyTo(bogus);
        Assert.ThrowsAny<Exception>(() => OggVorbisDecoder.Decode(bogus));
    }

    [Fact]
    public void Converter_upmixes_mono_and_downmixes_stereo()
    {
        var stereo = PcmConverter.Convert(new AudioClip([0.2f, 0.4f], 1, 100), 100, 2);
        Assert.Equal([0.2f, 0.2f, 0.4f, 0.4f], stereo.Samples);

        var mono = PcmConverter.Convert(new AudioClip([0.2f, 0.4f], 2, 100), 100, 1);
        Assert.Equal(0.3f, mono.Samples.Single(), 5);
    }

    [Fact]
    public void Converter_resamples_with_linear_interpolation_keeping_duration()
    {
        var clip = new AudioClip([0f, 1f, 0f, 1f], 1, 1000);
        var up = PcmConverter.Convert(clip, 2000, 1);
        Assert.Equal(8, up.Frames);
        Assert.Equal(clip.Duration, up.Duration);
        Assert.Equal(0.5f, up.Samples[1], 5);

        var down = PcmConverter.Convert(new AudioClip(new float[48000], 1, 48000), 22050, 1);
        Assert.Equal(22050, down.Frames);
        Assert.Same(clip, PcmConverter.Convert(clip, 1000, 1));
    }

    [Fact]
    public void Clip_round_trips_through_wav16_with_volume()
    {
        var clip = new AudioClip([1f, -1f, 0.5f, 2f], 2, 44100);
        var wav = clip.ToWav16(50);
        var back = WavDecoder.Decode(wav);
        Assert.Equal(2, back.Channels);
        Assert.Equal(44100, back.SampleRate);
        Assert.Equal(0.5f, back.Samples[0], 3);
        Assert.Equal(-0.5f, back.Samples[1], 3);
        Assert.Equal(0.25f, back.Samples[2], 3);
        Assert.Equal(1f, back.Samples[3], 3); // clamped
    }

    [Theory]
    [InlineData(1.0, 0)]
    [InlineData(0.5, -6.02)]
    [InlineData(0.0, -96)]
    [InlineData(1e-9, -96)]
    public void Converts_amplitude_to_dbfs(double amplitude, double expected) =>
        Assert.Equal(expected, AudioLevelMeter.ToDbfs(amplitude), 2);

    [Fact]
    public void Level_meter_reports_rms_per_window()
    {
        var levels = new List<double>();
        var meter = new AudioLevelMeter(1000, levels.Add, windowSeconds: 0.1);
        var sine = Enumerable.Range(0, 1000).Select(i => (float)Math.Sin(2 * Math.PI * 50 * i / 1000)).ToArray();
        meter.Process(sine.AsSpan(0, 250));
        meter.Process(sine.AsSpan(250));
        Assert.Equal(10, levels.Count);
        Assert.All(levels, level => Assert.Equal(-3.01, level, 1));

        levels.Clear();
        meter.Process(new float[100]);
        Assert.Equal([-96d], levels);
    }

    [Fact]
    public void Device_ids_are_unique_names()
    {
        var devices = AudioDeviceSelector.WithUniqueIds([("Speakers", true), ("Speakers", false), (" ", false), ("speakers", false)]);
        Assert.Equal(["Speakers", "Speakers (2)", "Unnamed device", "speakers (3)"], devices.Select(d => d.Id));
        Assert.True(devices[0].IsDefault);
        Assert.Equal("Speakers", devices[1].Name);
    }

    [Fact]
    public void Device_selector_matches_id_then_name()
    {
        AudioDevice[] devices = [new("id-1", "Speakers", true), new("id-2", "Headset", false)];
        Assert.Equal("id-2", AudioDeviceSelector.Resolve(devices, "id-2")?.Id);
        Assert.Equal("id-2", AudioDeviceSelector.Resolve(devices, " HEADSET ")?.Id);
        Assert.Null(AudioDeviceSelector.Resolve(devices, "Default"));
        Assert.Null(AudioDeviceSelector.Resolve(devices, "missing"));
    }

    [Fact]
    public void Native_library_failure_falls_back_and_logs_why()
    {
        var log = new AppLog();
        var fallback = new FakeAudioBackend();
        var chosen = AudioPlayerFactory.SelectBackend(null,
            () => throw new TypeInitializationException("SoundFlow.Native", new DllNotFoundException("libminiaudio.so")),
            () => fallback, log);
        Assert.Same(fallback, chosen);
        Assert.Contains(log.Recent(), e => e.Level == LogLevel.Warning && e.Message.Contains("DllNotFoundException: libminiaudio.so"));
    }

    [Fact]
    public void Native_backend_is_preferred_and_logged()
    {
        var log = new AppLog();
        var native = new FakeAudioBackend();
        Assert.Same(native, AudioPlayerFactory.SelectBackend(null, () => native, () => throw new InvalidOperationException(), log));
        Assert.Contains(log.Recent(), e => e.Message == "Audio backend: fake");
    }

    [Theory]
    [InlineData("fallback")]
    [InlineData(" Legacy ")]
    public void Environment_variable_forces_the_fallback(string value)
    {
        var fallback = new FakeAudioBackend();
        Assert.Same(fallback, AudioPlayerFactory.SelectBackend(value, () => throw new InvalidOperationException("must not load"), () => fallback, null));
    }

    /// <summary>A WAV with a raw format code and one sample, optionally as WAVE_FORMAT_EXTENSIBLE.</summary>
    private static byte[] BuildWav(int code, int bits, byte[] sample, int? extensibleSubFormat = null)
    {
        var fmtSize = extensibleSubFormat is null ? 16 : 40;
        var wav = new byte[12 + 8 + fmtSize + 8 + sample.Length];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        BinaryPrimitives.WriteInt32LittleEndian(span[4..], wav.Length - 8);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[16..], fmtSize);
        BinaryPrimitives.WriteInt16LittleEndian(span[20..], (short)(extensibleSubFormat is null ? code : 0xFFFE));
        BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        BinaryPrimitives.WriteInt32LittleEndian(span[24..], 8000);
        BinaryPrimitives.WriteInt32LittleEndian(span[28..], 8000 * bits / 8);
        BinaryPrimitives.WriteInt16LittleEndian(span[32..], (short)Math.Max(1, bits / 8));
        BinaryPrimitives.WriteInt16LittleEndian(span[34..], (short)bits);
        if (extensibleSubFormat is { } sub)
        {
            BinaryPrimitives.WriteInt16LittleEndian(span[36..], 22);
            BinaryPrimitives.WriteInt16LittleEndian(span[44..], (short)sub);
        }
        var data = 20 + fmtSize;
        "data"u8.CopyTo(span[data..]);
        BinaryPrimitives.WriteInt32LittleEndian(span[(data + 4)..], sample.Length);
        sample.CopyTo(span[(data + 8)..]);
        return wav;
    }
}
