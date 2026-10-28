namespace Cuebot.VoiceControl.Audio;

/// <summary>
/// Streaming mono resampler to the recogniser's rate. Downsampling averages the input over each output sample's span
/// (a box filter, enough to keep speech free of aliasing at 48 → 16 kHz); upsampling interpolates linearly.
/// </summary>
internal sealed class Resampler
{
    private readonly double _step;
    private readonly bool _passThrough;
    private readonly bool _down;
    private double _accumulated;
    private double _filled;
    private float _previous;
    private double _position;

    public Resampler(int inputRate, int outputRate)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(inputRate);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(outputRate);
        InputRate = inputRate;
        OutputRate = outputRate;
        _passThrough = inputRate == outputRate;
        _down = inputRate > outputRate;
        // Downsampling: share of one output sample each input sample covers. Upsampling: input samples per output sample.
        _step = _down ? (double)outputRate / inputRate : (double)inputRate / outputRate;
    }

    public int InputRate { get; }
    public int OutputRate { get; }

    /// <summary>Converts <paramref name="input"/> and appends the result to <paramref name="output"/>.</summary>
    public void Process(ReadOnlySpan<float> input, List<float> output)
    {
        if (_passThrough)
        {
            foreach (var s in input) output.Add(s);
            return;
        }
        if (_down)
        {
            foreach (var sample in input)
            {
                var remaining = _step;
                while (remaining > 1e-12)
                {
                    var take = Math.Min(remaining, 1 - _filled);
                    _accumulated += sample * take;
                    _filled += take;
                    remaining -= take;
                    if (_filled < 1 - 1e-9) continue;
                    output.Add((float)_accumulated);
                    _accumulated = 0;
                    _filled = 0;
                }
            }
            return;
        }
        foreach (var sample in input)
        {
            while (_position <= 1)
            {
                output.Add((float)(_previous + (sample - _previous) * _position));
                _position += _step;
            }
            _position -= 1;
            _previous = sample;
        }
    }
}
