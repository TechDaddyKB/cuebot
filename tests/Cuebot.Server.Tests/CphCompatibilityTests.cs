using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Scripting.Compat;

namespace Cuebot.Server.Tests;

/// <summary>Streamer.bot-style C# (CPHInline classes and CPH calls) compiled and run against a real ActionEngine.</summary>
public sealed class CphCompatibilityTests : IAsyncLifetime
{
    private readonly TestBot _bot = new();

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync() => await _bot.DisposeAsync();

    private async Task<ActionRunRecord> RunAsync(ActionDefinition action, Dictionary<string, object?>? args = null)
    {
        await _bot.Engine.RunAsync(action, args);
        return await _bot.WaitForRunAsync(action.Name);
    }

    private const string TypicalScript =
        """
        using System;
        using System.Collections.Generic;
        using Streamer.bot.Plugin.Interface;
        using Streamer.bot.Plugin.Interface.Enums;
        using Streamer.bot.Plugin.Interface.Model;
        using Streamer.bot.Common.Events;

        public class CPHInline
        {
            private int _runs;

            public void Init()
            {
                CPH.RegisterCustomTrigger("Boss defeated", "bossDefeated", new[] { "Games", "Boss" });
            }

            public bool Execute()
            {
                _runs++;
                string user = args["user"].ToString();
                if (!CPH.TryGetArg("count", out int count)) count = 1;

                int total = CPH.GetGlobalVar<int>("totalHugs", true) + count;
                CPH.SetGlobalVar("totalHugs", total, true);
                CPH.SetTwitchUserVar(user, "hugs", count, true);
                CPH.SetArgument("total", total);
                CPH.SetArgument("runs", _runs);
                CPH.SetArgument("source", CPH.GetSource().ToString());
                CPH.SendMessage($"{user} gave {count} hugs ({total} total)");
                CPH.LogInfo("hugged");
                CPH.Wait(10);
                CPH.SetArgument("between", CPH.Between(5, 5));
                return true;
            }

            public void Dispose()
            {
            }
        }
        """;

    [Fact]
    public async Task Typical_streamerbot_script_compiles_and_runs_unchanged()
    {
        var action = _bot.AddAction("Hug", TypicalScript);

        var run = await RunAsync(action, new() { ["user"] = "Ann", ["count"] = "3", ["eventType"] = "twitch.chatMessage" });

        Assert.True(run.Success, run.Error);
        var args = _bot.Capture.ByAction["Hug"];
        Assert.Equal(3, args["total"]);
        Assert.Equal(1, args["runs"]);
        Assert.Equal("Twitch", args["source"]);
        Assert.Equal(5, args["between"]);
        Assert.Equal(3L, _bot.Globals.GetValue("totalHugs"));
        Assert.True(_bot.Globals.IsPersisted("totalHugs"));
        Assert.Equal(3L, _bot.UserVariables.Get("twitch", "ann", "hugs"));
        Assert.Contains("Ann gave 3 hugs (3 total)", _bot.Sender.Sent);
        Assert.Contains(_bot.Log.Recent(), e => e.Message == "hugged" && e.Source == "Hug");
        Assert.Contains(_bot.CodeTriggers.All, t => t.EventName == "bossDefeated" && t.Name == "Boss defeated");
    }

    [Fact]
    public async Task Instance_persists_between_runs_and_init_runs_once()
    {
        var action = _bot.AddAction("Hug", TypicalScript);
        await RunAsync(action, new() { ["user"] = "Ann" });
        _bot.Runs.Clear();
        var run = await RunAsync(action, new() { ["user"] = "Bob", ["count"] = 2 });

        Assert.True(run.Success, run.Error);
        Assert.Equal(2, _bot.Capture.ByAction["Hug"]["runs"]);
        Assert.Equal(3, _bot.Capture.ByAction["Hug"]["total"]);
        Assert.Single(_bot.CodeTriggers.All);
    }

    [Fact]
    public async Task Returning_false_stops_the_action()
    {
        var action = _bot.AddAction("Stop", "public class CPHInline { public bool Execute() { return false; } }");
        var run = await RunAsync(action);
        Assert.True(run.Success, run.Error);
        Assert.False(_bot.Capture.ByAction.ContainsKey("Stop"));
    }

    [Fact]
    public async Task RunAction_TriggerCodeEvent_and_action_state()
    {
        _bot.AddAction("Child");
        _bot.AddAction("On boss", null, new TriggerDefinition
        {
            Type = CodeTriggerRegistry.TriggerTypeId, Settings = { ["eventName"] = "bossDefeated" },
        });
        _bot.AddAction("Toggled");
        var parent = _bot.AddAction("Parent",
            """
            public class CPHInline
            {
                public bool Execute()
                {
                    CPH.SetArgument("childRan", CPH.RunAction("Child"));
                    CPH.SetArgument("missingRan", CPH.RunAction("Does not exist"));
                    CPH.SetArgument("exists", CPH.ActionExists("child"));
                    CPH.SetArgument("actionCount", CPH.GetActions().Count);
                    CPH.DisableAction("Toggled");
                    CPH.TriggerCodeEvent("bossDefeated");
                    return true;
                }
            }
            """);

        var run = await RunAsync(parent, new() { ["boss"] = "Dragon" });

        Assert.True(run.Success, run.Error);
        var args = _bot.Capture.ByAction["Parent"];
        Assert.Equal(true, args["childRan"]);
        Assert.Equal(false, args["missingRan"]);
        Assert.Equal(true, args["exists"]);
        Assert.Equal(4, args["actionCount"]);
        Assert.False(_bot.Profile.Snapshot.FindAction("Toggled")!.Enabled);
        await _bot.WaitForRunAsync("On boss");
        Assert.Equal("Dragon", _bot.Capture.ByAction["On boss"]["boss"]);
        Assert.Equal("bossDefeated", _bot.Capture.ByAction["On boss"]["eventName"]);
    }

    [Fact]
    public async Task Obs_and_tts_methods_go_through_script_functions()
    {
        var action = _bot.AddAction("Obs",
            """
            public class CPHInline
            {
                public bool Execute()
                {
                    CPH.SetArgument("scene", CPH.ObsGetCurrentScene());
                    CPH.ObsSetScene("BRB");
                    CPH.ObsHideSource("Main", "Webcam");
                    CPH.TtsSpeak("Narrator", "Be right back");
                    return true;
                }
            }
            """);

        var run = await RunAsync(action);

        Assert.True(run.Success, run.Error);
        Assert.Equal("Main", _bot.Capture.ByAction["Obs"]["scene"]);
        var calls = _bot.Functions.Calls.Select(c => c.Args).ToList();
        Assert.Contains(calls, c => c.Contains("SetCurrentProgramScene") && c.Contains("BRB"));
        Assert.Contains(calls, c => c.Contains("SetSceneItemEnabled") && c.Contains("\"sceneItemId\":7") && c.Contains("false"));
        Assert.Contains(calls, c => c.Contains("Be right back") && c.Contains("Narrator"));
    }

    [Fact]
    public async Task Groups_commands_and_user_variables()
    {
        _bot.Commands.Save(new CommandDefinition { Name = "Lurk", Commands = ["!lurk"] });
        var action = _bot.AddAction("Admin",
            """
            using Streamer.bot.Plugin.Interface.Enums;
            public class CPHInline
            {
                public bool Execute()
                {
                    CPH.AddGroup("VIPs");
                    CPH.AddUserIdToGroup("42", Platform.Twitch, "VIPs");
                    CPH.SetArgument("inGroup", CPH.UserIdInGroup("42", Platform.Twitch, "VIPs"));
                    CPH.SetArgument("groups", string.Join(",", CPH.GetGroups()));
                    CPH.DisableCommand("Lurk");
                    CPH.SetArgument("commands", CPH.GetCommands().Count);
                    CPH.SetTwitchUserVarById("42", "rank", "gold", true);
                    CPH.SetArgument("rank", CPH.GetTwitchUserVarById<string>("42", "rank"));
                    CPH.SetGlobalVar("list", new List<string> { "a", "b" });
                    CPH.SetArgument("second", CPH.GetGlobalVar<List<string>>("list")[1]);
                    return true;
                }
            }
            """);

        var run = await RunAsync(action);

        Assert.True(run.Success, run.Error);
        var args = _bot.Capture.ByAction["Admin"];
        Assert.Equal(true, args["inGroup"]);
        Assert.Equal("VIPs", args["groups"]);
        Assert.Equal(1, args["commands"]);
        Assert.Equal("gold", args["rank"]);
        Assert.Equal("b", args["second"]);
        Assert.False(_bot.Commands.Find("Lurk")!.Enabled);
    }

    [Fact]
    public async Task Unsupported_methods_throw_a_clear_NotSupportedException()
    {
        var action = _bot.AddAction("Unsupported",
            """
            public class CPHInline
            {
                public bool Execute()
                {
                    var info = CPH.TwitchGetUserInfoByLogin("ann");
                    CPH.SetArgument("name", info.UserName);
                    return true;
                }
            }
            """);

        var run = await RunAsync(action);

        Assert.False(run.Success);
        Assert.Contains("NotSupportedException", run.Error);
        Assert.Contains("CPH.TwitchGetUserInfoByLogin is not supported", run.Error);
    }

    [Fact]
    public async Task Compile_errors_and_runtime_errors_report_lines()
    {
        var broken = _bot.AddAction("Broken", "public class CPHInline\n{\n    public bool Execute()\n    {\n        return Nope();\n    }\n}");
        var run = await RunAsync(broken);
        Assert.False(run.Success);
        Assert.Contains("line 5", run.Error);

        var throws = _bot.AddAction("Throws", "public class CPHInline\n{\n    public bool Execute()\n    {\n        throw new InvalidOperationException(\"boom\");\n    }\n}");
        var failed = await RunAsync(throws);
        Assert.False(failed.Success);
        Assert.Contains("boom", failed.Error);
        Assert.Contains("line 5", failed.Error);
    }

    [Fact]
    public async Task CPH_is_available_in_plain_cuebot_scripts_next_to_Cb()
    {
        var action = _bot.AddAction("Plain",
            """
            CPH.SetGlobalVar("mode", "party", false);
            Args["mode"] = CPH.GetGlobalVar<string>("mode", false);
            args["viaAlias"] = CPH.TryGetArg("user", out string u) ? u : "none";
            await Cb.Log("done");
            return true;
            """);

        var run = await RunAsync(action, new() { ["user"] = "Zoe" });

        Assert.True(run.Success, run.Error);
        Assert.Equal("party", _bot.Capture.ByAction["Plain"]["mode"]);
        Assert.Equal("Zoe", _bot.Capture.ByAction["Plain"]["viaAlias"]);
    }

    [Fact]
    public async Task Websocket_methods_need_the_servers_functions()
    {
        var action = _bot.AddAction("Ws", "public class CPHInline { public bool Execute() { CPH.WebsocketBroadcastJson(\"{}\"); return true; } }");
        var run = await RunAsync(action);
        Assert.False(run.Success);
        Assert.Contains("websocket.broadcast", run.Error);
    }

    [Fact]
    public void Conversions()
    {
        Assert.Equal(5, CphConvert.To<int>("5"));
        Assert.Equal(5, CphConvert.To<int>(5L));
        Assert.True(CphConvert.To<bool>("true"));
        Assert.Equal(2.5, CphConvert.To<double>(2.5m));
        Assert.Equal("7", CphConvert.To<string>(7L));
        Assert.Equal(0, CphConvert.To<int>("not a number"));
        Assert.Equal(["x"], CphConvert.To<List<string>>("[\"x\"]"));
        Assert.Equal("[1,2]", CphConvert.ToStored(new[] { 1, 2 }));
        Assert.Equal(3, CphConvert.ToStored(3));
    }
}
