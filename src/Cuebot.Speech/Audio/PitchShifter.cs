namespace Cuebot.Speech;

/// <summary>
/// Changes the pitch of rendered speech while keeping its length, for engines that can't do it themselves (Windows
/// natural voices such as "Microsoft Ana Online" ignore SSML pitch; Piper has none). The audio is stretched in time by
/// the pitch ratio (WSOLA: overlapping windows, each placed where it lines up best with the last), then resampled back
/// to the original length, which moves every frequency by that ratio.
/// </summary>
public static class PitchShifter
{
    /// <summary>The pitch ratio for Cuebot's -10..10 pitch: the same ±50 % as engines that take SSML pitch.</summary>
    public static double Ratio(int pitch) => 1 + Prosody.PitchPercent(pitch) / 100.0;

    /// <summary>Returns <paramref name="wav"/> with its pitch moved by <paramref name="pitch"/> (-10..10); unchanged for 0.</summary>
    /// <exception cref="InvalidDataException">Not a WAV file.</exception>
    public static byte[] ShiftWav(byte[] wav, int pitch)
    {
        if (pitch == 0) return wav;
        var clip = WavDecoder.Decode(wav);
        return Shift(clip, Ratio(pitch)).ToWav16();
    }

    /// <summary>Moves every frequency by <paramref name="ratio"/> (2 = an octave up) and keeps the length.</summary>
    public static AudioClip Shift(AudioClip clip, double ratio)
    {
        if (Math.Abs(ratio - 1) < 0.001 || clip.Frames == 0) return clip;
        var frames = clip.Frames;
        var channels = clip.Channels;
        var output = new float[frames * channels];
        for (var c = 0; c < channels; c++)
        {
            var mono = new float[frames];
            for (var i = 0; i < frames; i++) mono[i] = clip.Samples[i * channels + c];
            var shifted = Resample(Stretch(mono, ratio, clip.SampleRate), frames);
            for (var i = 0; i < frames; i++) output[i * channels + c] = shifted[i];
        }
        return new AudioClip(output, channels, clip.SampleRate);
    }

    /// <summary>WSOLA time stretch: the result is about <paramref name="factor"/> times as long, at the same pitch.</summary>
    internal static float[] Stretch(float[] input, double factor, int sampleRate)
    {
        var window = Math.Max(64, sampleRate * 30 / 1000 & ~1);   // 30 ms
        var hop = window / 2;                                      // output hop: 50 % overlap
        var tolerance = window / 4;                                // how far a window may move to line up
        var analysisHop = hop / factor;
        var length = (int)Math.Ceiling(input.Length * factor) + window;
        var output = new float[length];
        var weight = new float[length];
        var hann = new float[window];
        for (var i = 0; i < window; i++) hann[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / window);

        var previous = 0;   // where the last window was taken from
        for (var k = 0; ; k++)
        {
            var outAt = k * hop;
            var nominal = (int)Math.Round(k * analysisHop);
            if (nominal >= input.Length || outAt + window > length) break;
            var from = k == 0 ? 0 : BestOffset(input, previous + hop, nominal, tolerance, window);
            for (var i = 0; i < window; i++)
            {
                var source = from + i;
                if (source >= input.Length) break;
                output[outAt + i] += input[source] * hann[i];
                weight[outAt + i] += hann[i];
            }
            previous = from;
        }
        for (var i = 0; i < length; i++) if (weight[i] > 1e-3f) output[i] /= weight[i];
        return output.AsSpan(0, Math.Min(length, (int)Math.Ceiling(input.Length * factor))).ToArray();
    }

    /// <summary>The start near <paramref name="nominal"/> whose window best continues the one taken at <paramref name="natural"/>.</summary>
    private static int BestOffset(float[] input, int natural, int nominal, int tolerance, int window)
    {
        var best = Math.Clamp(nominal, 0, Math.Max(0, input.Length - 1));
        var bestScore = double.NegativeInfinity;
        var compare = window / 2;
        for (var candidate = Math.Max(0, nominal - tolerance); candidate <= nominal + tolerance; candidate += 2)
        {
            if (candidate + compare >= input.Length || natural + compare >= input.Length) break;
            double score = 0;
            for (var i = 0; i < compare; i += 2) score += input[natural + i] * input[candidate + i];
            if (score > bestScore)
            {
                bestScore = score;
                best = candidate;
            }
        }
        return best;
    }

    /// <summary>Linear resampling of <paramref name="input"/> to exactly <paramref name="length"/> samples.</summary>
    internal static float[] Resample(float[] input, int length)
    {
        var output = new float[length];
        if (input.Length == 0) return output;
        var step = (double)input.Length / length;
        for (var i = 0; i < length; i++)
        {
            var position = i * step;
            var index = (int)position;
            var fraction = (float)(position - index);
            var a = input[Math.Min(index, input.Length - 1)];
            var b = input[Math.Min(index + 1, input.Length - 1)];
            output[i] = a + (b - a) * fraction;
        }
        return output;
    }
}
