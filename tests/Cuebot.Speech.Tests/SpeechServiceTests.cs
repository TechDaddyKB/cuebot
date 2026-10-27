using System.Buffers.Binary;
using Cuebot.Core.Events;
using Cuebot.Speech.Tests.Fakes;

namespace Cuebot.Speech.Tests;

public class SpeechServiceTests
{
    [Fact]
    public async Task Speaks_queued_texts_in_order()
    {
        await using var h = await SpeechHarness.StartAsync();

        _ = h.Service.SpeakAsync("one");
        _ = h.Service.SpeakAsync("two");
        await h.Service.SpeakAsync("three", wait: true);

        Assert.Equal(["one", "two", "three"], h.Engine.History.Select(c => c.Text));
    }

    [Fact]
    public async Task Uses_alias_voice_and_rate_and_applies_replacements()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Settings.Aliases.Add(new VoiceAlias { Name = "Fast", EngineId = "fake", VoiceId = "v2", Rate = 5 });
        h.Settings.Replacements.Add(new WordReplacement { Find = "gg", Replace = "good game" });

        await h.Service.SpeakAsync("gg   all", "fast", wait: true);

        Assert.Equal(new SynthesisCall("good game all", "v2", 5), h.Engine.History.Single());
    }

    [Fact]
    public async Task Volume_is_alias_volume_times_master_volume()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Settings.Aliases[0].Volume = 50;
        h.Settings.MasterVolume = 50;

        await h.Service.SpeakAsync("quiet", wait: true);

        var playback = await h.Player.NextAsync();
        var info = Wav.Parse(playback.Wav);
        Assert.Equal(FakeTtsEngine.SampleValue / 4, BinaryPrimitives.ReadInt16LittleEndian(playback.Wav.AsSpan(info.DataOffset)));
    }

    [Fact]
    public async Task Skip_ends_the_current_text_and_continues_with_the_next()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Player.Block = true;

        var first = h.Service.SpeakAsync("first", wait: true);
        var second = h.Service.SpeakAsync("second", wait: true);
        await h.Player.NextAsync();
        Assert.Equal("first", h.Service.CurrentText);
        Assert.Equal(1, h.Service.QueueLength);

        h.Service.SkipCurrent();
        await first; // a skipped text counts as done
        var next = await h.Player.NextAsync();
        Assert.Equal("second", h.Service.CurrentText);
        next.Release();
        await second;

        Assert.Null(h.Service.CurrentText);
    }

    [Fact]
    public async Task ClearQueue_cancels_waiting_texts_but_not_the_current_one()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Player.Block = true;

        var current = h.Service.SpeakAsync("current", wait: true);
        var waiting = h.Service.SpeakAsync("waiting", wait: true);
        var playback = await h.Player.NextAsync();

        h.Service.ClearQueue();

        Assert.Equal(0, h.Service.QueueLength);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        playback.Release();
        await current;
        Assert.Equal(["current"], h.Engine.History.Select(c => c.Text));
    }

    [Fact]
    public async Task Paused_queue_waits_until_resumed()
    {
        await using var h = await SpeechHarness.StartAsync();
        var changes = 0;
        h.Service.StateChanged += () => Interlocked.Increment(ref changes);

        h.Service.Paused = true;
        var spoken = h.Service.SpeakAsync("later", wait: true);
        await Task.Delay(100);

        Assert.Empty(h.Engine.History);
        Assert.Equal(1, h.Service.QueueLength);
        Assert.True(h.Service.Paused);

        h.Service.Paused = false;
        await spoken.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(h.Engine.History);
        Assert.True(changes >= 3);
    }

    [Fact]
    public async Task Cancelling_a_waiting_caller_stops_its_playback()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Player.Block = true;
        using var cts = new CancellationTokenSource();

        var spoken = h.Service.SpeakAsync("stop me", wait: true, ct: cts.Token);
        await h.Player.NextAsync();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => spoken);
    }

    [Fact]
    public async Task Synthesis_errors_reach_waiting_callers_and_the_queue_keeps_going()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Engine.Failure = new InvalidOperationException("boom");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Service.SpeakAsync("x", wait: true));
        Assert.Equal("boom", error.Message);

        h.Engine.Failure = null;
        await h.Service.SpeakAsync("y", wait: true).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Publishes_started_and_finished_events()
    {
        await using var h = await SpeechHarness.StartAsync();
        var events = new List<BotEvent>();
        using var _ = h.Bus.Subscribe(e => { lock (events) events.Add(e); });

        await h.Service.SpeakAsync("hello", "main", wait: true);

        lock (events)
        {
            Assert.Equal([SpeechEvents.Started, SpeechEvents.Finished], events.Select(e => e.Type));
            Assert.All(events, e =>
            {
                Assert.Equal("hello", e.Args["text"]);
                Assert.Equal("Main", e.Args["alias"]);
            });
        }
    }

    [Fact]
    public async Task TestVoiceAsync_uses_the_given_alias_even_if_unsaved()
    {
        await using var h = await SpeechHarness.StartAsync();

        await h.Service.TestVoiceAsync(new VoiceAlias { Name = "Draft", EngineId = "fake", VoiceId = "draft", Rate = -3 }, "test");

        Assert.Equal(new SynthesisCall("test", "draft", -3), h.Engine.History.Single());
    }

    [Fact]
    public async Task TestVoiceAsync_reports_unavailable_engines()
    {
        await using var h = await SpeechHarness.StartAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            h.Service.TestVoiceAsync(new VoiceAlias { Name = "Ghost", EngineId = "nope" }, "test"));
    }

    [Theory]
    [InlineData(100, 100, 100)]
    [InlineData(50, 50, 25)]
    [InlineData(150, 80, 80)]
    [InlineData(100, -10, 0)]
    public void CombineVolume_multiplies_and_clamps(int alias, int master, int expected) =>
        Assert.Equal(expected, SpeechService.CombineVolume(alias, master));
}
