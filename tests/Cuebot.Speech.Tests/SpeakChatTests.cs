using Cuebot.Core.Events;
using Cuebot.Speech.Tests.Fakes;

namespace Cuebot.Speech.Tests;

public class SpeakChatTests
{
    private static BotEvent Chat(string user, string message, string? userName = null) =>
        new("chat.message", new Dictionary<string, object?>
        {
            ["platform"] = "Twitch",
            ["user"] = user,
            ["userName"] = userName ?? user.ToLowerInvariant(),
            ["message"] = message,
            ["isModerator"] = false,
        });

    private static async Task<SpeechHarness> StartWithChatAsync(Action<SpeakChatSettings>? configure = null)
    {
        var h = await SpeechHarness.StartAsync();
        h.Settings.SpeakChat.Enabled = true;
        h.Settings.SpeakChat.Alias = "Main";
        configure?.Invoke(h.Settings.SpeakChat);
        return h;
    }

    [Fact]
    public async Task Reads_chat_messages_with_the_template()
    {
        await using var h = await StartWithChatAsync();

        h.Bus.Publish(Chat("Bob", "hello there"));

        var call = await h.Engine.NextAsync();
        Assert.Equal(new SynthesisCall("Bob says hello there", "v1", 0), call);
    }

    [Fact]
    public async Task Skips_ignored_users_and_commands()
    {
        await using var h = await StartWithChatAsync(chat => chat.IgnoredUsers.Add("NIGHTBOT"));

        h.Bus.Publish(Chat("Nightbot", "I am a bot"));
        h.Bus.Publish(Chat("Some_Display", "by login", userName: "nightbot"));
        h.Bus.Publish(Chat("Alice", "  !command"));
        h.Bus.Publish(Chat("Alice", "real message"));

        Assert.Equal("Alice says real message", (await h.Engine.NextAsync()).Text);
        Assert.Single(h.Engine.History);
    }

    [Fact]
    public async Task Does_nothing_when_disabled()
    {
        await using var h = await StartWithChatAsync(chat => chat.Enabled = false);

        h.Bus.Publish(Chat("Bob", "hello"));
        await h.Service.SpeakAsync("marker", wait: true);

        Assert.Equal(["marker"], h.Engine.History.Select(c => c.Text));
    }

    [Fact]
    public void Utterance_replaces_links_and_truncates_the_message_only()
    {
        var settings = new SpeakChatSettings { Template = "%user%: %message%", MaxLength = 10 };

        var text = ChatSpeech.BuildUtterance(Chat("Bob", "see https://x.io/abc   please read"), settings);

        Assert.Equal("Bob: see link p", text);
    }

    [Fact]
    public void Commands_are_read_when_not_ignored()
    {
        var settings = new SpeakChatSettings { IgnoreCommands = false, SkipLinks = false, Template = "%message%" };

        Assert.Equal("!so example.com", ChatSpeech.BuildUtterance(Chat("Bob", "!so example.com"), settings));
    }

    [Fact]
    public void Empty_messages_are_skipped() =>
        Assert.Null(ChatSpeech.BuildUtterance(Chat("Bob", "   "), new SpeakChatSettings()));
}
