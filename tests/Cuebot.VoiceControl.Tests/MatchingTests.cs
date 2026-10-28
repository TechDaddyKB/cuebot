using Cuebot.VoiceControl.Engines;
using Cuebot.VoiceControl.Matching;

namespace Cuebot.VoiceControl.Tests;

public sealed class PhraseTextTests
{
    [Theory]
    [InlineData("Start the Countdown!", "start the countdown")]
    [InlineData("  switch   to\tcamera, two. ", "switch to camera 2")]
    [InlineData("Café — déjà vu", "cafe deja vu")]
    [InlineData("Don't stop", "dont stop")]
    [InlineData("count-down", "count down")]
    [InlineData("...", "")]
    [InlineData(null, "")]
    public void Normalizes_case_punctuation_accents_and_small_numbers(string? text, string expected) =>
        Assert.Equal(expected, PhraseText.Normalize(text));

    [Fact]
    public void Tokens_keep_their_position_in_the_original_text()
    {
        var tokens = PhraseText.Tokenize("Hey, Cuebot!");
        Assert.Equal(["hey", "cuebot"], tokens.Select(t => t.Normalized));
        Assert.Equal("Cuebot!", "Hey, Cuebot!"[tokens[1].Start..tokens[1].End]);
    }
}

public sealed class PhraseMatcherTests
{
    [Theory]
    [InlineData("start the countdown", "Start the countdown.", true)]
    [InlineData("start the countdown", "please start the countdown", false)]
    [InlineData("start the countdown", "Start the count down.", true)]
    [InlineData("scene two", "Scene 2", true)]
    [InlineData("lights on", "lights off", false)]
    public void Exact_needs_the_whole_utterance(string phrase, string spoken, bool matches) =>
        Assert.Equal(matches, PhraseMatcher.Match(phrase, CommandLocation.Exact, spoken, 0.2) is not null);

    [Fact]
    public void Start_matches_the_beginning_and_returns_the_rest_as_input()
    {
        var match = PhraseMatcher.Match("say", CommandLocation.Start, "Say, hello chat and welcome!", 0.2);
        Assert.NotNull(match);
        Assert.Equal("hello chat and welcome!", match.Remainder);
        Assert.Equal("Say,", match.Matched);
        Assert.Null(PhraseMatcher.Match("say", CommandLocation.Start, "I say hello", 0.2));
    }

    [Fact]
    public void Anywhere_finds_the_phrase_in_the_middle()
    {
        var match = PhraseMatcher.Match("clip that", CommandLocation.Anywhere, "Oh wow, clip that, that was great", 0.2);
        Assert.NotNull(match);
        Assert.Equal(1, match.Similarity);
        Assert.Equal("that was great", match.Remainder);
        Assert.Null(PhraseMatcher.Match("clip that", CommandLocation.Anywhere, "nothing to see here", 0.2));
    }

    [Fact]
    public void Fuzzy_matches_tolerate_small_transcription_differences_only_within_the_tolerance()
    {
        var fuzzy = PhraseMatcher.Match("toggle the webcam", CommandLocation.Exact, "Toggle the web cams", 0.2);
        Assert.NotNull(fuzzy);
        Assert.InRange(fuzzy.Similarity, 0.8, 0.99);
        Assert.Null(PhraseMatcher.Match("toggle the webcam", CommandLocation.Exact, "Toggle the web cams", 0));
        Assert.NotNull(PhraseMatcher.Match("toggle the webcam", CommandLocation.Exact, "toggle the webcam", 0));
        Assert.Null(PhraseMatcher.Match("go", CommandLocation.Exact, "no", 0.5 - 0.01));
    }

    [Fact]
    public void Levenshtein_distance_is_correct() => Assert.Equal(3, PhraseMatcher.Levenshtein("kitten", "sitting"));

    [Fact]
    public void Empty_phrases_or_speech_never_match()
    {
        Assert.Null(PhraseMatcher.Match("", CommandLocation.Anywhere, "anything", 0.2));
        Assert.Null(PhraseMatcher.Match("hello", CommandLocation.Anywhere, " ... ", 0.2));
    }
}

public sealed class CommandEvaluatorTests
{
    private static VoiceCommand Cmd(string phrase, CommandLocation location = CommandLocation.Exact, bool stopAfter = false,
        int? threshold = null, bool enabled = true) => new()
    {
        Name = phrase, Phrase = phrase, Location = location, StopAfter = stopAfter, Enabled = enabled,
        OverrideGlobal = threshold is not null, ConfidenceThreshold = threshold ?? 0,
    };

    private static RecognitionResult Heard(string text, double confidence = 0.9, bool isFinal = true) => new(1, text, confidence, isFinal);

    [Fact]
    public void Every_matching_enabled_command_fires_in_order()
    {
        var hits = CommandEvaluator.Evaluate(Heard("clip that and mark it"),
            [Cmd("clip that", CommandLocation.Anywhere), Cmd("mark it", CommandLocation.Anywhere), Cmd("other"), Cmd("clip", CommandLocation.Start, enabled: false)],
            50, 20);
        Assert.Equal(["clip that", "mark it"], hits.Select(h => h.Command.Phrase));
    }

    [Fact]
    public void Thresholds_use_the_global_value_unless_overridden()
    {
        Assert.Empty(CommandEvaluator.Evaluate(Heard("lights", 0.4), [Cmd("lights")], 50, 20));
        Assert.Single(CommandEvaluator.Evaluate(Heard("lights", 0.4), [Cmd("lights", threshold: 30)], 50, 20));
        Assert.Empty(CommandEvaluator.Evaluate(Heard("lights", 0.9), [Cmd("lights", threshold: 95)], 50, 20));
        // Fuzzy matches lower the effective confidence: 0.6 × ~0.86 is below 55.
        Assert.Empty(CommandEvaluator.Evaluate(Heard("web cams", 0.6), [Cmd("webcam")], 55, 30));
    }

    [Fact]
    public void Stop_after_ends_the_check_and_interim_results_only_fire_stop_after_commands()
    {
        VoiceCommand[] commands = [Cmd("first", CommandLocation.Anywhere, stopAfter: true), Cmd("second", CommandLocation.Anywhere)];
        Assert.Equal(["first"], CommandEvaluator.Evaluate(Heard("first then second"), commands, 0, 20).Select(h => h.Command.Phrase));

        Assert.Equal(["first"], CommandEvaluator.Evaluate(Heard("first and", isFinal: false), commands, 0, 20).Select(h => h.Command.Phrase));
        Assert.Empty(CommandEvaluator.Evaluate(Heard("second", isFinal: false), commands, 0, 20));
        Assert.Empty(CommandEvaluator.Evaluate(Heard("now", isFinal: false), [Cmd("now", stopAfter: true)], 0, 20));
    }

    [Fact]
    public void Commands_fired_early_do_not_fire_again()
    {
        var first = Cmd("first", CommandLocation.Anywhere, stopAfter: true);
        var other = Cmd("other", CommandLocation.Anywhere);
        Assert.Empty(CommandEvaluator.Evaluate(Heard("first other"), [first, other], 0, 20, [first.Id]));
        var plain = Cmd("plain", CommandLocation.Anywhere);
        Assert.Equal(["other"], CommandEvaluator.Evaluate(Heard("plain other"), [plain, other], 0, 20, [plain.Id]).Select(h => h.Command.Phrase));
    }

    [Fact]
    public void Interim_results_are_only_needed_for_early_firing_commands()
    {
        Assert.False(CommandEvaluator.NeedsInterimResults([Cmd("a"), Cmd("b", CommandLocation.Start)]));
        Assert.False(CommandEvaluator.NeedsInterimResults([Cmd("a", stopAfter: true)]));
        Assert.True(CommandEvaluator.NeedsInterimResults([Cmd("b", CommandLocation.Start, stopAfter: true)]));
    }

    [Fact]
    public void Command_arguments_follow_streamer_bot_names()
    {
        var command = Cmd("say", CommandLocation.Start);
        var result = new RecognitionResult(3, "Say hello world", 0.873, true)
        {
            Alternates = [new("Say hello world", 0.873), new("Stay hello world", 0.41)],
        };
        var hit = Assert.Single(CommandEvaluator.Evaluate(result, [command], 50, 20));
        var args = VoiceControlArgs.ForCommand(result, hit, "whisper");
        Assert.Equal("say", args["spokenCommand"]);
        Assert.Equal("Say hello world", args["spokenText"]);
        Assert.Equal("hello world", args["spokenTextInput"]);
        Assert.Equal(0.873, args["spokenTextConfidence"]);
        Assert.Equal(87, args["spokenTextConfidencePercent"]);
        Assert.Equal("Stay hello world", args["altPhraseText1"]);
        Assert.Equal(41, args["altPhraseConfidencePercent1"]);
        Assert.Equal(2, args["altPhraseCount"]);
        Assert.Equal(command.Id.ToString(), args["voiceCommandId"]);

        var dictation = VoiceControlArgs.ForDictation(result, [], "whisper");
        Assert.Equal("", dictation["spokenCommand"]);
        Assert.Equal("Say hello world", dictation["altPhraseText0"]);
    }
}
