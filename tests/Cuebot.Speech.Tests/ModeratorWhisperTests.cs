using Cuebot.Core.Events;
using Cuebot.Speech.Tests.Fakes;

namespace Cuebot.Speech.Tests;

/// <summary>Speaker.bot's "Allow Moderator Whispers": moderators whisper !tts commands to the broadcaster's Twitch account.</summary>
public class ModeratorWhisperTests
{
    /// <summary>A broadcaster whisper as the Twitch module publishes it (EventSub user.whisper.message): no badges.</summary>
    private static BotEvent Whisper(string user, string text) => new("twitch.whisper", new Dictionary<string, object?>
    {
        ["user"] = user,
        ["userName"] = user.ToLowerInvariant(),
        ["userId"] = user.ToLowerInvariant() + "-id",
        ["message"] = text,
        ["rawInput"] = text,
        ["messageId"] = Guid.NewGuid().ToString("N"),
        ["messageKind"] = "Whisper",
        ["toUser"] = "Streamer",
    });

    private static async Task<SpeechHarness> StartAsync()
    {
        var h = await SpeechHarness.StartAsync();
        h.Settings.SpeakChat.Enabled = true;
        h.Settings.SpeakChat.Template = "%user%: %message%";
        h.Settings.Commands.AllowModeratorWhispers = true;
        h.Roles.Moderators.Add("moddy-id");
        return h;
    }

    private static Task Handle(SpeechHarness h, BotEvent whisper) => h.Service.HandleWhisperAsync(ChatContext.FromEvent(whisper)! with { Platform = "Twitch" });

    [Fact]
    public async Task A_moderator_whisper_runs_moderator_commands_without_chat_replies()
    {
        await using var h = await StartAsync();

        await Handle(h, Whisper("Moddy", "!tts pause"));

        Assert.True(h.Service.Paused);
        Assert.Equal(["moddy-id"], h.Roles.Lookups);

        await Handle(h, Whisper("Moddy", "!tts status"));
        Assert.Empty(h.Chat.Replies);   // whispered commands get no chat feedback, as in Speaker.bot
    }

    [Fact]
    public async Task A_moderator_whisper_can_use_speaking_commands()
    {
        await using var h = await StartAsync();

        await Handle(h, Whisper("Moddy", "!say hello from a whisper"));
        await h.DrainAsync();

        Assert.Equal(["Moddy: hello from a whisper"], h.Spoken);
    }

    [Fact]
    public async Task Whispers_from_viewers_and_plain_whispers_are_ignored()
    {
        await using var h = await StartAsync();

        await Handle(h, Whisper("Alice", "!tts pause"));
        await Handle(h, Whisper("Moddy", "just saying hi"));
        await h.DrainAsync();

        Assert.False(h.Service.Paused);
        Assert.Empty(h.Spoken);
        Assert.Empty(h.Chat.Replies);   // no "not allowed" reply to a whisper
        Assert.Equal(["alice-id"], h.Roles.Lookups);   // plain whispers never ask Twitch
    }

    [Fact]
    public async Task Whispers_arrive_from_the_bus_only_when_the_option_is_on()
    {
        await using var h = await StartAsync();
        h.Settings.Commands.AllowModeratorWhispers = false;

        h.Bus.Publish(Whisper("Moddy", "!tts pause"));
        await Task.Delay(100);
        Assert.False(h.Service.Paused);
        Assert.Empty(h.Roles.Lookups);

        h.Settings.Commands.AllowModeratorWhispers = true;
        h.Bus.Publish(Whisper("Moddy", "!tts pause"));
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!h.Service.Paused)
        {
            Assert.True(DateTime.UtcNow < deadline, "the whispered command did not run");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task The_option_is_off_by_default()
    {
        Assert.False(new TtsCommandSettings().AllowModeratorWhispers);
        await using var h = await SpeechHarness.StartAsync();
        Assert.False(h.Settings.Commands.AllowModeratorWhispers);
    }
}
