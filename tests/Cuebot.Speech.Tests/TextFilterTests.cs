namespace Cuebot.Speech.Tests;

public class TextFilterTests
{
    [Fact]
    public void Literal_replacements_are_case_insensitive_and_applied_in_order()
    {
        WordReplacement[] replacements =
        [
            new() { Find = "LOL", Replace = "laughing out loud" },
            new() { Find = "loud", Replace = "quietly" },
            new() { Find = "", Replace = "ignored" },
        ];

        Assert.Equal("that was laughing out quietly", TextFilter.ApplyReplacements("that was lol", replacements));
    }

    [Fact]
    public void Regex_replacements_support_groups()
    {
        WordReplacement[] replacements = [new() { Find = @"(\d+)k", Replace = "$1 thousand", IsRegex = true }];

        Assert.Equal("5 thousand viewers", TextFilter.ApplyReplacements("5K viewers", replacements));
    }

    [Fact]
    public void Invalid_regex_is_skipped()
    {
        WordReplacement[] replacements =
        [
            new() { Find = "(unclosed", Replace = "x", IsRegex = true },
            new() { Find = "hi", Replace = "hello" },
        ];

        Assert.Equal("hello (unclosed", TextFilter.ApplyReplacements("hi (unclosed", replacements));
    }

    [Fact]
    public void Catastrophic_regex_times_out_and_is_skipped()
    {
        WordReplacement[] replacements = [new() { Find = "(a+)+$", Replace = "x", IsRegex = true }];
        var text = new string('a', 40) + "!";

        Assert.Equal(text, TextFilter.ApplyReplacements(text, replacements));
    }

    [Theory]
    [InlineData("check https://example.com/x?y=1 now", "check link now")]
    [InlineData("go to www.twitch.tv/someone", "go to link")]
    [InlineData("visit clips.twitch.tv or example.com/path", "visit link or link")]
    [InlineData("no links here, version 1.5", "no links here, version 1.5")]
    public void ReplaceLinks_replaces_urls_and_domains(string input, string expected) =>
        Assert.Equal(expected, TextFilter.ReplaceLinks(input));

    [Fact]
    public void CollapseWhitespace_joins_lines_and_trims() =>
        Assert.Equal("a b c", TextFilter.CollapseWhitespace("  a \n\t b   c  "));

    [Theory]
    [InlineData("hello world", 5, "hello")]
    [InlineData("hello world", 6, "hello")]
    [InlineData("hello", 0, "hello")]
    [InlineData("hello", -1, "hello")]
    [InlineData("hi", 10, "hi")]
    public void Truncate_limits_length(string input, int max, string expected) =>
        Assert.Equal(expected, TextFilter.Truncate(input, max));
}
