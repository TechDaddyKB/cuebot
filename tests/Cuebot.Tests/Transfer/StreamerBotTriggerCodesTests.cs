using System.Text.Json.Nodes;
using Cuebot.Core.Transfer;
using Cuebot.Core.Transfer.StreamerBot;
using static Cuebot.Core.Transfer.StreamerBot.SbJson;

namespace Cuebot.Tests.Transfer;

/// <summary>
/// Every trigger Streamer.bot 1.0.7 offers, as it saves a freshly added one (TestData/streamerbot-1.0.7-trigger-defaults.json:
/// the type code and its default settings, read from a real install on 2026-10-28). Each code either maps to a Cuebot
/// trigger or is listed in <see cref="StreamerBotMappingTable.NoEquivalentTriggers"/>. Cuebot.UiTests checks the
/// mapped ones against the full app's registered trigger types and their parameters.
/// </summary>
public sealed class StreamerBotTriggerCodesTests
{
    public static JsonArray Defaults() =>
        (JsonArray)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "streamerbot-1.0.7-trigger-defaults.json")))!;

    /// <summary>One action per trigger ("T&lt;code&gt;"), the settings flat on the trigger as exports store them.</summary>
    public static string Export(JsonArray defaults)
    {
        var actions = new JsonArray();
        foreach (var item in defaults.OfType<JsonObject>())
        {
            var code = Str(item, "type")!;
            var trigger = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["type"] = int.Parse(code), ["enabled"] = true, ["exclusions"] = new JsonArray() };
            foreach (var (key, value) in Obj(item, "settings")!) trigger[key] = value?.DeepClone();
            actions.Add(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(), ["name"] = "T" + code, ["enabled"] = true,
                ["triggers"] = new JsonArray(trigger), ["subActions"] = new JsonArray(),
            });
        }
        return new JsonObject { ["data"] = new JsonObject { ["actions"] = actions }, ["version"] = 24 }.ToJsonString();
    }

    [Fact]
    public void Every_trigger_code_maps_or_is_listed_as_having_no_equivalent()
    {
        var defaults = Defaults();
        Assert.Equal(419, defaults.Count);
        var table = StreamerBotMappingTable.Default;
        var (bundle, report) = new StreamerBotImporter().Convert(Export(defaults));
        var byName = bundle.Actions.ToDictionary(a => a.Name);
        var mapped = 0;
        foreach (var item in defaults.OfType<JsonObject>())
        {
            var code = Str(item, "type")!;
            var action = byName["T" + code];
            if (StreamerBotMappingTable.NoEquivalentTriggers.ContainsKey(code))
            {
                Assert.Empty(action.Triggers);
                Assert.True(BundleImporter.IsPlaceholder(Assert.Single(action.SubActions)), code);
                continue;
            }
            var trigger = Assert.Single(action.Triggers);
            var rule = table.Triggers.Single(r => r.TypeKeys.Contains(code));
            Assert.Equal(rule.TargetType, trigger.Type);
            Assert.False(rule.Tentative, code);
            Assert.Empty(action.SubActions);
            mapped++;
        }
        Assert.Equal(419 - StreamerBotMappingTable.NoEquivalentTriggers.Count, mapped);
        Assert.Equal(371, mapped);
        Assert.Equal(48, report.Placeholders.Count);
        // Every listed gap is a real 1.0.7 code, and no gap also has a rule.
        var codes = defaults.OfType<JsonObject>().Select(i => Str(i, "type")!).ToHashSet();
        Assert.All(StreamerBotMappingTable.NoEquivalentTriggers.Keys, k => Assert.Contains(k, codes));
        Assert.All(StreamerBotMappingTable.NoEquivalentTriggers.Keys, k => Assert.DoesNotContain(table.Triggers, r => r.TypeKeys.Contains(k)));
    }

    [Fact]
    public void Trigger_rule_names_and_type_keys_are_unique()
    {
        var rules = StreamerBotMappingTable.Default.Triggers;
        Assert.Equal(rules.Count, rules.Select(r => r.Name).Distinct().Count());
        var keys = rules.SelectMany(r => r.TypeKeys.Select(Normalize)).ToList();
        Assert.Equal(keys.Count, keys.Distinct().Count());
    }

    [Fact]
    public void Default_settings_import_without_filters_or_warnings()
    {
        // A freshly added trigger filters on nothing, so nothing is dropped; only the unset command/timer ids, the
        // disabled input triggers and the StreamLoots reaction stand-in are reported.
        var (bundle, report) = new StreamerBotImporter().Convert(Export(Defaults()));
        var expected = new[] { "T401", "T402", "T701", "T16001", "T16002", "T36004" };
        Assert.All(report.Warnings, w => Assert.Contains(expected, name => w.StartsWith($"'{name}'", StringComparison.Ordinal)));
        Assert.Equal(["T16001", "T16002"], bundle.Actions.Where(a => a.Triggers.Any(t => !t.Enabled)).Select(a => a.Name).Order());
    }

    private static TriggerSettings One(int code, params (string Key, JsonNode? Value)[] settings)
    {
        var item = new JsonObject { ["type"] = code, ["settings"] = new JsonObject() };
        foreach (var (key, value) in settings) item["settings"]![key] = value;
        var (bundle, report) = new StreamerBotImporter().Convert(Export([item]));
        var trigger = Assert.Single(Assert.Single(bundle.Actions).Triggers);
        return new(trigger.Type, trigger.Settings, report.Warnings);
    }

    private sealed record TriggerSettings(string Type, IDictionary<string, string> Settings, IList<string> Warnings);

    [Fact]
    public void Range_filters_become_min_and_max_settings()
    {
        var tip = One(1201, ("min", 5), ("max", 50));
        Assert.Equal(("streamelements.tip", "5", "50"), (tip.Type, tip.Settings["minAmount"], tip.Settings["maxAmount"]));
        // A Max alone is an exact value.
        var exact = One(201, ("min", -1), ("max", 10));
        Assert.Equal(("10", "10"), (exact.Settings["minAmount"], exact.Settings["maxAmount"]));
        var open = One(7001, ("min", -1), ("max", -1));
        Assert.Equal(("0", "0"), (open.Settings["minAmount"], open.Settings["maxAmount"]));
        Assert.Empty(open.Warnings);
        // Only a minimum in Cuebot: the maximum is reported.
        var viewers = One(164, ("min", 10), ("max", 20));
        Assert.Equal("10", viewers.Settings["minViewers"]);
        Assert.Contains(viewers.Warnings, w => w.Contains("'max'"));
        Assert.Equal("2", One(110, ("min", -1), ("max", -1)).Settings["minLevel"]);
        Assert.Equal("5", One(497, ("min", 5), ("max", -1)).Settings["minMonths"]);
        var cheer = One(102, ("min", -1), ("max", 500));
        Assert.Equal(("500", "500"), (cheer.Settings["minBits"], cheer.Settings["maxBits"]));
        // No range at all in Cuebot.
        var pulse = One(5001, ("min", 100), ("max", -1));
        Assert.Equal("pulsoid.heartRate", pulse.Type);
        Assert.Contains(pulse.Warnings, w => w.Contains("'min'"));
    }

    [Fact]
    public void Named_filters_are_carried_and_ids_are_reported()
    {
        var variable = One(710, ("userLogon", "viewer"), ("platform", "twitch"), ("variableName", "points"), ("persisted", 1));
        Assert.Equal(("points", "Yes", "viewer", "twitch"),
            (variable.Settings["variable"], variable.Settings["persisted"], variable.Settings["userLogin"], variable.Settings["platform"]));
        Assert.Equal("No", One(709, ("persisted", 2)).Settings["persisted"]);

        var model = One(19001, ("modelId", "abc"), ("modelName", "Akari"));
        Assert.Equal("Akari", model.Settings["modelName"]);
        Assert.Empty(model.Warnings);
        var byId = One(19001, ("modelId", "abc"), ("modelName", null));
        Assert.Equal("", byId.Settings["modelName"]);
        Assert.Contains(byId.Warnings, w => w.Contains("'modelId'"));

        var meter = One(21019, ("identifier", "PCM_IN_01"), ("name", "Mic"));
        Assert.Equal("Mic", meter.Settings["levelName"]);
        Assert.Contains(One(21020, ("mixerType", "com.elgato.mix.stream")).Warnings, w => w.Contains("'mixerType'"));

        var effect = One(20003, ("gameName", "Doom"), ("effectName", "Spawn"), ("effectType", 0), ("min", 100), ("max", -1));
        Assert.Equal(("Spawn", "Doom", "100"), (effect.Settings["effectName"], effect.Settings["gameName"], effect.Settings["minPrice"]));
        Assert.Equal("free", One(20009, ("coinType", "Free")).Settings["coinType"]);

        Assert.Equal("BRB", One(33007, ("sceneName", "BRB")).Settings["scene"]);
        Assert.Equal("SceneStaged", One(33012, ("eventName", "SceneStaged")).Settings["event"]);
        var obs = One(14009, ("vendorName", "move"), ("eventName", "done"), ("obsId", null));
        Assert.Equal(("move", "done", ""), (obs.Settings["vendor"], obs.Settings["vendorEventType"], obs.Settings["connection"]));
        Assert.Equal("CustomEvent", One(14003, ("eventName", "CustomEvent")).Settings["eventType"]);
        Assert.Equal("hook", One(34001, ("webhookId", Guid.NewGuid().ToString()), ("webhookName", "hook")).Settings["hookName"]);
        Assert.Equal("sig", One(29003, ("eventName", "sig")).Settings["signalName"]);
        Assert.Equal("nofx", One(30003, ("voiceId", "nofx")).Settings["voice"]);
        Assert.Contains(One(32003, ("groupId", Guid.NewGuid().ToString())).Warnings, w => w.Contains("'groupId'"));
        Assert.Equal(("chat.emoteAdded", "BTTV"), (One(182).Type, One(182).Settings["provider"]));
        Assert.Equal(("users.presentViewers", "YouTube"), (One(4017).Type, One(4017).Settings["platform"]));
        Assert.Equal(("users.firstWords", "Kick"), (One(35008).Type, One(35008).Settings["platform"]));
    }

    [Fact]
    public void Sub_tier_masks_other_than_every_tier_are_reported()
    {
        var all = One(103, ("tiers", 16));
        Assert.Equal("Any", all.Settings["tier"]);
        Assert.Empty(all.Warnings);
        var some = One(103, ("tiers", 4));
        Assert.Equal("Any", some.Settings["tier"]);
        Assert.Contains(some.Warnings, w => w.Contains("'tiers'"));
        var gift = One(105, ("tiers", 16), ("min", -1), ("max", -1), ("subType", 1), ("monthsGifted", 15));
        Assert.Single(gift.Warnings, w => w.Contains("'subType'"));
    }
}
