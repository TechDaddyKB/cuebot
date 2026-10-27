namespace Cuebot.Speech.Tests.Fakes;

/// <summary>Microphone whose level the test sets by hand.</summary>
internal sealed class FakeAudioInput : IAudioInput
{
    private Action<double>? _listener;

    public string? MonitoredDevice { get; private set; }
    public int MonitorCount { get; private set; }
    public bool IsMonitoring => _listener is not null;

    public IReadOnlyList<AudioDevice> GetInputDevices() => [new("mic-1", "Fake mic", true)];

    public IDisposable MonitorLevel(string? deviceId, Action<double> onLevelDb)
    {
        MonitoredDevice = deviceId;
        MonitorCount++;
        _listener = onLevelDb;
        return new Stop(() => _listener = null);
    }

    public void Level(double db) => _listener?.Invoke(db);

    private sealed class Stop(Action stop) : IDisposable
    {
        public void Dispose() => stop();
    }
}

/// <summary>Clock moved by hand.</summary>
internal sealed class ManualTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
