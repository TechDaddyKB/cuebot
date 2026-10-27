using Cuebot.Core.Logging;
using Cuebot.Speech.Tests.Fakes;

namespace Cuebot.Speech.Tests;

public class VoiceGateTests
{
    private static readonly VoiceGateProfile Profile = new() { Name = "Mic", DeviceId = "mic-1", ThresholdDb = -30, ReleaseMilliseconds = 500 };

    [Fact]
    public void Holds_while_loud_and_releases_after_the_release_time()
    {
        using var log = new AppLog();
        var input = new FakeAudioInput();
        var time = new ManualTime();
        using var gate = new VoiceGate(input, log, time);
        var changes = 0;
        gate.Changed += () => changes++;

        gate.Apply(Profile);
        Assert.Equal("mic-1", input.MonitoredDevice);
        Assert.False(gate.IsHolding);

        input.Level(-50);
        Assert.False(gate.IsHolding);
        input.Level(-20);
        Assert.True(gate.IsHolding);
        time.Advance(TimeSpan.FromMilliseconds(300));
        input.Level(-60);
        Assert.True(gate.IsHolding); // not quiet for long enough yet
        time.Advance(TimeSpan.FromMilliseconds(250));
        input.Level(-60);
        Assert.False(gate.IsHolding);
        Assert.Equal(2, changes);
    }

    [Fact]
    public void Releases_when_the_input_stops_reporting()
    {
        using var log = new AppLog();
        var input = new FakeAudioInput();
        var time = new ManualTime();
        using var gate = new VoiceGate(input, log, time);
        gate.Apply(Profile);
        input.Level(0);

        time.Advance(TimeSpan.FromMilliseconds(500));
        gate.CheckStale();
        Assert.True(gate.IsHolding);
        time.Advance(VoiceGate.StaleAfter);
        gate.CheckStale();
        Assert.False(gate.IsHolding);
    }

    [Fact]
    public void Switching_off_stops_monitoring_and_opens()
    {
        using var log = new AppLog();
        var input = new FakeAudioInput();
        using var gate = new VoiceGate(input, log, new ManualTime());
        gate.Apply(Profile);
        input.Level(0);

        gate.Apply(null);

        Assert.False(gate.IsHolding);
        Assert.False(input.IsMonitoring);
        Assert.Null(gate.ActiveProfile);
        input.Level(0);
        Assert.False(gate.IsHolding);
    }

    [Fact]
    public void Without_audio_input_the_gate_never_holds()
    {
        using var log = new AppLog();
        using var gate = new VoiceGate(null, log);

        gate.Apply(Profile);
        gate.OnLevel(0);

        Assert.False(gate.IsAvailable);
        Assert.False(gate.IsHolding);
    }

    [Fact]
    public async Task Service_holds_the_queue_while_the_streamer_talks()
    {
        var input = new FakeAudioInput();
        await using var h = new SpeechHarness(input, new FakeTtsEngine(voices: "v1"));
        h.Settings.VoiceGate.Profiles.Add(Profile);
        h.Settings.VoiceGate.ActiveProfile = "mic";
        await h.Service.StartAsync();
        Assert.True(h.Service.VoiceGateAvailable);
        Assert.Equal(1, input.MonitorCount);

        input.Level(-10);
        Assert.True(h.Service.VoiceGateHolding);
        var spoken = h.Service.SpeakAsync("after you", wait: true);
        await Task.Delay(100);
        Assert.Empty(h.Engine.History);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        input.Level(-80);
        await spoken.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(h.Engine.History);
    }

    [Fact]
    public async Task Profiles_are_activated_by_name()
    {
        var input = new FakeAudioInput();
        await using var h = new SpeechHarness(input, new FakeTtsEngine(voices: "v1"));
        h.Settings.VoiceGate.Profiles.Add(Profile);

        Assert.False(h.Service.ActivateVoiceGateProfile("unknown"));
        Assert.True(h.Service.ActivateVoiceGateProfile("MIC"));
        Assert.Equal("Mic", h.Settings.VoiceGate.ActiveProfile);
        Assert.True(input.IsMonitoring);
        Assert.True(h.Service.ActivateVoiceGateProfile("none"));
        Assert.Equal("", h.Settings.VoiceGate.ActiveProfile);
        Assert.False(input.IsMonitoring);
    }
}
