using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using static Cuebot.Tests.Engine.EngineHarness;

namespace Cuebot.Tests.Engine;

public class ProfileFormatTests
{
    /// <summary>An actions.json written before wave 2 (no queues, weights or action options).</summary>
    private const string OldProfile = """
        {
          "version": 1,
          "actions": [
            {
              "id": "5b0c7d0e-8c8e-4a39-9d1f-0e6f7d7a1a01",
              "name": "Greet",
              "group": "Chat",
              "enabled": true,
              "concurrent": false,
              "triggers": [ { "id": "5b0c7d0e-8c8e-4a39-9d1f-0e6f7d7a1a02", "type": "test.event", "enabled": true, "settings": {} } ],
              "subActions": [
                {
                  "id": "5b0c7d0e-8c8e-4a39-9d1f-0e6f7d7a1a03",
                  "type": "core.if",
                  "enabled": true,
                  "parameters": { "left": "%user%", "operator": "equals", "right": "alice" },
                  "branches": {
                    "True": [ { "id": "5b0c7d0e-8c8e-4a39-9d1f-0e6f7d7a1a04", "type": "test.record", "enabled": true, "parameters": { "text": "hi %user%" }, "branches": {} } ],
                    "False": []
                  },
                  "layout": { "x": 1, "y": 2 }
                }
              ]
            }
          ],
          "timers": []
        }
        """;

    [Fact]
    public async Task Old_format_profile_loads_with_defaults_and_behaves_the_same()
    {
        using var dir = new EngineTempDir();
        await File.WriteAllTextAsync(Path.Combine(dir.Path, "actions.json"), OldProfile);
        using var log = new AppLog();
        using var profile = new ProfileService(new AppPaths(dir.Path), log);

        var action = profile.Snapshot.Actions.Single();
        Assert.Equal("", action.Queue);
        Assert.False(action.RandomAction || action.ConcurrentSubActions || action.AlwaysRun || action.ExcludeFromHistory);
        Assert.Equal(1, action.SubActions.Single().Weight);
        Assert.Empty(profile.Snapshot.Queues);

        using var h = new EngineHarness();
        h.Profile.Profile.Actions.Add(Json.Clone(profile.Profile.Actions.Single()));
        h.Profile.NotifyChanged();
        await Task.WhenAll(h.Engine.Dispatch(new Core.Events.BotEvent("test.event",
            new Dictionary<string, object?> { ["user"] = "Alice" })));
        Assert.Equal(["hi Alice"], h.Calls);
        Assert.Equal(QueueService.DefaultQueue, h.History.Entries.Single().Queue);
    }

    [Fact]
    public void New_fields_round_trip_through_json()
    {
        var group = Group(random: true, Record("a", weight: 2.5));
        var profile = new BotProfile
        {
            Actions =
            [
                new ActionDefinition
                {
                    Name = "A", Queue = "Alerts", RandomAction = true, ConcurrentSubActions = true, AlwaysRun = true,
                    ExcludeFromHistory = true, SubActions = [group],
                },
            ],
            Queues = [new QueueDefinition { Name = "Alerts", Blocking = true }],
        };

        var json = JsonSerializer.Serialize(profile, Json.Options);
        foreach (var name in new[] { "\"queue\"", "\"randomAction\"", "\"concurrentSubActions\"", "\"alwaysRun\"",
                     "\"excludeFromHistory\"", "\"weight\"", "\"queues\"", "\"blocking\"" })
        {
            Assert.Contains(name, json);
        }

        var back = JsonSerializer.Deserialize<BotProfile>(json, Json.Options)!;
        var action = back.Actions.Single();
        Assert.Equal("Alerts", action.Queue);
        Assert.True(action.RandomAction && action.ConcurrentSubActions && action.AlwaysRun && action.ExcludeFromHistory);
        var loadedGroup = action.SubActions.Single();
        Assert.Equal("core.group", loadedGroup.Type);
        Assert.Equal("true", loadedGroup.Parameters["random"]);
        Assert.Equal("#336699", loadedGroup.Parameters["color"]);
        Assert.Equal(2.5, loadedGroup.Branches["Steps"].Single().Weight);
        var queue = back.Queues.Single();
        Assert.Equal(("Alerts", true), (queue.Name, queue.Blocking));
    }

    [Fact]
    public void Queues_persist_through_the_profile_service()
    {
        using var dir = new EngineTempDir();
        using var log = new AppLog();
        var paths = new AppPaths(dir.Path);
        using (var profile = new ProfileService(paths, log))
        {
            profile.Profile.Queues.Add(new QueueDefinition { Name = "Alerts", Blocking = true });
            profile.NotifyChanged();
            Assert.True(profile.Snapshot.FindQueue("alerts")!.Blocking);
        }

        using var reloaded = new ProfileService(paths, log);
        Assert.Equal("Alerts", reloaded.Snapshot.Queues.Single().Name);
    }

    [Fact]
    public void Null_queue_list_in_json_is_tolerated()
    {
        using var dir = new EngineTempDir();
        File.WriteAllText(Path.Combine(dir.Path, "actions.json"), """{ "version": 1, "actions": [], "queues": null }""");
        using var log = new AppLog();
        using var profile = new ProfileService(new AppPaths(dir.Path), log);
        Assert.Empty(profile.Snapshot.Queues);
    }
}
