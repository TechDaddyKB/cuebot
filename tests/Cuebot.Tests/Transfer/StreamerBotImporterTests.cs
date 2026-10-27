using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Transfer;
using Cuebot.Core.Transfer.StreamerBot;

namespace Cuebot.Tests.Transfer;

/// <summary>
/// The Streamer.bot importer against synthetic exports built to match the publicly described format
/// (base64 of "SBAE" + gzip JSON). Field names inside the JSON are assumptions; see StreamerBotImporter.
/// </summary>
public sealed class StreamerBotImporterTests : IDisposable
{
    private static readonly string FollowId = Guid.NewGuid().ToString();
    private static readonly string HelperId = Guid.NewGuid().ToString();
    private static readonly string QueueId = Guid.NewGuid().ToString();
    private static readonly string CommandId = Guid.NewGuid().ToString();
    private static readonly string TimerId = Guid.NewGuid().ToString();
    private const string Code = "public class CPHInline { public bool Execute() { CPH.SendMessage(\"hi\"); return true; } }";

    private readonly TransferTestEnv _env = new();

    private static string NestedSample() => new JsonObject
    {
        ["meta"] = new JsonObject { ["name"] = "Alerts pack", ["author"] = "Tester", ["version"] = "1.2", ["description"] = "Follow alerts" },
        ["data"] = new JsonObject
        {
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = FollowId, ["name"] = "Follow alert", ["group"] = "Alerts", ["enabled"] = true, ["queue"] = QueueId,
                    ["triggers"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "Follow", ["enabled"] = true },
                        new JsonObject { ["type"] = "MysteryTrigger", ["x"] = 1 },
                    },
                    ["subActions"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "SendMessage", ["text"] = "Thanks %user%", ["useBot"] = true, ["index"] = 0 },
                        new JsonObject { ["type"] = "Delay", ["delay"] = 1500, ["index"] = 1 },
                        new JsonObject { ["type"] = "SetGlobal", ["variableName"] = "follows", ["value"] = "1", ["persisted"] = true, ["index"] = 2 },
                        new JsonObject { ["type"] = "ObsSetScene", ["sceneName"] = "Alert", ["index"] = 3 },
                        new JsonObject { ["type"] = "ObsSetSourceVisibility", ["sceneName"] = "Alert", ["sourceName"] = "Confetti", ["state"] = "visible", ["index"] = 4 },
                        new JsonObject { ["type"] = "PlaySound", ["soundFile"] = "C:/a.wav", ["volume"] = 0.5, ["finishBeforeContinuing"] = true, ["index"] = 5 },
                        new JsonObject
                        {
                            ["type"] = "IfElse", ["input"] = "%user%", ["operation"] = "Equals", ["value"] = "bob", ["index"] = 6,
                            ["true"] = new JsonArray { new JsonObject { ["type"] = "RunAction", ["actionId"] = HelperId } },
                            ["false"] = new JsonArray { new JsonObject { ["type"] = "Comment", ["text"] = "not bob" } },
                        },
                        new JsonObject
                        {
                            ["type"] = "Group", ["name"] = "Fx", ["index"] = 7,
                            ["subActions"] = new JsonArray { new JsonObject { ["type"] = "GetGlobal", ["variableName"] = "follows", ["destinationVariable"] = "f" } },
                        },
                        new JsonObject { ["type"] = "WeirdThing", ["foo"] = "bar", ["enabled"] = true, ["index"] = 8 },
                    },
                },
                new JsonObject
                {
                    ["id"] = HelperId, ["name"] = "Helper", ["enabled"] = true,
                    ["triggers"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "Command", ["commandId"] = CommandId },
                        new JsonObject { ["type"] = "TimedAction", ["timerId"] = TimerId },
                        new JsonObject { ["type"] = "Cheer", ["min"] = 100, ["max"] = 0 },
                        new JsonObject { ["type"] = "Raid", ["min"] = 5 },
                        new JsonObject { ["type"] = "RewardRedemption", ["rewardName"] = "Hydrate", ["rewardId"] = "abc" },
                        new JsonObject { ["type"] = "Sub", ["tier"] = "2000" },
                    },
                    ["subActions"] = new JsonArray
                    {
                        new JsonObject { ["type"] = "ExecuteCode", ["byteCode"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(Code)) },
                    },
                },
            },
            ["queues"] = new JsonArray { new JsonObject { ["id"] = QueueId, ["name"] = "Alerts Q", ["blocking"] = true } },
            ["commands"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = CommandId, ["name"] = "Hello", ["command"] = "!hello\r\n!hi", ["enabled"] = true, ["mode"] = 0, ["location"] = 1,
                    ["globalCooldown"] = 10, ["userCooldown"] = 30, ["caseSensitive"] = false,
                },
            },
            ["timers"] = new JsonArray { new JsonObject { ["id"] = TimerId, ["name"] = "Promo", ["interval"] = 600, ["enabled"] = true, ["lines"] = 5 } },
        },
        ["version"] = 23,
        ["exportedFrom"] = "0.2.5",
    }.ToJsonString();

    [Fact]
    public void Decodes_the_SBAE_gzip_base64_envelope()
    {
        var payload = TransferCodec.Decode(TransferTestEnv.SbString(NestedSample()));
        Assert.Equal(PayloadKind.StreamerBot, payload.Kind);
        Assert.Contains("Follow alert", payload.Json);
        Assert.StartsWith("U0JBRR", TransferTestEnv.SbString("{}")); // the documented "U0JBRR…" start
    }

    [Fact]
    public void Maps_actions_sub_actions_triggers_commands_timers_and_queues()
    {
        var (bundle, report) = new StreamerBotImporter().Convert(NestedSample());
        Assert.Equal("Alerts pack", bundle.Header.Name);
        Assert.Equal("Tester", bundle.Header.Author);
        Assert.Contains("version 23", bundle.Header.Generator);

        var follow = bundle.Actions.Single(a => a.Name == "Follow alert");
        Assert.Equal(Guid.Parse(FollowId), follow.Id);
        Assert.Equal("Alerts", follow.Group);
        Assert.Equal("Alerts Q", follow.Queue);
        Assert.True(follow.Enabled);
        Assert.Equal("twitch.follow", Assert.Single(follow.Triggers).Type);

        var steps = follow.SubActions;
        // Unmapped trigger first (as a disabled placeholder), then the steps in order.
        Assert.Equal(["core.comment", "twitch.sendMessage", "core.delay", "core.setGlobal", "obs.setScene", "obs.setSourceVisibility",
            "audio.playSound", "core.if", "core.group", "core.comment"], steps.Select(s => s.Type));
        Assert.Equal("Thanks %user%", steps[1].Parameters["message"]);
        Assert.Equal("Bot", steps[1].Parameters["sendAs"]);
        Assert.Equal("1500", steps[2].Parameters["milliseconds"]);
        Assert.Equal("follows", steps[3].Parameters["name"]);
        Assert.Equal("true", steps[3].Parameters["persisted"]);
        Assert.Equal("Alert", steps[4].Parameters["scene"]);
        Assert.Equal("Visible", steps[5].Parameters["state"]);
        Assert.Equal("Confetti", steps[5].Parameters["source"]);
        Assert.Equal("50", steps[6].Parameters["volume"]);
        Assert.Equal("true", steps[6].Parameters["wait"]);
        var ifStep = steps[7];
        Assert.Equal("equals", ifStep.Parameters["operator"]);
        Assert.Equal("Helper", Assert.Single(ifStep.Branches["True"]).Parameters["action"]);
        Assert.Equal("not bob", Assert.Single(ifStep.Branches["False"]).Parameters["text"]);
        Assert.Equal("core.getGlobal", Assert.Single(steps[8].Branches["Steps"]).Type);

        var helper = bundle.Actions.Single(a => a.Name == "Helper");
        Assert.False(helper.Enabled); // contains code → imported disabled
        Assert.Equal(Code, Assert.Single(helper.SubActions).Parameters["code"]);
        Assert.Equal("code.csharp", helper.SubActions[0].Type);
        var triggers = helper.Triggers.ToDictionary(t => t.Type);
        var command = Assert.Single(bundle.Commands);
        Assert.Equal(command.Id.ToString(), triggers["command.triggered"].Settings["command"]);
        Assert.Equal("Promo", triggers["core.timer"].Settings["timer"]);
        Assert.Equal("100", triggers["twitch.cheer"].Settings["minBits"]);
        Assert.Equal("5", triggers["twitch.raid"].Settings["minViewers"]);
        Assert.Equal("Hydrate", triggers["twitch.rewardRedemption"].Settings["rewardTitle"]);
        Assert.Equal("2", triggers["twitch.sub"].Settings["tier"]);

        Assert.Equal(["!hello", "!hi"], command.Commands);
        Assert.Equal(CommandLocation.Exact, command.Location);
        Assert.Equal(10, command.GlobalCooldownSeconds);
        Assert.Equal(30, command.UserCooldownSeconds);
        var timer = Assert.Single(bundle.Timers);
        Assert.Equal((600, 5), (timer.IntervalSeconds, timer.MinChatLines));
        Assert.True(Assert.Single(bundle.Queues).Blocking);

        Assert.Contains(report.Mapped, l => l.StartsWith("Sub-action SendMessage → twitch.sendMessage", StringComparison.Ordinal));
        Assert.Contains(report.Warnings, w => w.Contains("C# code"));
    }

    [Fact]
    public void Unmapped_items_become_disabled_placeholders_and_are_reported()
    {
        var (bundle, report) = new StreamerBotImporter().Convert(NestedSample());
        var follow = bundle.Actions.Single(a => a.Name == "Follow alert");
        var placeholders = TransferReferences.Walk(follow.SubActions).Where(BundleImporter.IsPlaceholder).ToList();
        Assert.Equal(2, placeholders.Count);
        Assert.All(placeholders, p => Assert.False(p.Enabled));

        var weird = placeholders.Single(p => p.Parameters[BundleImporter.PlaceholderTypeKey] == "WeirdThing");
        Assert.Contains("\"foo\": \"bar\"", weird.Parameters["text"]);
        Assert.Contains("WeirdThing", weird.Parameters["text"]);
        Assert.Equal("MysteryTrigger", placeholders[0].Parameters[BundleImporter.PlaceholderTypeKey]);

        Assert.Equal(2, report.Placeholders.Count);
        Assert.Contains(report.Placeholders, p => p is { ItemKind: "trigger", OriginalType: "MysteryTrigger", ActionName: "Follow alert" });
        Assert.Contains(report.Placeholders, p => p.ItemKind == "sub-action" && p.Json.Contains("bar"));
    }

    [Fact]
    public void Flat_layout_with_parent_ids_numeric_codes_aliases_and_field_inference()
    {
        var group = Guid.NewGuid().ToString();
        var cond = Guid.NewGuid().ToString();
        var json = new JsonObject
        {
            ["data"] = new JsonObject
            {
                ["actions"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = Guid.NewGuid().ToString(), ["name"] = "Flat",
                        ["triggers"] = new JsonArray { new JsonObject { ["type"] = 101 } },
                        ["subActions"] = new JsonArray
                        {
                            new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["parentId"] = group, ["type"] = 4242, ["delay"] = 250, ["index"] = 1 },
                            new JsonObject { ["id"] = group, ["parentId"] = null, ["type"] = "Group", ["name"] = "G", ["index"] = 0 },
                            new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["parentId"] = group, ["type"] = 77777, ["text"] = "hi", ["useBot"] = false, ["index"] = 0 },
                            new JsonObject { ["id"] = cond, ["type"] = "IfElse", ["input"] = "a", ["operation"] = "StartsWith", ["value"] = "b", ["index"] = 1 },
                            new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["parentId"] = cond, ["type"] = "Break", ["branch"] = "false" },
                            new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["parentId"] = cond, ["type"] = "LogMessage", ["message"] = "yes", ["logLevel"] = "Warn" },
                            new JsonObject { ["$type"] = "Vendor.Actions.SendMessageSubAction, Vendor", ["text"] = "by clr name", ["index"] = 2 },
                        },
                    },
                },
            },
        }.ToJsonString();

        var table = StreamerBotMappingTable.CreateDefault()
            .AddAliasesJson("""{ "subActions": { "4242": "Delay" }, "triggers": { "101": "Follow" } }""");
        var (bundle, report) = new StreamerBotImporter(table).Convert(json);
        var action = Assert.Single(bundle.Actions);
        Assert.Equal("twitch.follow", Assert.Single(action.Triggers).Type);

        Assert.Equal(["core.group", "core.if", "twitch.sendMessage"], action.SubActions.Select(s => s.Type));
        var children = action.SubActions[0].Branches["Steps"];
        Assert.Equal(["twitch.sendMessage", "core.delay"], children.Select(s => s.Type)); // ordered by index
        Assert.Equal("Broadcaster", children[0].Parameters["sendAs"]);
        Assert.Equal("250", children[1].Parameters["milliseconds"]);
        Assert.Equal("starts with", action.SubActions[1].Parameters["operator"]);
        Assert.Equal("core.log", Assert.Single(action.SubActions[1].Branches["True"]).Type);
        Assert.Equal("Warning", action.SubActions[1].Branches["True"][0].Parameters["level"]);
        Assert.Equal("core.stop", Assert.Single(action.SubActions[1].Branches["False"]).Type);
        Assert.Equal("by clr name", action.SubActions[2].Parameters["message"]);
        Assert.Empty(report.Placeholders);
        Assert.Contains(report.Mapped, l => l.Contains("recognised by its fields"));
    }

    [Fact]
    public void Unknown_container_keeps_its_children_inside_a_disabled_group()
    {
        var json = """
            { "actions": [ { "name": "A", "triggers": [], "subActions": [
                { "type": 999, "subActions": [ { "type": "Comment", "text": "inner" } ] } ] } ] }
            """;
        var (bundle, report) = new StreamerBotImporter().Convert(json);
        var step = Assert.Single(Assert.Single(bundle.Actions).SubActions);
        Assert.Equal("core.group", step.Type);
        Assert.False(step.Enabled);
        Assert.Equal("inner", Assert.Single(step.Branches["Steps"]).Parameters["text"]);
        Assert.Equal("999", Assert.Single(report.Placeholders).OriginalType);
    }

    [Fact]
    public void Unknown_if_operator_is_disabled_and_flagged()
    {
        var json = """{ "actions": [ { "name": "A", "triggers": [], "subActions": [ { "type": "IfElse", "input": "x", "operation": 7, "value": "y" } ] } ] }""";
        var (bundle, report) = new StreamerBotImporter().Convert(json);
        var step = Assert.Single(Assert.Single(bundle.Actions).SubActions);
        Assert.Equal("core.if", step.Type);
        Assert.False(step.Enabled);
        Assert.Contains(report.Warnings, w => w.Contains("operator '7'"));
    }

    [Fact]
    public void Unreadable_code_becomes_a_placeholder()
    {
        var json = """{ "actions": [ { "name": "A", "triggers": [], "subActions": [ { "type": "ExecuteCode", "byteCode": "%%%" } ] } ] }""";
        var (bundle, report) = new StreamerBotImporter().Convert(json);
        Assert.True(BundleImporter.IsPlaceholder(Assert.Single(Assert.Single(bundle.Actions).SubActions)));
        Assert.Contains("could not be read", Assert.Single(bundle.Actions).SubActions[0].Parameters["text"]);
        Assert.Single(report.Placeholders);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "data": { "something": [] } }""")]
    public void Exports_without_items_are_rejected(string json) =>
        Assert.Throws<TransferFormatException>(() => new StreamerBotImporter().Convert(json));

    [Fact]
    public void Corrupt_streamer_bot_strings_are_rejected()
    {
        var good = TransferTestEnv.SbString(NestedSample());
        var bytes = Convert.FromBase64String(good);
        bytes[20] ^= 0xFF;
        bytes[21] ^= 0xFF;
        Assert.Throws<TransferFormatException>(() => _env.Service.Preview(Convert.ToBase64String(bytes)));
        Assert.Throws<TransferFormatException>(() => _env.Service.Preview(Convert.ToBase64String([.. "SBAE"u8.ToArray(), 1, 2, 3])));
    }

    [Fact]
    public async Task Full_pipeline_previews_and_imports_without_running_anything()
    {
        var plan = _env.Service.Preview(TransferTestEnv.SbString(NestedSample()));
        Assert.Equal(PayloadKind.StreamerBot, plan.Source);
        Assert.Equal(2, plan.Report.Placeholders.Count);
        var follow = plan.Items.Single(i => i.Name == "Follow alert");
        Assert.Equal(2, follow.PlaceholderCount);
        Assert.Contains(follow.Warnings, w => w.Contains("placeholders"));

        var changes = await _env.Service.ApplyAsync(plan);
        Assert.Equal(5, changes.Added); // 2 actions, 1 queue, 1 command, 1 timer
        var snapshot = _env.Profile.Snapshot;
        Assert.Equal(2, snapshot.Actions.Count);
        Assert.Equal("Alerts Q", snapshot.FindAction("Follow alert")!.Queue);
        Assert.NotNull(snapshot.FindQueue("Alerts Q"));
        Assert.Single(_env.Commands.Snapshot);
        Assert.Null(_env.Globals.GetValue("follows")); // the SetGlobal step was not executed
    }

    [Fact]
    public void Plain_json_paste_is_treated_as_streamer_bot()
    {
        var plan = _env.Service.Preview(NestedSample());
        Assert.Equal(PayloadKind.StreamerBot, plan.Source);
    }

    [Fact]
    public void Mapping_table_describes_its_rules()
    {
        var lines = StreamerBotMappingTable.Default.Describe().ToList();
        Assert.Contains("Sub-action ExecuteCode → code.csharp", lines);
        Assert.Contains("Trigger Command → command.triggered", lines);
        Assert.Throws<TransferFormatException>(() => StreamerBotMappingTable.CreateDefault().AddAliasesJson("nope"));
    }

    public void Dispose() => _env.Dispose();
}
