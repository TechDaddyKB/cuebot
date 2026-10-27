using Cuebot.Core.Chat.Emotes;

namespace Cuebot.Tests.ChatExtras;

public class EmoteParserTests
{
    [Fact]
    public void Twitch_ranges_map_code_points_to_utf16_indices()
    {
        var text = "Kappa hi Kappa";
        var matches = EmoteParser.ParseTwitchRanges(text, "25:0-4,9-13");

        Assert.Equal([(0, 5), (9, 5)], matches.Select(m => (m.Start, m.Length)));
        Assert.All(matches, m => Assert.Equal("Kappa", m.Code));
        Assert.Equal("https://static-cdn.jtvnw.net/emoticons/v2/25/default/dark/1.0", matches[0].ImageUrl);
        Assert.Equal("Twitch", matches[0].Provider);
    }

    [Fact]
    public void Emoji_before_an_emote_count_as_one_code_point()
    {
        // "😀" is a surrogate pair (2 UTF-16 chars) but one code point; Twitch reports the emote at 2-6.
        var text = "😀 Kappa 👍🏽 LUL";
        Assert.Equal(2, "😀".Length);
        // 👍🏽 is two code points (thumbs up + skin tone), each a surrogate pair.
        var matches = EmoteParser.ParseTwitchRanges(text, "25:2-6/425618:11-13");

        Assert.Equal(2, matches.Count);
        Assert.Equal("Kappa", text.Substring(matches[0].Start, matches[0].Length));
        Assert.Equal(3, matches[0].Start);
        Assert.Equal("LUL", text.Substring(matches[1].Start, matches[1].Length));
        Assert.Equal("425618", matches[1].Emote.Id);
    }

    [Fact]
    public void Bad_or_out_of_range_positions_are_ignored()
    {
        Assert.Empty(EmoteParser.ParseTwitchRanges("hi", "25:0-9"));
        Assert.Empty(EmoteParser.ParseTwitchRanges("hi", "garbage"));
        Assert.Empty(EmoteParser.ParseTwitchRanges("hi", "25:1-0"));
        Assert.Empty(EmoteParser.ParseTwitchRanges("hi", ""));
        Assert.Single(EmoteParser.ParseTwitchRanges("Kappa", "25:0-4,0-4"));   // duplicates collapse
    }

    [Fact]
    public void Kick_tokens_become_emotes_named_by_their_code()
    {
        var text = "gg [emote:37226:KEKW] nice [emote:1:x]";
        var matches = EmoteParser.ParseKickTokens(text);

        Assert.Equal(2, matches.Count);
        Assert.Equal("KEKW", matches[0].Code);
        Assert.Equal("[emote:37226:KEKW]", matches[0].Text);
        Assert.Equal(3, matches[0].Start);
        Assert.Equal("https://files.kick.com/emotes/37226/fullsize", matches[0].ImageUrl);
    }

    [Fact]
    public void Third_party_words_match_exactly_and_skip_native_ranges()
    {
        var emotes = new Dictionary<string, Emote>(StringComparer.Ordinal)
        {
            ["KEKW"] = new("KEKW", "1", "7TV", "u1"),
            ["Kappa"] = new("Kappa", "2", "BTTV", "u2"),
        };
        var text = "KEKW kekw KEKWW Kappa 😀KEKW KEKW";
        var native = EmoteParser.ParseTwitchRanges(text, "25:16-20");   // the Twitch "Kappa"

        var words = EmoteParser.FindWords(text, emotes, native);

        Assert.Equal([0, text.LastIndexOf("KEKW", StringComparison.Ordinal)], words.Select(w => w.Start));
        Assert.All(words, w => Assert.Equal("7TV", w.Provider));
    }

    [Fact]
    public void Split_returns_text_and_emote_pieces_in_order()
    {
        var emotes = new Dictionary<string, Emote> { ["A"] = new("A", "1", "FFZ", "u") };
        var text = "x A y A";
        var parts = EmoteParser.Split(text, EmoteParser.FindWords(text, emotes));

        Assert.Equal(["x ", "A", " y ", "A"], parts.Select(p => p.Text));
        Assert.Equal([false, true, false, true], parts.Select(p => p.Emote is not null));
    }

    [Fact]
    public void Structured_platform_emotes_are_checked_against_the_text()
    {
        var text = "hi :yt-wave: there";
        var list = new[]
        {
            new PlatformEmote(3, 9, ":yt-wave:", "w", "https://yt/wave.png"),
            new PlatformEmote(50, 3, "bad", "b", "https://yt/bad.png"),
        };

        var matches = EmoteParser.FromPlatformList("YouTube", text, list);

        Assert.Single(matches);
        Assert.Equal(":yt-wave:", matches[0].Text);
        Assert.Equal("YouTube", matches[0].Provider);
    }
}
