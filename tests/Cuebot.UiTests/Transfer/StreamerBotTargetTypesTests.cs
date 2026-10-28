using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Cuebot.App;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Transfer;
using Cuebot.Core.Transfer.StreamerBot;

namespace Cuebot.UiTests.Transfer;

/// <summary>
/// Every Streamer.bot mapping rule targets a sub-action or trigger type the full app registers, and the converted
/// steps and triggers only set parameters those types declare. Triggers are checked with every Streamer.bot 1.0.7
/// trigger as it saves a freshly added one (Cuebot.Tests/TestData/streamerbot-1.0.7-trigger-defaults.json).
/// </summary>
public sealed class StreamerBotTargetTypesTests
{
    // One hand-made item per numeric code, shaped like public exports.
    private const string AllCodes = """
        { "data": { "actions": [ { "id": "11111111-2222-4333-8444-555555555555", "name": "All", "triggers": [
            { "type": 401, "commandId": "x" }, { "type": 701, "timerId": "x" }, { "type": 101 }, { "type": 102, "min": 1, "max": -1 },
            { "type": 103, "tiers": 16 }, { "type": 104, "tiers": 16, "min": 1, "max": -1 }, { "type": 105, "tiers": 16 },
            { "type": 106, "tiers": 16, "min": 1 }, { "type": 107, "min": 1 }, { "type": 112, "rewardId": "" },
            { "type": 118, "gameOnly": false, "gameName": "Chess" }, { "type": 120, "username": "" }, { "type": 124 }, { "type": 133 },
            { "type": 137 }, { "type": 139 }, { "type": 154 }, { "type": 155 }, { "type": 186, "minutes": [3] }, { "type": 472, "min": 1 },
            { "type": 706 }, { "type": 4001 }, { "type": 4002 }, { "type": 4003 }, { "type": 4005 }, { "type": 4016, "username": "" },
            { "type": 14004, "sceneName": "A", "obsId": null }, { "type": 14005 }, { "type": 18001, "eventName": "e" }, { "type": 18002, "name": "n", "eventName": "e" } ],
          "actions": [
            { "type": 1, "soundFile": "a.wav", "volume": 1, "finishBeforeContinuing": true },
            { "type": 4, "actionId": "11111111-2222-4333-8444-555555555555", "runImmedately": true },
            { "type": 5, "file": "a.txt", "count": 1, "variableName": "line" },
            { "type": 6, "command": "x", "arguments": "", "workingDir": "", "waitForExit": 0 },
            { "type": 7, "folder": "f", "recursive": true, "volume": 1 },
            { "type": 8, "timerId": "t", "state": 0 },
            { "type": 10, "text": "hi", "useBot": true }, { "type": 15, "title": "t" }, { "type": 16, "source": 0, "game": "g" },
            { "type": 17, "description": "d" }, { "type": 20, "userLogin": "u", "duration": 1, "reason": "" }, { "type": 21 }, { "type": 24 },
            { "type": 29, "userLogin": "u" }, { "type": 30, "sceneName": "s", "sourceName": "x", "state": 0 },
            { "type": 35, "sceneName": "s", "sourceName": "x", "filterName": "f", "state": 1 },
            { "type": 38, "sceneName": "s", "sourceName": "x", "url": "u" }, { "type": 39, "sceneName": "s", "sourceName": "x", "text": "t" },
            { "type": 46, "raw": "{\"requestType\":\"GetStats\"}", "prefix": "p", "resultsToArgs": false },
            { "type": 47, "sceneName": "s", "filterName": "f", "state": 2 }, { "type": 50, "sourceType": 1 }, { "type": 50, "sourceType": 0 },
            { "type": 51, "userLogin": "u" },
            { "type": 120, "input": "%a%", "operation": 6, "value": null, "subActions": [ { "type": 99901, "subActions": [ { "type": 124 } ] }, { "type": 99902 } ] },
            { "type": 120, "variableName": "a", "operation": 0, "value": "b", "actionId": "11111111-2222-4333-8444-555555555555", "afterAction": 1 },
            { "type": 121, "source": 0, "variableName": "a", "destinationVariable": "b" }, { "type": 121, "source": 2, "variableName": "a" },
            { "type": 122, "destination": 0, "source": 1, "variableName": "a", "value": "1" }, { "type": 122, "destination": 1, "source": 2, "variableName": "a" },
            { "type": 123, "variableName": "a", "value": "b" }, { "type": 128, "input": "%i%", "operation": 3, "value": "3", "subActions": [] },
            { "type": 322, "sourceName": "m", "fileName": "f" }, { "type": 323, "sourceName": "i", "fileName": "f" }, { "type": 328 },
            { "type": 500, "userLogin": "u" }, { "type": 506 }, { "type": 511, "userLogin": "u", "reason": "r" }, { "type": 550, "userIndex": 0 },
            { "type": 560, "text": "t", "replyId": "%msgId%" }, { "type": 601, "alias": "a", "message": "m" },
            { "type": 1000, "quoteType": 1, "quoteId": "1" }, { "type": 1002, "value": 10 }, { "type": 1003, "randomType": 0, "min": 1, "max": 2 },
            { "type": 1007, "url": "https://example.invalid", "variableName": "v", "headers": "{}" }, { "type": 1009, "value": "c" },
            { "type": 1013, "groupName": "All", "variableName": "c" }, { "type": 1020, "logLevel": 2, "message": "m" },
            { "type": 1024, "title": "t", "text": "x" }, { "type": 1036, "quoteId": "1" }, { "type": 1037, "quoteVariable": "q" },
            { "type": 4001, "text": "t" }, { "type": 4002, "title": "t" },
            { "type": 11001, "buttonId": "b", "title": "t" }, { "type": 11002, "buttonId": "b", "image": "i" }, { "type": 11008, "buttonId": "b", "value": "v" },
            { "type": 15001, "userLogin": "u", "prefix": "" }, { "type": 99900, "name": "g", "subActions": [] },
            { "type": 99998, "executeCodeId": "c", "method": "M" }, { "type": 99999, "name": "n", "code": "public class CPHInline {}" } ] } ] } }
        """;

    [AvaloniaFact]
    public async Task Every_rule_targets_a_registered_type_with_its_parameters()
    {
        var dir = Directory.CreateTempSubdirectory("cuebot-ui-sbtypes-").FullName;
        await using var host = AppHost.Create(new AppPaths(dir));
        var registry = host.Get<Registry>();
        var table = StreamerBotMappingTable.Default;
        Assert.All(table.SubActions, r => Assert.True(registry.GetSubAction(r.TargetType) is not null, $"{r.Name}: no sub-action {r.TargetType}"));
        Assert.All(table.Triggers, r => Assert.True(registry.GetTrigger(r.TargetType) is not null, $"{r.Name}: no trigger {r.TargetType}"));

        var triggerDefaults = TriggerDefaults();
        var (defaultsBundle, defaultsReport) = new StreamerBotImporter().Convert(triggerDefaults);
        Assert.Equal(StreamerBotMappingTable.NoEquivalentTriggers.Count, defaultsReport.Placeholders.Count);
        Assert.Equal(419 - StreamerBotMappingTable.NoEquivalentTriggers.Count, defaultsBundle.Actions.Sum(a => a.Triggers.Count));

        var jsons = new List<string> { AllCodes, triggerDefaults };
        var samples = Environment.GetEnvironmentVariable("CUEBOT_SB_SAMPLES") is { Length: > 0 } env ? env : @"C:\Users\docker\cuebot-sb-exports\samples";
        if (Directory.Exists(samples))
        {
            foreach (var file in Directory.EnumerateFiles(samples, "*.sb"))
            {
                try
                {
                    jsons.Add(TransferCodec.Decode(File.ReadAllText(file)).Json);
                }
                catch (TransferFormatException)
                {
                    // incomplete sample strings are reported by the Cuebot.Tests samples test
                }
            }
        }

        var (all, report) = new StreamerBotImporter().Convert(AllCodes);
        Assert.Empty(report.Placeholders);
        foreach (var json in jsons)
        {
            var (bundle, _) = new StreamerBotImporter().Convert(json);
            foreach (var action in bundle.Actions)
            {
                foreach (var step in TransferReferences.Walk(action.SubActions).Where(s => !BundleImporter.IsPlaceholder(s)))
                {
                    var handler = registry.GetSubAction(step.Type);
                    Assert.True(handler is not null, $"no sub-action {step.Type}");
                    var keys = handler!.Parameters.Select(p => p.Key).ToHashSet();
                    foreach (var key in step.Parameters.Keys) Assert.True(keys.Contains(key), $"{step.Type} has no parameter '{key}'");
                }
                foreach (var trigger in action.Triggers)
                {
                    var type = registry.GetTrigger(trigger.Type);
                    Assert.True(type is not null, $"no trigger {trigger.Type}");
                    var keys = type!.Parameters.Select(p => p.Key).ToHashSet();
                    foreach (var key in trigger.Settings.Keys) Assert.True(keys.Contains(key), $"{trigger.Type} has no setting '{key}'");
                }
            }
        }
        Assert.NotEmpty(all.Actions);
    }

    /// <summary>One action per Streamer.bot 1.0.7 trigger code, with the settings it saves by default.</summary>
    private static string TriggerDefaults()
    {
        var defaults = (JsonArray)JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "streamerbot-1.0.7-trigger-defaults.json")))!;
        Assert.Equal(419, defaults.Count);
        var actions = new JsonArray();
        foreach (var item in defaults.OfType<JsonObject>())
        {
            var trigger = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["type"] = item["type"]!.DeepClone(), ["enabled"] = true };
            foreach (var (key, value) in (JsonObject)item["settings"]!) trigger[key] = value?.DeepClone();
            actions.Add(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString(), ["name"] = $"T{item["type"]}", ["triggers"] = new JsonArray(trigger), ["subActions"] = new JsonArray(),
            });
        }
        return new JsonObject { ["data"] = new JsonObject { ["actions"] = actions }, ["version"] = 24 }.ToJsonString();
    }
}
