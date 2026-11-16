using Cuebot.Speech.Tests.Fakes;

namespace Cuebot.Speech.Tests.Audio;

public sealed class PitchShifterTests
{
    private const int Rate = 22050;

    private static AudioClip Sine(double hz, double seconds)
    {
        var samples = new float[(int)(Rate * seconds)];
        for (var i = 0; i < samples.Length; i++) samples[i] = 0.5f * MathF.Sin((float)(2 * Math.PI * hz * i / Rate));
        return new AudioClip(samples, 1, Rate);
    }

    /// <summary>Frequency from zero crossings, ignoring the first and last 10 % (window edges).</summary>
    private static double Frequency(AudioClip clip)
    {
        var s = clip.Samples;
        int from = s.Length / 10, to = s.Length * 9 / 10, crossings = 0;
        for (var i = from + 1; i < to; i++) if (s[i - 1] < 0 && s[i] >= 0) crossings++;
        return crossings / ((double)(to - from) / Rate);
    }

    [Theory]
    [InlineData(4, 1.2)]
    [InlineData(-4, 0.8)]
    [InlineData(10, 1.5)]
    public void Shifts_the_pitch_and_keeps_the_length(int pitch, double ratio)
    {
        var input = Sine(220, 1.0);

        var output = PitchShifter.Shift(input, PitchShifter.Ratio(pitch));

        Assert.Equal(ratio, PitchShifter.Ratio(pitch), 3);
        Assert.Equal(input.Frames, output.Frames);
        Assert.InRange(Frequency(output), 220 * ratio * 0.95, 220 * ratio * 1.05);
        Assert.InRange(output.Samples.Max(), 0.3f, 0.6f);   // no silence, no blow-up
    }

    [Fact]
    public void Zero_pitch_leaves_the_audio_alone()
    {
        var wav = Sine(220, 0.2).ToWav16();
        Assert.Same(wav, PitchShifter.ShiftWav(wav, 0));
    }

    [Fact]
    public async Task Speech_shifts_the_pitch_for_engines_that_leave_it_to_Cuebot()
    {
        var engine = new FakeTtsEngine { PitchSupport = PitchSupport.Cuebot, Audio = Sine(220, 0.5).ToWav16() };
        await using var h = await SpeechHarness.StartAsync(engine);
        h.Settings.Aliases[0].Pitch = 4;

        await h.Service.SpeakAsync("hello", wait: true);

        var played = WavDecoder.Decode((await h.Player.NextAsync()).Wav);
        Assert.InRange(Frequency(played), 220 * 1.2 * 0.95, 220 * 1.2 * 1.05);
    }

    [Fact]
    public async Task Engines_with_their_own_pitch_get_the_audio_untouched()
    {
        var wav = Sine(220, 0.5).ToWav16();
        var engine = new FakeTtsEngine { Audio = wav };
        await using var h = await SpeechHarness.StartAsync(engine);
        h.Settings.Aliases[0].Pitch = 4;

        await h.Service.SpeakAsync("hello", wait: true);

        Assert.Equal(4, engine.History.Single().Pitch);
        Assert.InRange(Frequency(WavDecoder.Decode((await h.Player.NextAsync()).Wav)), 209, 231);
    }
}
