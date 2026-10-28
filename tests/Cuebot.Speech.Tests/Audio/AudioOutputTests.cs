using Cuebot.Core.Logging;
using Cuebot.Speech.Tests.Fakes;

namespace Cuebot.Speech.Tests.Audio;

public class AudioOutputTests
{
    private readonly FakeAudioBackend _backend = new();
    private readonly AppLog _log = new();
    private readonly AudioOutput _output;

    public AudioOutputTests() => _output = new AudioOutput(_backend, _log);

    [Fact]
    public void Lists_devices_from_the_backend()
    {
        Assert.Equal(["Speakers", "Headset"], _output.GetOutputDevices().Select(d => d.Id));
        Assert.Equal(["Mic"], _output.GetInputDevices().Select(d => d.Id));
        Assert.Equal("fake", _output.BackendName);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("Default", null)]
    [InlineData("Application Default", null)]
    [InlineData("Headset", "Headset")]
    [InlineData("headset", "Headset")]
    public async Task Plays_on_the_selected_device(string? requested, string? expected)
    {
        var play = _output.PlayAsync(TestAudio.Wav16(10), new PlaybackOptions(requested, 50), CancellationToken.None);
        var voice = (await TestAudio.VoicesAsync(_backend, 1))[0];
        Assert.Equal(expected, voice.DeviceId);
        Assert.Equal(0.5f, voice.Gain);
        voice.Finish();
        await TestAudio.Within(play);
    }

    [Fact]
    public async Task Unknown_device_falls_back_to_default_and_warns_once()
    {
        for (var i = 0; i < 2; i++)
        {
            var play = _output.PlayAsync(TestAudio.Wav16(10), new PlaybackOptions("Unplugged USB"), CancellationToken.None);
            var voice = (await TestAudio.VoicesAsync(_backend, i + 1))[i];
            Assert.Null(voice.DeviceId);
            voice.Finish();
            await TestAudio.Within(play);
        }
        Assert.Single(_log.Recent(), e => e.Level == LogLevel.Warning && e.Message.Contains("Unplugged USB"));
    }

    [Fact]
    public async Task Sounds_play_concurrently_and_cancelling_one_leaves_the_other()
    {
        using var cancelFirst = new CancellationTokenSource();
        var first = _output.PlayAsync(TestAudio.Wav16(10), PlaybackOptions.Default, cancelFirst.Token);
        var second = _output.PlayAsync(TestAudio.Wav16(10), PlaybackOptions.Default, CancellationToken.None);
        var voices = await TestAudio.VoicesAsync(_backend, 2);

        cancelFirst.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TestAudio.Within(first));
        Assert.True(voices[0].Stopped);
        Assert.False(voices[1].Stopped);
        Assert.False(second.IsCompleted);

        voices[1].Finish();
        await TestAudio.Within(second);
    }

    [Fact]
    public async Task Already_cancelled_token_plays_nothing()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _output.PlayAsync(TestAudio.Wav16(10), PlaybackOptions.Default, new CancellationToken(canceled: true)));
        Assert.Empty(_backend.Voices);
    }

    [Fact]
    public void Wav_is_decoded_in_managed_code()
    {
        var clip = _output.Decode(TestAudio.Wav16(250, short.MaxValue));
        Assert.Equal(TimeSpan.FromMilliseconds(250), clip.Duration);
        Assert.Equal(0, _backend.DecodeCalls);
    }

    [Fact]
    public void Mp3_and_flac_use_the_native_decoder_when_available()
    {
        var mp3 = new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 0, 0 };
        Assert.Throws<NotSupportedException>(() => _output.Decode(mp3));

        var decoded = new AudioClip(new float[48000 * 2], 2, 48000);
        _backend.Decoder = _ => decoded;
        Assert.Same(decoded, _output.Decode(mp3));
        Assert.Same(decoded, _output.Decode("fLaC\0\0\0\0"u8.ToArray()));
    }

    [Fact]
    public void Compressed_wav_falls_back_to_the_native_decoder()
    {
        var adpcm = TestAudio.Wav16(10);
        adpcm[20] = 2; // MS ADPCM format code
        Assert.Throws<NotSupportedException>(() => _output.Decode(adpcm));

        var decoded = new AudioClip(new float[10], 1, 1000);
        _backend.Decoder = _ => decoded;
        Assert.Same(decoded, _output.Decode(adpcm));
    }

    [Fact]
    public void Unknown_and_opus_data_are_rejected()
    {
        Assert.Throws<NotSupportedException>(() => _output.Decode([1, 2, 3]));
        Assert.Throws<NotSupportedException>(() => _output.Decode("OggS\0\0\0\0OpusHead"u8.ToArray()));
    }

    [Fact]
    public void Level_monitor_reports_dbfs_until_disposed()
    {
        var levels = new List<double>();
        using (var monitor = _output.MonitorLevel("mic", levels.Add))
        {
            var capture = _backend.Capture!;
            Assert.Equal("Mic", capture.DeviceId);
            capture.Feed(Enumerable.Repeat(1f, 100).ToArray()); // window = 50 ms at 1 kHz = 50 samples
            Assert.Equal([0d, 0d], levels);
        }
        Assert.True(_backend.Capture!.Disposed);
    }

    [Fact]
    public void Sample_capture_streams_raw_samples_from_the_resolved_device()
    {
        var received = new List<float>();
        using (var stream = _output.CaptureSamples("mic", samples => received.AddRange(samples.ToArray())))
        {
            Assert.NotNull(stream);
            Assert.Equal(1000, stream.SampleRate);
            Assert.Equal("Mic", _backend.Capture!.DeviceId);
            _backend.Capture.Feed([0.25f, -0.5f]);
        }
        Assert.Equal([0.25f, -0.5f], received);
        Assert.True(_backend.Capture!.Disposed);
    }

    [Fact]
    public void Sample_capture_is_null_when_the_backend_cannot_capture()
    {
        _backend.SupportsCapture = false;
        Assert.Null(_output.CaptureSamples(null, _ => { }));
        IAudioInput withoutCapture = new InputWithoutSamples();
        Assert.Null(withoutCapture.CaptureSamples(null, _ => { }));
    }

    private sealed class InputWithoutSamples : IAudioInput
    {
        public IReadOnlyList<AudioDevice> GetInputDevices() => [];
        public IDisposable MonitorLevel(string? deviceId, Action<double> onLevelDb) => throw new NotSupportedException();
    }

    [Fact]
    public void Level_monitor_without_capture_is_a_harmless_no_op()
    {
        _backend.SupportsCapture = false;
        using var monitor = _output.MonitorLevel(null, _ => throw new InvalidOperationException());
        Assert.Contains(_log.Recent(), e => e.Level == LogLevel.Warning && e.Message.Contains("not available"));
    }

    [Fact]
    public void Backend_is_created_lazily_and_disposed_with_the_output()
    {
        var created = 0;
        var output = new AudioOutput(() => { created++; return _backend; });
        Assert.Equal(0, created);
        output.GetOutputDevices();
        output.GetOutputDevices();
        Assert.Equal(1, created);
        output.Dispose();
        Assert.True(_backend.Disposed);
    }

    [Fact]
    public void Device_listing_failures_return_an_empty_list()
    {
        var output = new AudioOutput(() => throw new InvalidOperationException("boom"), _log);
        Assert.Empty(output.GetOutputDevices());
    }

    [Fact]
    public async Task Fallback_backend_converts_clips_to_wav_and_stops_on_request()
    {
        var player = new FakeAudioPlayer { Block = true };
        var backend = new LegacyAudioBackend(player, "legacy", canMix: false);
        var voice = backend.Play(new AudioClip([0.5f, 0.5f], 1, 8000), null, 0.5f);

        var playback = await player.NextAsync();
        var info = Wav.Parse(playback.Wav);
        Assert.Equal(8000, info.Format.SampleRate);
        Assert.InRange((int)BitConverter.ToInt16(playback.Wav, info.DataOffset), 8190, 8193); // 0.5 * 0.5 * 32767

        voice.Stop();
        await TestAudio.Within(voice.Completion);
        Assert.Empty(backend.GetInputDevices());
        Assert.Null(backend.StartCapture(null, _ => { }));
    }
}
