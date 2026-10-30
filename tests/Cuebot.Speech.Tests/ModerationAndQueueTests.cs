using Cuebot.Core.Events;
using Cuebot.Speech.Tests.Fakes;
using static Cuebot.Speech.Tests.Fakes.ChatEvents;

namespace Cuebot.Speech.Tests;

public class ModerationTests
{
    private static async Task<SpeechHarness> StartAsync()
    {
        var h = await SpeechHarness.StartAsync();
        h.Settings.SpeakChat.Enabled = true;
        h.Settings.SpeakChat.Template = "%message%";
        return h;
    }

    private static BotEvent Moderation(string type, params (string Key, object? Value)[] args) =>
        new(type, args.ToDictionary(a => a.Key, a => a.Value));

    [Theory]
    [InlineData("twitch.messageDeleted")]
    [InlineData("chat.messageDeleted")]
    public async Task Deleted_messages_are_dropped_from_the_queue(string type)
    {
        await using var h = await StartAsync();
        h.Service.Paused = true;
        await h.SayAsync(Message("Bob", "keep me", messageId: "m1"));
        await h.SayAsync(Message("Bob", "delete me", messageId: "m2"));

        h.Bus.Publish(Moderation(type, ("platform", "Twitch"), ("messageId", "m2")));
        h.Service.Paused = false;
        await h.DrainAsync();

        Assert.Equal(["keep me"], h.Spoken);
    }

    [Fact]
    public async Task Speak_next_steps_through_a_paused_queue_one_text_at_a_time()
    {
        await using var h = await StartAsync();
        Assert.False(h.Service.SpeakNext());   // nothing playing, nothing waiting
        h.Service.Paused = true;
        h.Player.Block = true;
        foreach (var text in new[] { "one", "two", "three" }) await h.SayAsync(Message("Bob", text));
        Assert.Equal(3, h.Service.QueueLength);

        Assert.True(h.Service.SpeakNext());
        var first = await h.Player.NextAsync();
        Assert.Equal(["one"], h.Spoken);
        await Task.Delay(100);
        Assert.Equal(0, h.Player.Pending);      // still paused: only the one text went ahead
        Assert.Equal(2, h.Service.QueueLength);

        Assert.True(h.Service.SpeakNext());     // stops "one" and lets "two" start
        await h.Player.NextAsync();
        Assert.True(h.Service.History.Single(e => e.Text == "one").Skipped);
        Assert.Equal(["one", "two"], h.Spoken);
        Assert.Equal(1, h.Service.QueueLength);
        first.Release();

        // Not paused: Speak next stops the one playing and the queue carries on by itself.
        h.Player.Block = false;
        h.Service.Paused = false;
        Assert.True(h.Service.SpeakNext());
        await h.DrainAsync();
        Assert.Equal(["one", "two", "three"], h.Spoken);
        Assert.True(h.Service.History.Single(e => e.Text == "two").Skipped);
        Assert.False(h.Service.SpeakNext());
    }

    [Fact]
    public async Task Deleting_the_playing_message_stops_it()
    {
        await using var h = await StartAsync();
        h.Player.Block = true;
        await h.SayAsync(Message("Bob", "playing", messageId: "p1"));
        await h.Player.NextAsync();

        h.Player.Block = false;
        h.Bus.Publish(Moderation("twitch.messageDeleted", ("targetMessageId", "p1")));
        await h.DrainAsync();

        Assert.Null(h.Service.CurrentText);
        Assert.True(h.Service.History.Single(e => e.Text == "playing").Skipped);
    }

    [Theory]
    [InlineData("twitch.userBanned")]
    [InlineData("chat.userBanned")]
    [InlineData("twitch.userTimedOut")]
    [InlineData("chat.userTimedOut")]
    public async Task Banned_or_timed_out_users_are_silenced(string type)
    {
        await using var h = await StartAsync();
        h.Player.Block = true;
        await h.SayAsync(Message("Troll", "first", userId: "t1"));
        await h.Player.NextAsync();
        await h.SayAsync(Message("Troll", "second", userId: "t1"));
        await h.SayAsync(Message("Nice", "hello"));

        h.Player.Block = false; // before the ban, so the next text does not block
        h.Bus.Publish(Moderation(type, ("platform", "Twitch"), ("userId", "t1"), ("user", "Troll")));
        await h.DrainAsync();

        Assert.Equal(["first", "hello"], h.Spoken); // first had started and was cut off
        Assert.True(h.Service.History.Single(e => e.Text == "first").Skipped);
    }

    [Fact]
    public async Task Users_are_matched_by_name_when_no_id_is_given()
    {
        await using var h = await StartAsync();
        h.Service.Paused = true;
        await h.SayAsync(Message("Troll", "x"));

        h.Bus.Publish(Moderation("twitch.userBanned", ("targetUserName", "troll")));

        Assert.Equal(0, h.Service.QueueLength);
    }

    [Fact]
    public async Task Other_platforms_are_not_affected()
    {
        await using var h = await StartAsync();
        h.Service.Paused = true;
        await h.SayAsync(Message("Same", "rumble text", platform: "Rumble", userId: "u1"));

        h.Bus.Publish(Moderation("chat.userBanned", ("platform", "Twitch"), ("userId", "u1")));

        Assert.Equal(1, h.Service.QueueLength);
    }

    [Fact]
    public async Task Moderation_handling_can_be_switched_off()
    {
        await using var h = await StartAsync();
        h.Settings.Moderation.SkipOnBan = false;
        h.Settings.Moderation.SkipOnTimeout = false;
        h.Settings.Moderation.SkipOnMessageDeleted = false;
        h.Service.Paused = true;
        await h.SayAsync(Message("Bob", "x", userId: "b", messageId: "m"));

        h.Bus.Publish(Moderation("twitch.userBanned", ("userId", "b")));
        h.Bus.Publish(Moderation("twitch.userTimedOut", ("userId", "b")));
        h.Bus.Publish(Moderation("twitch.messageDeleted", ("messageId", "m")));

        Assert.Equal(1, h.Service.QueueLength);
    }
}

public class QueueTests
{
    [Fact]
    public async Task Full_queue_drops_the_newest_by_default()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Settings.Queue.MaxLength = 2;
        h.Service.Paused = true;

        var a = h.Service.SpeakAsync("a", wait: true);
        var b = h.Service.SpeakAsync("b", wait: true);
        var c = h.Service.SpeakAsync("c", wait: true);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c);
        Assert.Equal(2, h.Service.QueueLength);
        h.Service.Paused = false;
        await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["a", "b"], h.Spoken);
    }

    [Fact]
    public async Task Drop_oldest_evicts_the_oldest_waiting_text()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Settings.Queue.MaxLength = 2;
        h.Settings.Queue.DropPolicy = QueueDropPolicy.DropOldest;
        h.Service.Paused = true;

        var a = h.Service.SpeakAsync("a", wait: true);
        _ = h.Service.SpeakAsync("b");
        _ = h.Service.SpeakAsync("c");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a);
        Assert.Equal(["b", "c"], h.Service.Queue.Select(q => q.Text));
    }

    [Fact]
    public async Task Higher_priority_texts_jump_ahead()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Service.Paused = true;

        _ = h.Service.SpeakAsync("low", new SpeakOptions { Priority = -1 });
        _ = h.Service.SpeakAsync("normal 1", new SpeakOptions());
        _ = h.Service.SpeakAsync("urgent", new SpeakOptions { Priority = 5 });
        _ = h.Service.SpeakAsync("normal 2", new SpeakOptions());

        Assert.Equal(["urgent", "normal 1", "normal 2", "low"], h.Service.Queue.Select(q => q.Text));
        h.Service.Paused = false;
        await h.DrainAsync();
        Assert.Equal(["urgent", "normal 1", "normal 2", "low"], h.Spoken);
    }

    [Fact]
    public void Queue_keeps_order_and_signals_per_item()
    {
        var queue = new SpeechQueue();
        SpeechRequest Request(string text, int priority = 0) =>
            new(text, null, null, CancellationToken.None, new SpeakOptions { Priority = priority });

        Assert.True(queue.TryEnqueue(Request("a"), 0, QueueDropPolicy.DropNewest, out _));
        Assert.True(queue.TryEnqueue(Request("b", 1), 0, QueueDropPolicy.DropNewest, out _));
        Assert.Equal(["b", "a"], queue.Snapshot().Select(r => r.Text));
        Assert.Equal(["b"], queue.RemoveAll(r => r.Text == "b").Select(r => r.Text));
        Assert.Equal("a", queue.TryTake()!.Text);
        Assert.Null(queue.TryTake());

        Assert.True(queue.TryEnqueue(Request("x", 2), 1, QueueDropPolicy.DropOldest, out _));
        Assert.False(queue.TryEnqueue(Request("lower", 1), 1, QueueDropPolicy.DropOldest, out var refused));
        Assert.Equal("lower", refused!.Text);
        queue.Complete();
        Assert.False(queue.TryEnqueue(Request("late"), 0, QueueDropPolicy.DropNewest, out var none));
        Assert.Null(none);
    }

    [Fact]
    public async Task Silent_speak_renders_a_file_with_duration_without_playing()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Engine.Samples = 8000; // half a second at 16 kHz
        h.Service.Paused = true; // silent speech does not wait for the queue

        var result = await h.Service.SpeakAsync("quiet", new SpeakOptions { Silent = true }).WaitAsync(TimeSpan.FromSeconds(5));

        try
        {
            Assert.Equal(TimeSpan.FromMilliseconds(500), result.Duration);
            Assert.False(result.Played);
            Assert.True(File.Exists(result.FilePath));
            Assert.EndsWith("-Main.wav", result.FilePath);
            Assert.Equal(0, h.Player.Pending);
            Assert.Equal(("quiet", "Main", "fake", "v1"), (result.Text, result.Alias, result.EngineId, result.VoiceId));
        }
        finally
        {
            File.Delete(result.FilePath!);
        }
    }

    [Fact]
    public async Task Save_folder_keeps_every_spoken_text_and_wait_returns_duration()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Engine.Samples = 1600;
        h.Settings.Queue.SaveFolder = Path.Combine(h.Folder, "saved");
        h.Settings.Aliases[0].Name = "Bad:Name?";

        var result = await h.Service.SpeakAsync("hello", new SpeakOptions(), wait: true);

        Assert.True(result.Played);
        Assert.Equal(TimeSpan.FromMilliseconds(100), result.Duration);
        var file = Assert.Single(Directory.GetFiles(h.Settings.Queue.SaveFolder));
        Assert.Equal(file, result.FilePath);
        Assert.EndsWith("-Bad_Name_.wav", file);
        Assert.Equal(Wav.FromPcm16(new byte[3200].Select((_, i) => i % 2 == 0 ? (byte)0xE8 : (byte)0x03).ToArray(), 16000, 1),
            await File.ReadAllBytesAsync(file));
    }

    [Theory]
    [InlineData("a/b\\c:d", "a_b_c_d")]
    [InlineData("  ..  ", "speech")]
    [InlineData("Ok name", "Ok name")]
    public void File_names_are_sanitized(string name, string expected) =>
        Assert.Equal(expected, SpeechService.SanitizeFileName(name));

    [Fact]
    public async Task Disabled_speech_ignores_new_texts()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Service.Enabled = false;

        var result = await h.Service.SpeakAsync("nope", new SpeakOptions(), wait: true);

        Assert.True(result.Skipped);
        Assert.Equal(0, h.Service.QueueLength);
        Assert.False(h.Settings.Enabled);
    }

    [Fact]
    public async Task Disabling_stops_and_clears_and_publishes_the_state()
    {
        await using var h = await SpeechHarness.StartAsync();
        var states = new List<BotEvent>();
        using var _ = h.Bus.Subscribe(e => { if (e.Type == SpeechEvents.StateChanged) lock (states) states.Add(e); });
        h.Player.Block = true;
        var playing = h.Service.SpeakAsync("playing", wait: true);
        await h.Player.NextAsync();
        var waiting = h.Service.SpeakAsync("waiting", wait: true);

        h.Service.Enabled = false;

        await playing.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        lock (states) Assert.Equal(false, Assert.Single(states).Args["enabled"]);
    }

    [Fact]
    public async Task Output_device_comes_from_the_alias_or_the_global_setting()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Settings.OutputDeviceId = "fake-1";
        h.Settings.Aliases.Add(new VoiceAlias { Name = "Headset", EngineId = "fake", VoiceId = "h", DeviceId = "fake-2", Volume = 40 });

        await h.Service.SpeakAsync("global", wait: true);
        var first = await h.Player.NextAsync();
        await h.Service.SpeakAsync("alias", "headset", wait: true);
        var second = await h.Player.NextAsync();

        Assert.Equal(new PlaybackOptions("fake-1", 100), first.Options);
        Assert.Equal(new PlaybackOptions("fake-2", 40), second.Options);
        Assert.Equal(2, h.Service.OutputDevices.Count);
    }

    [Fact]
    public async Task History_and_finished_events_report_duration()
    {
        await using var h = await SpeechHarness.StartAsync();
        h.Engine.Samples = 16000;
        var finished = new TaskCompletionSource<BotEvent>();
        using var _ = h.Bus.Subscribe(e => { if (e.Type == SpeechEvents.Finished) finished.TrySetResult(e); });

        await h.Service.SpeakAsync("one second", new SpeakOptions { UserName = "Bob", Source = "test" }, wait: true);

        var e = await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1000L, e.Args["duration"]);
        Assert.Equal(false, e.Args["skipped"]);
        Assert.Equal("Bob", e.Args["user"]);
        var entry = Assert.Single(h.Service.History);
        Assert.Equal(("one second", "Main", "Bob", "test"), (entry.Text, entry.Alias, entry.User, entry.Source));
    }

    [Theory]
    [InlineData(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }, 128)] // MPEG1 layer III 128 kbps
    [InlineData(new byte[] { 0xFF, 0xF3, 0x40, 0x00 }, 32)]  // MPEG2 layer III 32 kbps
    public void Mp3_duration_is_estimated_from_the_bitrate(byte[] header, int kbps)
    {
        var data = header.Concat(new byte[kbps * 125 - header.Length]).ToArray(); // one second of data

        Assert.Equal(1.0, AudioDuration.Estimate(new SynthesizedAudio(data, AudioFormat.Mp3)).TotalSeconds, 3);
        Assert.Equal(TimeSpan.Zero, AudioDuration.Estimate(new SynthesizedAudio([1, 2, 3], AudioFormat.Ogg)));
        Assert.Equal(TimeSpan.Zero, AudioDuration.Estimate(new SynthesizedAudio([1, 2, 3], AudioFormat.Wav)));
    }
}
