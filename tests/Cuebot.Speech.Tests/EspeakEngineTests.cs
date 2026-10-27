namespace Cuebot.Speech.Tests;

public class EspeakEngineTests
{
    private const string VoicesOutput = """
        Pty Language       Age/Gender VoiceName          File                 Other Languages
         5  af              --/M      Afrikaans          gmw/af
         5  en-gb           --/M      English_(Great_Britain) gmw/en               (en 2)
         2  en-us           --/M      English_(America)  gmw/en-US            (en 3)
         5  en-us           --/F      Duplicate          gmw/en-US-f

        """;

    [Fact]
    public void Parses_voice_table_and_skips_header_and_duplicates()
    {
        var voices = EspeakVoiceParser.Parse(VoicesOutput.Replace("\n", "\r\n"));

        Assert.Equal(
        [
            new VoiceInfo("af", "Afrikaans", "af"),
            new VoiceInfo("en-gb", "English (Great Britain)", "en-gb"),
            new VoiceInfo("en-us", "English (America)", "en-us"),
        ], voices);
    }

    [Fact]
    public void Parses_empty_output() => Assert.Empty(EspeakVoiceParser.Parse(""));

    [Theory]
    [InlineData(0, 175)]
    [InlineData(-10, 80)]
    [InlineData(10, 450)]
    [InlineData(-5, 128)]
    [InlineData(4, 285)]
    [InlineData(99, 450)]
    public void Maps_rate_to_words_per_minute(int rate, int expected) =>
        Assert.Equal(expected, EspeakEngine.RateToWordsPerMinute(rate));

    [Fact]
    public async Task Is_unavailable_when_not_installed()
    {
        var engine = new EspeakEngine(() => null);

        Assert.False(engine.IsAvailable);
        Assert.NotNull(engine.UnavailableReason);
        await Assert.ThrowsAsync<InvalidOperationException>(() => engine.GetVoicesAsync(CancellationToken.None));
    }

    [Fact]
    public void Arguments_carry_voice_rate_and_pitch()
    {
        var arguments = EspeakEngine.BuildArguments(new SynthesisRequest("text", "en-gb") { Rate = 10, Pitch = -4 });

        Assert.Equal(["-v", "en-gb", "-b", "1", "-s", "450", "-p", "30", "--stdout", "--stdin"], arguments);
        Assert.Equal(99, EspeakEngine.PitchToEspeak(20));
    }

    [Fact]
    public void Windows_pitch_uses_escaped_ssml() =>
        Assert.Equal("<speak version=\"1.0\" xmlns=\"http://www.w3.org/2001/10/synthesis\" xml:lang=\"en-GB\">"
                     + "<prosody pitch=\"+15%\">a &amp; b</prosody></speak>",
            WindowsSpeechEngine.BuildSsml("a & b", 3, "en-GB"));

    [Fact]
    public void Chat_context_reads_neutral_and_optional_twitch_args()
    {
        var chat = ChatContext.FromEvent(new Cuebot.Core.Events.BotEvent("chat.message", new Dictionary<string, object?>
        {
            ["platform"] = "Twitch",
            ["user"] = "Alice",
            ["userId"] = "1",
            ["userName"] = "alice",
            ["message"] = "hi",
            ["messageId"] = "m",
            ["isModerator"] = "true",
            ["isSubscriber"] = true,
            ["emotes"] = "25:0-1",
            ["emoteNames"] = "Kappa, PogChamp",
            ["msgType"] = "highlighted-message",
            ["isSharedChat"] = true,
        }))!;

        Assert.Equal(("Twitch", "Alice", "1", "alice", "hi", "m"), (chat.Platform, chat.User, chat.UserId, chat.UserName, chat.Message, chat.MessageId));
        Assert.True(chat.IsModerator && chat.IsSubscriber && chat.IsHighlighted && chat.IsSharedChat);
        Assert.False(chat.IsVip || chat.IsBroadcaster);
        Assert.Equal(["Kappa", "PogChamp"], chat.EmoteNames);
        Assert.Equal("25:0-1", chat.EmoteRanges);
        Assert.Null(ChatContext.FromEvent(new Cuebot.Core.Events.BotEvent("chat.message")));
        Assert.Equal("rumble", ChatContext.FromEvent(new Cuebot.Core.Events.BotEvent("rumble.chatMessage",
            new Dictionary<string, object?> { ["user"] = "x", ["message"] = "y" }))!.Platform);
    }
}
