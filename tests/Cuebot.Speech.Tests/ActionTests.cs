using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Scripting;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Speech.Tests;

public class ActionTests
{
    [Theory]
    [InlineData("Pause", false, true)]
    [InlineData("pause", true, true)]
    [InlineData("Resume", true, false)]
    [InlineData("Toggle", true, false)]
    [InlineData("Toggle", false, true)]
    [InlineData("bogus", true, true)]
    public void Pause_modes(string mode, bool paused, bool expected) =>
        Assert.Equal(expected, PauseSpeechSubAction.ApplyMode(mode, paused));

    [Theory]
    [InlineData("Main", "", true)]
    [InlineData("Main", "main", true)]
    [InlineData("Main", "Other", false)]
    [InlineData(null, "Main", false)]
    public void Triggers_filter_by_alias(string? spoken, string filter, bool expected) =>
        Assert.Equal(expected, SpeechTriggers.MatchesAlias(spoken, filter));

    [Fact]
    public async Task AddSpeech_registers_everything()
    {
        var root = Path.Combine(Path.GetTempPath(), "cuebot-speech-tests", Guid.NewGuid().ToString("N"));
        var services = new ServiceCollection().AddCuebotCore(new AppPaths(root)).AddSpeech();
        await using (var provider = services.BuildServiceProvider())
        {
            var registry = provider.GetRequiredService<Registry>();
            var speech = provider.GetRequiredService<SpeechService>();

            Assert.Equal(["windows", "espeak", "elevenlabs"], speech.Engines.Select(e => e.Id));
            foreach (var id in new[] { "speech.speak", "speech.skip", "speech.clearQueue", "speech.pause", "audio.playSound" })
                Assert.NotNull(registry.GetSubAction(id));
            Assert.NotNull(registry.GetTrigger("speech.started"));
            Assert.NotNull(registry.GetTrigger("speech.finished"));
            Assert.Contains(provider.GetRequiredService<ScriptApi>().Functions, f => f.Name == "speech.speak");
            Assert.NotNull(provider.GetRequiredService<AudioService>());
        }
        try { Directory.Delete(root, recursive: true); }
        catch (IOException) { }
    }

    [Fact]
    public async Task AudioService_rejects_non_wav_files()
    {
        var file = Path.Combine(Path.GetTempPath(), $"cuebot-{Guid.NewGuid():N}.mp3");
        await File.WriteAllBytesAsync(file, [1, 2, 3]);
        try
        {
            var audio = new AudioService(new Fakes.FakeAudioPlayer());
            await Assert.ThrowsAsync<NotSupportedException>(() => audio.PlayFileAsync(file, 100, CancellationToken.None));
            await Assert.ThrowsAsync<FileNotFoundException>(() => audio.PlayFileAsync(file + ".wav", 100, CancellationToken.None));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task AudioService_plays_wav_at_volume()
    {
        var file = Path.Combine(Path.GetTempPath(), $"cuebot-{Guid.NewGuid():N}.wav");
        await File.WriteAllBytesAsync(file, Wav.FromPcm16([0x10, 0x27], 8000, 1)); // 10000
        try
        {
            var player = new Fakes.FakeAudioPlayer();
            await new AudioService(player).PlayFileAsync(file, 10, CancellationToken.None);

            var played = await player.NextAsync();
            Assert.Equal(1000, BitConverter.ToInt16(played.Wav, 44));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
