using Cuebot.Core.Commands;

namespace Cuebot.Tests.Commands;

public class CommandMatcherTests
{
    private static CommandMatch? Basic(string message, string command, CommandLocation location, bool caseSensitive = false) =>
        CommandMatcher.Match(message, command, CommandMode.Basic, location, caseSensitive);

    [Theory]
    [InlineData("!so bob", true, "bob")]
    [InlineData("  !so   bob  marley ", true, "bob  marley")]
    [InlineData("!so", true, "")]
    [InlineData("!sob", false, null)]
    [InlineData("hey !so bob", false, null)]
    [InlineData("!SO bob", true, "bob")]
    public void Start_location_requires_the_command_as_first_word(string message, bool matches, string? input)
    {
        var match = Basic(message, "!so", CommandLocation.Start);
        Assert.Equal(matches, match is not null);
        if (match is not null) Assert.Equal(input, match.Input);
    }

    [Fact]
    public void Start_location_splits_words()
    {
        var match = Basic("!roll 2 d20", "!roll", CommandLocation.Start)!;
        Assert.Equal(["2", "d20"], match.Words);
    }

    [Theory]
    [InlineData("!lurk", true)]
    [InlineData("  !lurk ", true)]
    [InlineData("!lurk now", false)]
    [InlineData("!lurking", false)]
    public void Exact_location_needs_the_whole_message(string message, bool matches) =>
        Assert.Equal(matches, Basic(message, "!lurk", CommandLocation.Exact) is not null);

    [Theory]
    [InlineData("that was lol", true, "")]
    [InlineData("lol that was funny", true, "that was funny")]
    [InlineData("so funny lol!", true, "!")]
    [InlineData("lollipop", false, null)]
    [InlineData("trolololo", false, null)]
    [InlineData("haha, lol, yes", true, ", yes")]
    public void Anywhere_location_matches_whole_words(string message, bool matches, string? input)
    {
        var match = Basic(message, "lol", CommandLocation.Anywhere);
        Assert.Equal(matches, match is not null);
        if (match is not null) Assert.Equal(input, match.Input);
    }

    [Fact]
    public void Anywhere_location_with_a_prefix_character()
    {
        Assert.NotNull(Basic("please !hug bob", "!hug", CommandLocation.Anywhere));
        Assert.NotNull(Basic("please !hug, bob", "!hug", CommandLocation.Anywhere));
        Assert.Null(Basic("please!hug bob", "!hug", CommandLocation.Anywhere));
        Assert.Equal("bob", Basic("please !hug bob", "!hug", CommandLocation.Anywhere)!.Input);
    }

    [Fact]
    public void Anywhere_finds_a_later_valid_occurrence()
    {
        var match = Basic("lollipop then lol ok", "lol", CommandLocation.Anywhere);
        Assert.Equal("ok", match!.Input);
    }

    [Fact]
    public void Case_sensitivity_is_respected()
    {
        Assert.Null(Basic("!Hello", "!hello", CommandLocation.Start, caseSensitive: true));
        Assert.NotNull(Basic("!hello", "!hello", CommandLocation.Start, caseSensitive: true));
        Assert.NotNull(Basic("!Hello", "!hello", CommandLocation.Start));
    }

    [Fact]
    public void Blank_command_never_matches()
    {
        Assert.Null(Basic("anything", "  ", CommandLocation.Anywhere));
        Assert.Null(CommandMatcher.Match("anything", "", CommandMode.Regex, CommandLocation.Anywhere, false));
    }

    [Fact]
    public void MatchAny_returns_the_first_matching_alias()
    {
        var match = CommandMatcher.MatchAny("!hi there", ["!hello", "!hi", "!h"], CommandMode.Basic, CommandLocation.Start, false);
        Assert.Equal("!hi", match!.Command);
        Assert.Null(CommandMatcher.MatchAny("!yo", ["!hello", "!hi"], CommandMode.Basic, CommandLocation.Start, false));
    }

    [Fact]
    public void Regex_exposes_numbered_and_named_groups_and_the_rest_as_input()
    {
        var match = CommandMatcher.Match("!give 50 to (?<x>)", @"!give (\d+) to", CommandMode.Regex, CommandLocation.Start, false);
        Assert.NotNull(match);
        Assert.Equal("50", match.Groups["1"]);
        Assert.Equal("!give 50 to", match.Matched);
        Assert.Equal("(?<x>)", match.Input);

        var named = CommandMatcher.Match("roll 3d6 please", @"(?<count>\d+)d(?<sides>\d+)", CommandMode.Regex, CommandLocation.Anywhere, false)!;
        Assert.Equal("3", named.Groups["count"]);
        Assert.Equal("6", named.Groups["sides"]);
        Assert.Equal("please", named.Input);
    }

    [Theory]
    [InlineData(CommandLocation.Start, "hi there", true)]
    [InlineData(CommandLocation.Start, "oh hi there", false)]
    [InlineData(CommandLocation.Exact, "hi", true)]
    [InlineData(CommandLocation.Exact, "hi there", false)]
    [InlineData(CommandLocation.Anywhere, "oh hi there", true)]
    public void Regex_location_anchors_the_pattern(CommandLocation location, string message, bool matches) =>
        Assert.Equal(matches, CommandMatcher.Match(message, "h(i|ey)", CommandMode.Regex, location, false) is not null);

    [Fact]
    public void Regex_alternation_is_anchored_as_a_whole()
    {
        // ^(?:a|b) rather than ^a|b
        Assert.Null(CommandMatcher.Match("x b", "a|b", CommandMode.Regex, CommandLocation.Start, false));
    }

    [Fact]
    public void Regex_case_sensitivity_and_invalid_patterns()
    {
        Assert.Null(CommandMatcher.Match("HELLO", "hello", CommandMode.Regex, CommandLocation.Exact, caseSensitive: true));
        Assert.NotNull(CommandMatcher.Match("HELLO", "hello", CommandMode.Regex, CommandLocation.Exact, caseSensitive: false));
        Assert.Null(CommandMatcher.Match("abc", "(unclosed", CommandMode.Regex, CommandLocation.Anywhere, false));
        Assert.NotNull(CommandMatcher.ValidateRegex("(unclosed"));
        Assert.Null(CommandMatcher.ValidateRegex(@"^!\w+$"));
    }
}

public class CooldownTrackerTests
{
    [Fact]
    public void Global_and_user_cooldowns_report_remaining_time()
    {
        var clock = new TestClock();
        var tracker = new CooldownTracker(clock);
        var g = TimeSpan.FromSeconds(10);
        var u = TimeSpan.FromSeconds(30);

        Assert.True(tracker.TryStart("cmd", "alice", g, u).Allowed);
        var blocked = tracker.TryStart("cmd", "bob", g, u);
        Assert.Equal(CooldownKind.Global, blocked.Kind);
        Assert.Equal(TimeSpan.FromSeconds(10), blocked.Remaining);

        clock.Advance(11);
        Assert.True(tracker.TryStart("cmd", "bob", g, u).Allowed);
        clock.Advance(11);
        var user = tracker.TryStart("cmd", "alice", g, u);
        Assert.False(user.Allowed);
        Assert.Equal(CooldownKind.User, user.Kind);
        Assert.Equal(TimeSpan.FromSeconds(8), user.Remaining);
        Assert.Equal(TimeSpan.FromSeconds(8), tracker.UserRemaining("cmd", "alice"));

        Assert.True(tracker.TryStart("other", "alice", g, u).Allowed); // keys are independent
        tracker.Reset("cmd");
        Assert.True(tracker.TryStart("cmd", "alice", g, u).Allowed);
    }

    [Fact]
    public void Zero_cooldowns_never_block()
    {
        var tracker = new CooldownTracker(new TestClock());
        for (var i = 0; i < 5; i++) Assert.True(tracker.TryStart("cmd", "a", TimeSpan.Zero, TimeSpan.Zero).Allowed);
        Assert.Equal(TimeSpan.Zero, tracker.GlobalRemaining("cmd"));
    }
}
