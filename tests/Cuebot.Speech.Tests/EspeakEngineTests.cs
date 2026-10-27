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
}
