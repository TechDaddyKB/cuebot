using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Logging;
using Cuebot.Core.Transfer;
using Cuebot.Core.Transfer.StreamerBot;
using Cuebot.Core.Variables;
using Cuebot.Tests.Variables;

namespace Cuebot.Tests.Transfer;

/// <summary>
/// The numeric Streamer.bot type codes, one small hand-made fixture per code. The fixtures follow the shapes seen
/// in public exports (field names, value types and the all-zero GUID for "none"); no exported content is copied.
/// </summary>
public sealed class StreamerBotCodeTests
{
    private const string NoId = "00000000-0000-0000-0000-000000000000";
    private static readonly string OtherId = "5d4c3b2a-1111-4222-8333-944455556666";
    private static readonly string CodeId = "7e8f9a0b-1111-4222-8333-944455556666";

    /// <summary>Wraps sub-actions (older flat layout, "actions") and triggers in a one-action export.</summary>
    private static string Export(JsonArray subActions, JsonArray? triggers = null, JsonObject? data = null, int version = 10)
    {
        data ??= new JsonObject();
        var actions = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "1a2b3c4d-1111-4222-8333-944455556666", ["name"] = "Main", ["group"] = "Tests", ["enabled"] = true,
                ["queue"] = NoId, ["triggers"] = triggers ?? new JsonArray(), [version >= 23 ? "subActions" : "actions"] = subActions,
            },
            new JsonObject { ["id"] = OtherId, ["name"] = "Helper", ["enabled"] = true, ["triggers"] = new JsonArray(), ["actions"] = new JsonArray() },
        };
        if (data["actions"] is JsonArray extra)
        {
            data.Remove("actions");
            foreach (var a in extra.ToArray())
            {
                extra.Remove(a);
                actions.Add(a);
            }
        }
        data["actions"] = actions;
        return new JsonObject { ["meta"] = new JsonObject { ["name"] = "Fixture" }, ["data"] = data, ["version"] = version }.ToJsonString();
    }

    private static JsonObject Sub(int type, int index, params (string Key, JsonNode? Value)[] fields)
    {
        var o = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["weight"] = 0, ["type"] = type, ["group"] = null, ["enabled"] = true, ["index"] = index };
        foreach (var (k, v) in fields) o[k] = v;
        return o;
    }

    private static JsonObject Trig(int type, params (string Key, JsonNode? Value)[] fields)
    {
        var o = new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["type"] = type, ["enabled"] = true, ["exclusions"] = new JsonArray() };
        foreach (var (k, v) in fields) o[k] = v;
        return o;
    }

    private static (ActionDefinition Main, ImportReport Report, TransferBundle Bundle) Import(string json)
    {
        var (bundle, report) = new StreamerBotImporter().Convert(json);
        return (bundle.Actions.Single(a => a.Name == "Main"), report, bundle);
    }

    private static SubActionDefinition One(params JsonObject[] subs)
    {
        var (main, report, _) = Import(Export([.. subs]));
        Assert.Empty(report.Placeholders);
        return Assert.Single(main.SubActions);
    }

    [Theory]
    [InlineData(10, "twitch.sendMessage", "message", "Hello %user%")]
    [InlineData(1009, "core.comment", "text", "A note")]
    [InlineData(15, "twitch.setTitle", "title", "My title")]
    [InlineData(17, "twitch.createMarker", "description", "Marker")]
    public void Simple_text_codes(int code, string type, string key, string value)
    {
        var field = code switch { 10 => "text", 1009 => "value", 17 => "description", _ => "title" };
        var step = One(Sub(code, 0, (field, value), ("preferredAccount", 0), ("color", "#FF0000")));
        Assert.Equal(type, step.Type);
        Assert.Equal(value, step.Parameters[key]);
    }

    [Fact]
    public void Send_message_and_reply_pick_the_account_from_useBot()
    {
        var (main, _, _) = Import(Export(
        [
            Sub(10, 0, ("text", "a"), ("useBot", true), ("fallback", true)),
            Sub(10, 1, ("text", "b"), ("preferredAccount", 1)),
            Sub(560, 2, ("text", "c"), ("preferredAccount", 0), ("replyId", "%msgId%")),
        ]));
        Assert.Equal(["Bot", "Default", "Default"], main.SubActions.Select(s => s.Parameters["sendAs"]));
        Assert.Equal("twitch.replyToMessage", main.SubActions[2].Type);
        Assert.Equal("%msgId%", main.SubActions[2].Parameters["messageId"]);
    }

    [Fact]
    public void Delay_keeps_numbers_and_variables()
    {
        var (main, report, _) = Import(Export(
        [
            Sub(1002, 0, ("value", "500"), ("maxValue", "0"), ("random", false)),
            Sub(1002, 1, ("value", "%adLengthMs%"), ("maxValue", "0"), ("random", false)),
            Sub(1002, 2, ("value", "100"), ("maxValue", "900"), ("random", true)),
        ]));
        Assert.Equal(["500", "%adLengthMs%", "100"], main.SubActions.Select(s => s.Parameters["milliseconds"]));
        // A random delay waits between the delay and "To".
        Assert.Equal(("true", "900"), (main.SubActions[2].Parameters["random"], main.SubActions[2].Parameters["to"]));
        Assert.False(main.SubActions[0].Parameters.ContainsKey("random"));
        Assert.Empty(report.Warnings);
    }

    [Fact]
    public void Variables_arguments_and_globals()
    {
        var (main, report, _) = Import(Export(
        [
            Sub(123, 0, ("variableName", "greeting"), ("value", "hi %user%"), ("autoType", true)),
            Sub(121, 1, ("source", 0), ("variableName", "count"), ("persisted", true), ("destinationVariable", "c"), ("defaultValue", "0")),
            Sub(121, 2, ("source", 1), ("variableName", "bday"), ("persisted", true), ("destinationVariable", "bday"), ("defaultValue", null)),
            Sub(122, 3, ("destination", 0), ("variableName", "last"), ("persisted", false), ("source", 0), ("value", "rawInput")),
            Sub(122, 4, ("destination", 2), ("variableName", "bday"), ("persisted", true), ("source", 1), ("value", "no")),
            Sub(122, 5, ("destination", 0), ("variableName", "counter"), ("persisted", true), ("source", 2), ("value", "")),
            Sub(122, 6, ("destination", 0), ("variableName", "counter"), ("persisted", true), ("source", 3), ("value", "2")),
        ]));
        Assert.Empty(report.Placeholders);
        var s = main.SubActions;
        Assert.Equal(["core.setArgument", "core.getGlobal", "core.getUserVariable", "core.setGlobal", "core.setUserVariable", "core.setGlobal", "core.setGlobal"],
            s.Select(x => x.Type));
        Assert.Equal(("greeting", "hi %user%", "true"), (s[0].Parameters["name"], s[0].Parameters["value"], s[0].Parameters["autoType"]));
        Assert.Equal(("count", "c", "0"), (s[1].Parameters["name"], s[1].Parameters["destination"], s[1].Parameters["defaultValue"]));
        Assert.Equal("", s[2].Parameters["userId"]); // the user who ran the action
        Assert.Equal(("%rawInput%", "false", "Value"), (s[3].Parameters["value"], s[3].Parameters["persisted"], s[3].Parameters["mode"]));
        Assert.Equal(("%targetUserId%", "no"), (s[4].Parameters["userId"], s[4].Parameters["value"]));
        Assert.Equal("Increment", s[5].Parameters["mode"]);
        Assert.Equal(("Decrement", "2"), (s[6].Parameters["mode"], s[6].Parameters["value"]));
    }

    [Fact]
    public void Global_set_source_uses_the_1_0_numbering_from_format_23()
    {
        // Streamer.bot 1.0.7 describes source 0/1/2 as "Set ... to", "Increment ... by", "Decrement ... by".
        var (main, report, _) = Import(Export(
        [
            Sub(122, 0, ("destination", 0), ("variableName", "a"), ("persisted", true), ("source", 0), ("value", "rawInput")),
            Sub(122, 1, ("destination", 0), ("variableName", "a"), ("persisted", true), ("source", 1), ("value", "1")),
            Sub(122, 2, ("destination", 0), ("variableName", "a"), ("persisted", true), ("source", 2), ("value", "2")),
        ], version: 24));
        Assert.Empty(report.Placeholders);
        var s = main.SubActions;
        Assert.Equal(("Value", "rawInput"), (s[0].Parameters["mode"], s[0].Parameters["value"]));
        Assert.Equal(("Increment", "1"), (s[1].Parameters["mode"], s[1].Parameters["value"]));
        Assert.Equal(("Decrement", "2"), (s[2].Parameters["mode"], s[2].Parameters["value"]));
    }

    [Fact]
    public void Unknown_global_source_stays_a_placeholder()
    {
        var (_, report, _) = Import(Export([Sub(122, 0, ("destination", 0), ("variableName", "x"), ("source", 9), ("value", "1"))]));
        Assert.Equal("122", Assert.Single(report.Placeholders).OriginalType);
    }

    [Fact]
    public void Run_action_by_id_and_timer_state()
    {
        var timerId = Guid.NewGuid().ToString();
        var data = new JsonObject
        {
            ["timers"] = new JsonArray
            {
                new JsonObject { ["id"] = timerId, ["name"] = "Every minute", ["enabled"] = false, ["repeat"] = true, ["interval"] = 60,
                    ["randomInterval"] = false, ["upperInterval"] = 0, ["lines"] = 2, ["counter"] = 0 },
            },
        };
        var (main, report, bundle) = Import(Export(
        [
            Sub(4, 0, ("actionId", OtherId), ("runImmedately", false)),
            Sub(4, 1, ("actionId", NoId), ("runImmedately", true)),
            Sub(8, 2, ("timerId", timerId), ("state", 0)),
            Sub(8, 3, ("timerId", timerId), ("state", 1)),
        ], data: data));
        var s = main.SubActions;
        Assert.Equal(("Helper", "false"), (s[0].Parameters["action"], s[0].Parameters["wait"]));
        Assert.False(s[1].Enabled); // no action chosen
        Assert.Equal(("Every minute", "Enable"), (s[2].Parameters["timer"], s[2].Parameters["state"]));
        Assert.Equal("Disable", s[3].Parameters["state"]);
        var timer = Assert.Single(bundle.Timers);
        Assert.Equal((60, 60, 2, false), (timer.IntervalSeconds, timer.MaxIntervalSeconds, timer.MinChatLines, timer.Enabled));
        Assert.Empty(report.Placeholders);
    }

    [Fact]
    public void Old_if_else_runs_actions_then_continues_or_breaks()
    {
        var step = One(Sub(120, 0, ("variableName", "rawInput"), ("operation", 9), ("value", ""), ("actionId", OtherId), ("runImmediately", true),
            ("afterAction", 1), ("elseActionId", NoId), ("elseRunImmediately", true), ("elseAfterAction", 0), ("autoType", false)));
        Assert.Equal(("core.if", "%rawInput%", "is empty"), (step.Type, step.Parameters["left"], step.Parameters["operator"]));
        Assert.Equal(["core.runAction", "core.stop"], step.Branches["True"].Select(s => s.Type));
        Assert.Equal("Helper", step.Branches["True"][0].Parameters["action"]);
        Assert.Empty(step.Branches["False"]);
    }

    [Theory]
    [InlineData(0, "equals")]
    [InlineData(1, "not equals")]
    [InlineData(2, "contains")]
    [InlineData(3, "matches regex")]
    [InlineData(4, "less than")]
    [InlineData(5, "greater than")]
    [InlineData(7, "equals")]
    [InlineData(8, "not equals")]
    [InlineData(9, "is empty")]
    public void If_operator_codes(int code, string op)
    {
        var step = One(Sub(120, 0, ("input", "%x%"), ("operation", code), ("value", "y"), ("autoType", true)));
        Assert.Equal(op, step.Parameters["operator"]);
        Assert.True(step.Enabled);
    }

    [Fact]
    public void Does_not_exist_tests_whether_the_argument_is_set()
    {
        var step = One(Sub(120, 0, ("input", "targetUser"), ("operation", 6), ("value", null), ("autoType", true)));
        Assert.Equal("equals", step.Parameters["operator"]);
        var env = TestEnv.Create(new AppLog());
        string Resolve(string text, Dictionary<string, object?> args) => VariableResolver.Resolve(text, args, _ => null, env);
        var missing = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        var present = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["targetUser"] = "bob" };
        Assert.True(Conditions.Evaluate(Resolve(step.Parameters["left"], missing), "equals", Resolve(step.Parameters["right"], missing)));
        Assert.False(Conditions.Evaluate(Resolve(step.Parameters["left"], present), "equals", Resolve(step.Parameters["right"], present)));
    }

    [Fact]
    public void New_if_else_has_true_and_false_branch_children_and_groups_nest()
    {
        var json = Export(
        [
            new JsonObject
            {
                ["input"] = "%rawInput%", ["operation"] = 9, ["value"] = null, ["autoType"] = true, ["type"] = 120, ["enabled"] = true, ["index"] = 0,
                ["subActions"] = new JsonArray
                {
                    new JsonObject { ["random"] = false, ["type"] = 99901, ["index"] = 0, ["subActions"] = new JsonArray { new JsonObject { ["type"] = 124, ["index"] = 0 } } },
                    new JsonObject
                    {
                        ["random"] = false, ["type"] = 99902, ["index"] = 1,
                        ["subActions"] = new JsonArray { new JsonObject { ["quoteVariable"] = "%rawInput%", ["type"] = 1037, ["index"] = 0 } },
                    },
                },
            },
            new JsonObject
            {
                ["name"] = "Code", ["color"] = null, ["random"] = true, ["type"] = 99900, ["enabled"] = true, ["index"] = 1,
                ["subActions"] = new JsonArray
                {
                    new JsonObject { ["quoteType"] = 1, ["quoteId"] = "%input0%", ["type"] = 1000, ["index"] = 0 },
                    new JsonObject { ["quoteId"] = "%input0%", ["type"] = 1036, ["index"] = 1 },
                },
            },
        ], version: 23);
        var (main, report, _) = Import(json);
        Assert.Empty(report.Placeholders);
        var ifStep = main.SubActions[0];
        Assert.Equal("core.stop", Assert.Single(ifStep.Branches["True"]).Type);
        Assert.Equal("quotes.add", Assert.Single(ifStep.Branches["False"]).Type);
        var group = main.SubActions[1];
        Assert.Equal(("core.group", "Code", "true"), (group.Type, group.Parameters["name"], group.Parameters["random"]));
        Assert.Equal(["quotes.get", "quotes.delete"], group.Branches["Steps"].Select(s => s.Type));
        Assert.Equal("%input0%", group.Branches["Steps"][0].Parameters["id"]);
    }

    [Fact]
    public void Old_layout_groups_by_name_in_index_order()
    {
        var json = Export(
        [
            Sub(1009, 0, ("value", "top"), ("color", "")),
            Sub(123, 0, ("variableName", "b"), ("value", "2"), ("group", "Settings")),
            Sub(123, 1, ("variableName", "c"), ("value", "3"), ("group", "Settings")),
            Sub(1009, 2, ("value", "bottom"), ("color", "")),
        ]);
        var root = JsonNode.Parse(json)!;
        root["data"]!["actions"]![0]!["actionGroups"] = new JsonArray
        {
            new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["weight"] = 0, ["name"] = "Settings", ["random"] = false, ["index"] = 1 },
        };
        var (main, report, _) = Import(root.ToJsonString());
        Assert.Equal(["core.comment", "core.group", "core.comment"], main.SubActions.Select(s => s.Type));
        Assert.Equal("Settings", main.SubActions[1].Parameters["name"]);
        Assert.Equal(["b", "c"], main.SubActions[1].Branches["Steps"].Select(s => s.Parameters["name"]));
        Assert.DoesNotContain(report.Mapped, l => l.Contains("Sub-action Group")); // groups are not sub-actions there
    }

    [Fact]
    public void While_loops_over_its_children()
    {
        var json = Export(
        [
            new JsonObject
            {
                ["input"] = "%i%", ["operation"] = 3, ["value"] = "5", ["autoType"] = true, ["ignoreCase"] = false, ["type"] = 128, ["index"] = 0,
                ["subActions"] = new JsonArray { new JsonObject { ["variableName"] = "i", ["value"] = "$math(%i% + 1)$", ["type"] = 123, ["index"] = 0 } },
            },
        ], version: 23);
        var step = Assert.Single(Import(json).Main.SubActions);
        Assert.Equal(("core.while", "less than", "5"), (step.Type, step.Parameters["operator"], step.Parameters["right"]));
        Assert.Equal("core.setArgument", Assert.Single(step.Branches["Body"]).Type);
    }

    [Fact]
    public void Csharp_code_source_is_recovered_from_byteCode_or_code_and_never_enabled()
    {
        const string Source = "using System;\npublic class CPHInline { public bool Execute() { return true; } }";
        var (main, report, _) = Import(Export(
        [
            Sub(99999, 0, ("name", "Shared code"), ("description", ""), ("references", new JsonArray("mscorlib.dll")),
                ("byteCode", Convert.ToBase64String(Encoding.UTF8.GetBytes(Source))), ("precompile", false), ("delayStart", false),
                ("saveResultToVariable", false), ("saveToVariable", "")),
            Sub(99999, 1, ("name", ""), ("code", Source)),
        ]));
        Assert.False(main.Enabled);
        Assert.All(main.SubActions, s => Assert.Equal(("code.csharp", Source), (s.Type, s.Parameters["code"])));
        Assert.Equal("Shared code", main.SubActions[0].Parameters["name"]);
        Assert.Contains(report.Warnings, w => w.Contains("C# code"));
    }

    [Fact]
    public void Execute_method_calls_the_referenced_code_by_name()
    {
        var data = new JsonObject
        {
            ["actions"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = Guid.NewGuid().ToString(), ["name"] = "Library", ["triggers"] = new JsonArray(),
                    ["actions"] = new JsonArray { new JsonObject { ["id"] = CodeId, ["type"] = 99999, ["name"] = "Death counter", ["code"] = "public class CPHInline {}" } },
                },
            },
        };
        var (main, _, _) = Import(Export([Sub(99998, 0, ("executeCodeId", CodeId), ("method", "Increase"), ("runOnUiThread", false))], data: data));
        var step = Assert.Single(main.SubActions);
        Assert.Equal("code.csharp", step.Type);
        Assert.Contains("CPH.ExecuteMethod(\"Death counter\", \"Increase\")", step.Parameters["code"]);
        Assert.False(main.Enabled);
    }

    [Fact]
    public void Csharp_precompile_and_save_result_options_are_carried_over()
    {
        const string Source = "using System;\npublic class CPHInline { public bool Execute() { return true; } }";
        var (main, _, _) = Import(Export(
        [
            Sub(99999, 0, ("name", "Startup"), ("code", Source), ("references", new JsonArray("mscorlib.dll")), ("precompile", true),
                ("delayStart", true), ("saveResultToVariable", true), ("saveToVariable", "ok")),
            Sub(99998, 1, ("executeCodeId", CodeId), ("method", "Execute"), ("saveResultToVariable", true), ("saveToVariable", "methodOk")),
        ]));
        var code = main.SubActions[0].Parameters;
        Assert.Equal(("true", "true", "true", "ok"), (code["precompile"], code["delayedStart"], code["saveResult"], code["resultVariable"]));
        Assert.False(code.ContainsKey("references"));
        Assert.Equal(("true", "methodOk"), (main.SubActions[1].Parameters["saveResult"], main.SubActions[1].Parameters["resultVariable"]));
        Assert.False(main.Enabled);
    }

    [Fact]
    public void Csharp_references_keep_only_the_users_own_dlls_and_windows_forms_is_flagged()
    {
        const string Source = "using System;\nusing System.Windows.Forms;\npublic class CPHInline { public bool Execute() { return true; } }";
        var (main, report, _) = Import(Export(
        [
            Sub(99999, 0, ("code", Source), ("references", new JsonArray(
                @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\mscorlib.dll", @"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\System.Windows.Forms.dll",
                @".\Streamer.bot.Common.dll", @".\Newtonsoft.Json.dll", "System.dll", @".\dlls\StreamUP.dll", @"D:\libs\My.Helpers.dll"))),
        ]));
        var lines = main.SubActions[0].Parameters["references"].Split('\n');
        Assert.Equal(2, lines.Length);
        Assert.Equal(@"# StreamUP.dll (was .\dlls\StreamUP.dll in Streamer.bot): replace this line with the full path to your copy", lines[0]);
        Assert.Contains("My.Helpers.dll", lines[1]); // kept as is where D:\ is a full path, else as a comment
        Assert.Contains(report.Warnings, w => w.Contains("uses StreamUP.dll, My.Helpers.dll"));
        Assert.Contains(report.Warnings, w => w.Contains("Windows Forms"));
    }

    [Fact]
    public void Run_program_is_imported_with_the_action_disabled()
    {
        var (main, report, _) = Import(Export([Sub(6, 0, ("command", "python"), ("arguments", "x.py %rawInput%"), ("workingDir", null), ("envVars", "{}"), ("waitForExit", 30))]));
        var step = Assert.Single(main.SubActions);
        Assert.Equal(("core.runProgram", "python", "x.py %rawInput%", "true"),
            (step.Type, step.Parameters["path"], step.Parameters["arguments"], step.Parameters["wait"]));
        Assert.False(main.Enabled);
        Assert.Contains(report.Warnings, w => w.Contains("runs a program"));
    }

    [Fact]
    public void Sounds_logs_random_numbers_fetch_and_toasts()
    {
        var (main, report, _) = Import(Export(
        [
            Sub(1, 0, ("device", null), ("soundFile", "C:/a.wav"), ("finishBeforeContinuing", true), ("volume", 0.75), ("soundName", "")),
            Sub(7, 1, ("device", null), ("folder", "C:/sounds"), ("finishBeforeContinuing", false), ("volume", 1), ("recursive", true)),
            Sub(1020, 2, ("logLevel", 3), ("message", "careful")),
            Sub(1003, 3, ("randomType", 0), ("min", 1), ("max", 6)),
            Sub(1007, 4, ("url", "https://example.invalid/x"), ("variableName", "joke"), ("headers", "{\"Accept\":\"text/plain\"}"), ("parseAsJson", false)),
            Sub(1024, 5, ("toastId", ""), ("title", "T"), ("text", "Body"), ("attribution", ""), ("iconPath", "")),
        ]));
        Assert.Empty(report.Placeholders);
        var s = main.SubActions;
        Assert.Equal(("audio.playSound", "75", "true"), (s[0].Type, s[0].Parameters["volume"], s[0].Parameters["wait"]));
        Assert.Equal(("audio.playSoundFromFolder", "100", "true"), (s[1].Type, s[1].Parameters["volume"], s[1].Parameters["subfolders"]));
        Assert.Equal(("core.log", "Warning"), (s[2].Type, s[2].Parameters["level"]));
        Assert.Equal(("core.randomNumber", "randomNumber", "6"), (s[3].Type, s[3].Parameters["name"], s[3].Parameters["max"]));
        Assert.Equal(("core.httpRequest", "joke", "Accept: text/plain"), (s[4].Type, s[4].Parameters["variable"], s[4].Parameters["headers"]));
        Assert.Equal(("desktop.showNotification", "Body"), (s[5].Type, s[5].Parameters["message"]));
    }

    [Fact]
    public void Twitch_user_steps()
    {
        var (main, report, _) = Import(Export(
        [
            Sub(50, 0, ("sourceType", 1), ("variable", "")),
            Sub(50, 1, ("sourceType", 2), ("variable", "")),
            Sub(50, 2, ("userLogin", "%inputUser%")),
            Sub(51, 3, ("sourceType", 3), ("variable", "raider")),
            Sub(20, 4, ("userLogin", "user"), ("duration", 120), ("reason", "why")),
            Sub(29, 5, ("userLogin", "%targetUserName%")),
            Sub(550, 6, ("userIndex", "%i%")),
            Sub(16, 7, ("source", 0), ("game", "%rawInput%"), ("gameId", "")),
            Sub(50, 8, ("sourceType", 0), ("variable", "")),
        ]));
        Assert.Empty(report.Placeholders);
        var s = main.SubActions;
        Assert.Equal(["", "%input0%", "%inputUser%", "%raider%", "%user%", "%targetUserName%"], s.Take(6).Select(x => x.Parameters["user"]));
        Assert.Equal(["twitch.getUserInfo", "twitch.getUserInfo", "twitch.getUserInfo", "twitch.getFollowAge", "twitch.timeout", "twitch.sendShoutout",
            "twitch.getPresentUser", "twitch.setGame", "core.group"], s.Select(x => x.Type));
        Assert.Equal("%i%", s[6].Parameters["index"]);
        Assert.Equal(["twitch.getBroadcasterInfo", "twitch.getUserInfo"], s[8].Branches["Steps"].Select(x => x.Type));
    }

    [Fact]
    public void Obs_steps_use_the_default_connection()
    {
        var conn = Guid.NewGuid().ToString();
        var (main, report, _) = Import(Export(
        [
            Sub(30, 0, ("sceneName", "Main"), ("sourceName", "Cam"), ("state", 0), ("connectionId", NoId)),
            Sub(30, 1, ("sceneName", "Main"), ("sourceName", "Cam"), ("state", 1), ("connectionId", conn)),
            Sub(38, 2, ("sceneName", "Main"), ("sourceName", "Web"), ("url", "https://example.invalid"), ("connectionId", NoId)),
            Sub(39, 3, ("sceneName", "Main"), ("sourceName", "Txt"), ("text", "%count%"), ("connectionId", NoId)),
            Sub(47, 4, ("sceneName", "Main"), ("filterName", "Blur"), ("state", 2), ("connectionId", NoId)),
            Sub(46, 5, ("name", "GetStats"), ("prefix", "obsRaw"), ("raw", "{\"requestType\":\"GetStats\",\"requestData\":{}}"), ("resultsToArgs", true), ("connectionId", NoId)),
            Sub(35, 6, ("sceneName", "Main"), ("sourceName", "Cam"), ("filterName", "Color"), ("state", 0), ("connectionId", NoId)),
            Sub(323, 7, ("sceneName", "Main"), ("sourceName", "Img"), ("fileName", "C:/a.png"), ("connectionId", NoId)),
        ]));
        Assert.Empty(report.Placeholders);
        var s = main.SubActions;
        Assert.Equal(["obs.setSourceVisibility", "obs.setSourceVisibility", "obs.setBrowserUrl", "obs.setText", "obs.setFilterState", "obs.rawRequest",
            "obs.setFilterState", "obs.setImageFile"], s.Select(x => x.Type));
        Assert.All(s, x => Assert.Equal("", x.Parameters["connection"]));
        Assert.Equal(["Visible", "Hidden"], s.Take(2).Select(x => x.Parameters["state"]));
        Assert.Equal(("Web", "Txt"), (s[2].Parameters["input"], s[3].Parameters["input"]));
        Assert.Equal(("Main", "Blur", "Toggle"), (s[4].Parameters["source"], s[4].Parameters["filter"], s[4].Parameters["state"]));
        Assert.Equal(("GetStats", "{}", "obsRaw"), (s[5].Parameters["requestType"], s[5].Parameters["requestData"], s[5].Parameters["resultArgument"]));
        Assert.Equal(("Cam", "Enable"), (s[6].Parameters["source"], s[6].Parameters["state"]));
        Assert.Contains(report.Warnings, w => w.Contains(conn));
    }

    [Fact]
    public void Stream_deck_and_other_module_steps()
    {
        var (main, report, _) = Import(Export(
        [
            Sub(11001, 0, ("buttonId", "b1"), ("title", "Hi"), ("state", "")),
            Sub(11002, 1, ("buttonId", "b1"), ("image", "https://example.invalid/i.svg"), ("color", ""), ("state", "")),
            Sub(11008, 2, ("buttonId", "b1"), ("value", "7")),
            Sub(1013, 3, ("groupName", "All"), ("variableName", "commands"), ("includeAll", false)),
            Sub(15001, 4, ("userLogin", "%targetUser%"), ("prefix", "")),
            Sub(601, 5, ("alias", "Brian"), ("message", "hello"), ("badWordFilter", false)),
            Sub(4002, 6, ("title", "Live!"), ("broadcast", 0)),
            Sub(506, 7),
        ]));
        Assert.Empty(report.Placeholders);
        Assert.Equal(["streamDeck.setTitle", "streamDeck.setBackground", "streamDeck.setValue", "commands.getCommands", "pronouns.addForUser",
            "speech.speak", "youtube.setTitle", "twitch.getFollowerCount"], main.SubActions.Select(s => s.Type));
        Assert.Equal("Current", main.SubActions[0].Parameters["state"]);
        Assert.Equal("", main.SubActions[3].Parameters["group"]);
        // Speaker.bot Speak (601) is confirmed by Streamer.bot's UI text, so it is not flagged.
        Assert.DoesNotContain(report.Mapped, l => l.Contains("Speak") && l.Contains("inferred"));
    }

    [Fact]
    public void Unknown_codes_stay_placeholders()
    {
        var (main, report, _) = Import(Export([Sub(17001, 0, ("status", 0)), Sub(14401, 1, ("arLens", "Glasses"))], [Trig(702, ("variables", new JsonObject()))]));
        // 17001 is no Streamer.bot 1.0.7 code; 14401 (EpocCam Set AR Lens) is one Cuebot has no equivalent for.
        Assert.Equal(["17001", "14401", "702"], report.Placeholders.Select(p => p.OriginalType));
        Assert.All(main.SubActions, s => Assert.False(s.Enabled));
        Assert.Empty(main.Triggers);
    }

    [Fact]
    public void Trigger_codes()
    {
        var commandId = Guid.NewGuid().ToString();
        var data = new JsonObject
        {
            ["commands"] = new JsonArray
            {
                new JsonObject
                {
                    ["sources"] = new JsonArray(0, 1), ["permittedUsers"] = new JsonArray(), ["permittedGroups"] = new JsonArray("Moderators"),
                    ["id"] = commandId, ["name"] = "!so", ["enabled"] = false, ["include"] = true, ["mode"] = 0, ["command"] = "!so\r\n!shoutout",
                    ["location"] = 0, ["ignoreBotAccount"] = true, ["ignoreInternal"] = false, ["caseSensitive"] = false, ["globalCooldown"] = 5,
                    ["userCooldown"] = 0, ["group"] = "Chat", ["grantType"] = 0,
                },
            },
        };
        var (main, report, bundle) = Import(Export([], [
            Trig(401, ("commandId", commandId)),
            Trig(101), Trig(102, ("min", 100), ("max", -1)), Trig(103, ("tiers", 16)), Trig(104, ("tiers", 16), ("min", 3), ("max", -1)),
            Trig(105, ("tiers", 16), ("min", -1), ("max", -1), ("subType", 0), ("monthsGifted", 15)),
            Trig(106, ("tiers", 16), ("min", 5), ("max", -1), ("subType", 0)), Trig(107, ("min", 10), ("max", -1)),
            Trig(112, ("rewardId", "")), Trig(118, ("gameOnly", false), ("gameId", null), ("gameName", "Chess")),
            Trig(120, ("username", ""), ("isUserId", false)), Trig(133), Trig(139), Trig(186, ("minutes", new JsonArray(3))),
            Trig(472, ("min", -1), ("max", -1)), Trig(4003), Trig(4016, ("username", ""), ("isUserId", false)),
            Trig(14004, ("sceneName", "BRB"), ("obsId", null)), Trig(14005, ("obsId", null)),
            Trig(18001, ("eventName", "myEvent")), Trig(18002, ("name", "My event"), ("eventName", "other")),
        ], data));
        Assert.Empty(report.Placeholders);
        var t = main.Triggers;
        Assert.Equal(["command.triggered", "twitch.follow", "twitch.cheer", "twitch.sub", "twitch.resub", "twitch.subGift", "twitch.giftBomb", "twitch.raid",
            "twitch.rewardRedemption", "twitch.streamUpdate", "users.firstWords", "twitch.chatMessage", "twitch.adRun", "twitch.upcomingAd",
            "twitch.watchStreak", "youtube.chatMessage", "users.firstWords", "obs.sceneChanged", "obs.streamStarted", "custom.codeEvent", "custom.codeEvent"],
            t.Select(x => x.Type));
        var command = Assert.Single(bundle.Commands);
        Assert.Equal(command.Id.ToString(), t[0].Settings["command"]);
        Assert.Equal(("100", ""), (t[2].Settings["minBits"], t[2].Settings["maxBits"]));
        Assert.Equal("3", t[4].Settings["minMonths"]);
        Assert.Equal("5", t[6].Settings["minTotal"]);
        Assert.Equal("10", t[7].Settings["minViewers"]);
        Assert.Equal("Chess", t[9].Settings["categoryName"]);
        Assert.Equal(("Twitch", "YouTube"), (t[10].Settings["platform"], t[16].Settings["platform"]));
        Assert.Equal("3", t[13].Settings["minutes"]);
        Assert.Equal("BRB", t[17].Settings["scene"]);
        Assert.Equal(("myEvent", "other"), (t[19].Settings["eventName"], t[20].Settings["eventName"]));

        Assert.Equal(["!so", "!shoutout"], command.Commands);
        Assert.Equal(["Twitch:Message", "YouTube:Message"], command.Sources);
        Assert.Equal((ChatPermission.Broadcaster, "Moderators"), (command.MinimumRole, Assert.Single(command.AllowedGroups)));
        Assert.False(command.IgnoreInternal);
        Assert.Equal(5, command.GlobalCooldownSeconds);
    }

    [Fact]
    public void Deny_grant_type_and_newer_source_flags()
    {
        var data = new JsonObject
        {
            ["commands"] = new JsonArray
            {
                new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["name"] = "a", ["command"] = "!a", ["sources"] = 1, ["grantType"] = 1,
                    ["permittedUsers"] = new JsonArray("troll"), ["permittedGroups"] = new JsonArray() },
                new JsonObject { ["id"] = Guid.NewGuid().ToString(), ["name"] = "b", ["command"] = "!b", ["sources"] = new JsonArray(0, 6), ["grantType"] = 0 },
            },
        };
        var (_, report, bundle) = Import(Export([], data: data));
        var a = bundle.Commands.Single(c => c.Name == "a");
        Assert.Equal(["Twitch:Message"], a.Sources);
        Assert.Equal(("troll", ChatPermission.Everyone), (Assert.Single(a.DeniedUsers), a.MinimumRole));
        var b = bundle.Commands.Single(c => c.Name == "b");
        Assert.Empty(b.Sources);
        Assert.Contains(report.Warnings, w => w.Contains("code 6"));
    }
}
