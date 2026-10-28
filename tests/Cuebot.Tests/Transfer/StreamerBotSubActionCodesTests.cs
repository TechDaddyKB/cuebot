using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Transfer;
using Cuebot.Core.Transfer.StreamerBot;
using static Cuebot.Core.Transfer.StreamerBot.SbJson;

namespace Cuebot.Tests.Transfer;

/// <summary>
/// Every sub-action Streamer.bot 1.0.7 accepts, as it saves a freshly added one (TestData/streamerbot-1.0.7-subaction-defaults.json:
/// the type code and its default settings, read from a real install on 2026-10-28). Each code either maps to a Cuebot
/// sub-action or is listed in <see cref="StreamerBotMappingTable.NoEquivalentSubActions"/>. Cuebot.UiTests checks the
/// mapped ones against the full app's registered sub-action types and their parameters.
/// </summary>
public sealed class StreamerBotSubActionCodesTests
{
    private const int CodeCount = 395;

    public static JsonArray Defaults() =>
        (JsonArray)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "streamerbot-1.0.7-subaction-defaults.json")))!;

    /// <summary>One action per sub-action ("S&lt;code&gt;"), the settings flat on the sub-action as exports store them.</summary>
    public static string Export(JsonArray defaults)
    {
        var actions = new JsonArray();
        foreach (var item in defaults.OfType<JsonObject>())
        {
            var code = Str(item, "type")!;
            var sub = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["type"] = int.Parse(code), ["enabled"] = true, ["weight"] = 0, ["index"] = 0 };
            foreach (var (key, value) in Obj(item, "settings")!) sub[key] = value?.DeepClone();
            actions.Add(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(), ["name"] = "S" + code, ["enabled"] = true,
                ["triggers"] = new JsonArray(), ["subActions"] = new JsonArray(sub),
            });
        }
        return new JsonObject { ["data"] = new JsonObject { ["actions"] = actions }, ["version"] = 24 }.ToJsonString();
    }

    [Fact]
    public void Every_sub_action_code_maps_or_is_listed_as_having_no_equivalent()
    {
        var defaults = Defaults();
        Assert.Equal(CodeCount, defaults.Count);
        var table = StreamerBotMappingTable.Default;
        var (bundle, report) = new StreamerBotImporter().Convert(Export(defaults));
        var byName = bundle.Actions.ToDictionary(a => a.Name);
        var mapped = 0;
        foreach (var item in defaults.OfType<JsonObject>())
        {
            var code = Str(item, "type")!;
            var step = Assert.Single(byName["S" + code].SubActions);
            if (StreamerBotMappingTable.NoEquivalentSubActions.ContainsKey(code))
            {
                Assert.True(BundleImporter.IsPlaceholder(step), code);
                continue;
            }
            Assert.False(BundleImporter.IsPlaceholder(step), $"{code}: {step.Parameters.GetValueOrDefault("text")}");
            var rule = table.SubActions.Single(r => r.TypeKeys.Contains(code));
            Assert.Equal(rule.TargetType, step.Type);
            Assert.Equal(StreamerBotMappingTable.TentativeSubActionCodes.Contains(code), rule.Tentative);
            mapped++;
        }
        Assert.Equal(CodeCount - StreamerBotMappingTable.NoEquivalentSubActions.Count, mapped);
        Assert.Equal(351, mapped);
        Assert.Equal(44, report.Placeholders.Count);

        // Every listed gap and tentative code is a real 1.0.7 code, no gap also has a rule, and every numeric code a
        // rule names is one Streamer.bot 1.0.7 knows.
        var codes = defaults.OfType<JsonObject>().Select(i => Str(i, "type")!).ToHashSet();
        Assert.All(StreamerBotMappingTable.NoEquivalentSubActions.Keys, k => Assert.Contains(k, codes));
        Assert.All(StreamerBotMappingTable.TentativeSubActionCodes, k => Assert.Contains(k, codes));
        Assert.All(StreamerBotMappingTable.NoEquivalentSubActions.Keys, k => Assert.DoesNotContain(table.SubActions, r => r.TypeKeys.Contains(k)));
        Assert.All(table.SubActions.SelectMany(r => r.TypeKeys).Where(k => k.All(char.IsAsciiDigit)), k => Assert.Contains(k, codes));
    }

    [Fact]
    public void Sub_action_rule_names_and_type_keys_are_unique()
    {
        var rules = StreamerBotMappingTable.Default.SubActions;
        Assert.Equal(rules.Count, rules.Select(r => r.Name).Distinct().Count());
        Assert.Empty(rules.SelectMany(r => r.TypeKeys.Select(Normalize)).GroupBy(k => k).Where(g => g.Count() > 1).Select(g => g.Key));
    }

    [Fact]
    public void Default_settings_import_with_only_the_expected_reports()
    {
        // A freshly added sub-action names nothing, so the only reports are for steps that cannot run until they are
        // filled in (imported disabled), the program and C# notices and the Wave Link "all outputs" note.
        var (bundle, report) = new StreamerBotImporter().Convert(Export(Defaults()));
        var expected = new[] { "S4", "S6", "S16", "S46", "S1012", "S14002", "S14008", "S99998", "S99999" };
        Assert.DoesNotContain(report.Warnings, w => !expected.Any(name => w.StartsWith($"'{name}'", StringComparison.Ordinal)));
        var disabled = bundle.Actions.Where(a => TransferReferences.Walk(a.SubActions).Any(s => !s.Enabled && !BundleImporter.IsPlaceholder(s))).Select(a => a.Name);
        Assert.Equal(["S1012", "S16", "S4", "S46", "S99998"], disabled.Order(StringComparer.Ordinal));
    }

    private static (SubActionDefinition Step, IList<string> Warnings) One(int code, params (string Key, JsonNode? Value)[] settings)
    {
        var item = new JsonObject { ["type"] = code, ["settings"] = new JsonObject() };
        foreach (var (key, value) in settings) item["settings"]![key] = value;
        var (bundle, report) = new StreamerBotImporter().Convert(Export([item]));
        return (Assert.Single(Assert.Single(bundle.Actions).SubActions), report.Warnings);
    }

    [Fact]
    public void States_follow_the_documented_option_order()
    {
        Assert.Equal(("commands.setState", "Toggle"), (One(9, ("state", 2)).Step.Type, One(9, ("state", 2)).Step.Parameters["state"]));
        Assert.Equal("Disable", One(1004, ("state", 1)).Step.Parameters["state"]);
        Assert.Equal("false", One(13, ("status", 1)).Step.Parameters["enabled"]);
        // A real export ends the stream with OBS Streaming state 0.
        Assert.Equal(("obs.streaming", "Stop"), (One(33).Step.Type, One(33).Step.Parameters["mode"]));
        // Streamer.bot's UI shows fresh steps as "Start Recording", "Redemption Cancel", "Random Filter State (… Visible)" and
        // "Source Mute State (… Muted)".
        Assert.Equal(("obs.recording", "Start"), (One(45).Step.Type, One(45).Step.Parameters["mode"]));
        Assert.Equal("Cancel", One(11).Step.Parameters["status"]);
        Assert.Equal(("obs.setRandomFilterState", "Enable"), (One(40).Step.Type, One(40).Step.Parameters["state"]));
        Assert.Equal(("obs.setInputMute", "Mute"), (One(42).Step.Type, One(42).Step.Parameters["state"]));
        Assert.Equal(("slobs.setRandomFilterState", "slobs.setSourceMute"), (One(411).Step.Type, One(413).Step.Type));
        Assert.Equal("twitch.getCharityCampaign", One(25).Step.Type);
        Assert.Equal(("meldstudio.streaming", "meldstudio.recording"), (One(41003).Step.Type, One(41004).Step.Type));
        Assert.Equal(("users.resetFirstWords", "credits.reset"), (One(1026).Step.Type, One(1027).Step.Type));
        Assert.Equal(("twitch.setRewardMaxPerUser", "twitch.setRewardMaxPerStream"), (One(214).Step.Type, One(215).Step.Type));
        Assert.Equal("Save", One(321, ("state", 2)).Step.Parameters["mode"]);
        Assert.Equal("Track 3", "Track " + One(44, ("track", 2)).Step.Parameters["track"]);
        Assert.Equal("Archive", One(563).Step.Parameters["mode"]);
        Assert.Equal("Pause", One(1001, ("state", 0)).Step.Parameters["state"]);
        Assert.Equal("All", One(1001).Step.Parameters["queue"]);
        // An index past the documented options cannot be converted.
        Assert.True(BundleImporter.IsPlaceholder(One(13, ("status", 2)).Step));
    }

    [Fact]
    public void Obs_status_and_record_chapter_use_their_verified_codes()
    {
        Assert.Equal("obs.getStatus", One(328).Step.Type);
        var chapter = One(329, ("chapterName", "Intro")).Step;
        Assert.Equal(("obs.createRecordChapter", "Intro"), (chapter.Type, chapter.Parameters["chapterName"]));
    }

    [Fact]
    public void Switch_cases_become_case_lines_with_their_branches()
    {
        var log = new JsonObject { ["type"] = 1020, ["logLevel"] = 2, ["message"] = "m" };
        var step = One(127, ("input", "%rawInput%"), ("subActions", new JsonArray(
            new JsonObject { ["type"] = 99903, ["values"] = new JsonArray("a", "b"), ["caseSensitive"] = false, ["subActions"] = new JsonArray(log.DeepClone()) },
            new JsonObject { ["type"] = 99903, ["values"] = new JsonArray("c"), ["subActions"] = new JsonArray() },
            new JsonObject { ["type"] = 99904, ["subActions"] = new JsonArray(log.DeepClone()) }))).Step;
        Assert.Equal("core.switch", step.Type);
        Assert.Equal("a | b\nc", step.Parameters["cases"]);
        Assert.Equal("core.log", Assert.Single(step.Branches["a | b"]).Type);
        Assert.Empty(step.Branches["c"]);
        Assert.Equal("core.log", Assert.Single(step.Branches["Default"]).Type);
        Assert.Equal(["a | b", "c", "Default"], SwitchSubAction.ParseCases(step.Parameters["cases"]).Append(SwitchSubAction.Default));
    }

    [Fact]
    public void File_exists_sets_the_argument_streamer_bot_would()
    {
        var step = One(1039, ("file", @"C:\a.txt"), ("variableName", "hasFile")).Step;
        Assert.Equal(("core.fileExists", "File"), (step.Type, step.Parameters["kind"]));
        Assert.Equal(("hasFile", "True"), (step.Branches["True"][0].Parameters["name"], step.Branches["True"][0].Parameters["value"]));
        Assert.Equal("False", step.Branches["False"][0].Parameters["value"]);
        Assert.Equal("folderExists", One(1040).Step.Branches["True"][0].Parameters["name"]);
    }

    [Fact]
    public void Settings_carry_names_lists_and_numbers()
    {
        var group = One(553, ("userLogin", "viewer"), ("groupName", "VIPs")).Step;
        Assert.Equal(("groups.addUser", "VIPs", "twitch", "viewer"), (group.Type, group.Parameters["group"], group.Parameters["platform"], group.Parameters["user"]));
        Assert.Equal("%user%", One(4007).Step.Parameters["user"]);
        Assert.Equal("60", One(19, ("duration", 1)).Step.Parameters["length"]);
        Assert.Equal("#FF102030", One(1008, ("alpha", 255), ("red", 16), ("green", 32), ("blue", 48)).Step.Parameters["color"]);
        Assert.Equal("a=1\nb=x", One(1031, ("customArgs", new JsonObject { ["a"] = 1, ["b"] = "x" })).Step.Parameters["arguments"]);
        Assert.Equal("2", One(571, ("winningIndex", 1)).Step.Parameters["winner"]);
        Assert.Equal(("Stream", "Both"), (One(14008, ("mixerType", "com.elgato.mix.stream")).Step.Parameters["mix"], One(14010).Step.Parameters["mix"]));
        Assert.Contains(One(14002).Warnings, w => w.Contains("both Wave Link outputs"));
        var kick = One(35107, ("rewardId", "r1"), ("title", "T"), ("cost", "50"), ("operator", 0)).Step;
        Assert.Equal(["kick.setRewardTitle", "kick.setRewardCost"], kick.Branches["Steps"].Select(s => s.Type));
        var delay = One(1002, ("value", "100"), ("maxValue", "900"), ("random", true)).Step;
        Assert.Equal(("100", "true", "900"), (delay.Parameters["milliseconds"], delay.Parameters["random"], delay.Parameters["to"]));
    }
}
