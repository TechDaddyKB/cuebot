using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using static Cuebot.Core.Transfer.StreamerBot.SbJson;

namespace Cuebot.Core.Transfer.StreamerBot;

/// <summary>
/// Sub-action rules for the Streamer.bot 1.0.7 codes that StreamerBotRules.cs does not already cover. Every code and
/// the settings it saves by default were read from a real Streamer.bot 1.0.7 (each candidate code imported into it,
/// the codes it kept read back with their default settings, 2026-10-28). Which menu entry a code is was worked out
/// from those fields, the menu, the docs and the order Streamer.bot added sub-actions in; codes whose name is only
/// likely or unsure are in <see cref="TentativeSubActionCodes"/> and reported as "please check".
/// docs/streamerbot-import-codes.md lists every code; <see cref="NoEquivalentSubActions"/> the ones Cuebot cannot do.
/// <para>
/// Settings conventions: a state is an index into the documented options (0 Enabled/On, 1 Disabled/Off, 2 Toggle);
/// ids (all-zero GUID or null = not set) name Streamer.bot objects (rewards, decks, devices, instances) and are
/// passed on where Cuebot accepts an id, else the name saved next to them is used, else reported.
/// </para>
/// </summary>
public sealed partial class StreamerBotMappingTable
{
    /// <summary>Codes of the 1.0.7 switch's children: a case (with its values) and the default case.</summary>
    public const string SwitchCaseCode = "99903";
    public const string SwitchDefaultCode = "99904";

    /// <summary>
    /// Streamer.bot 1.0.7 sub-action codes whose menu name is only likely or unsure (a sibling with the same fields
    /// could be meant). Their rules are <see cref="SbRule.Tentative"/>.
    /// </summary>
    public static IReadOnlySet<string> TentativeSubActionCodes => TentativeCodes.All;

    // A holder class, so the set exists before CreateDefault (run by a static initializer in another file) reads it.
    private static class TentativeCodes
    {
        public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
        {
        "11", "12", "13", "14", "21", "23", "24", "25", "26", "27", "28", "31", "32", "33", "40", "41", "42", "43", "45", "49", "52",
        "125", "126", "129", "130", "200", "201", "208", "209", "210", "211", "212", "214", "215", "253", "254",
        "321", "323", "324", "325", "326", "402", "403", "404", "411", "412", "413", "414", "415",
        "500", "501", "502", "503", "504", "505", "507", "508", "509", "510", "512", "513",
        "520", "522", "523", "524", "525", "526", "527", "528", "530", "551", "552", "553", "554", "555", "556", "557", "558",
        "562", "563", "564", "566", "567", "569", "570", "575", "578", "579", "580", "601", "602", "603", "604", "605", "606",
        "1010", "1011", "1015", "1017", "1021", "1026", "1027", "1035", "1038",
        "4005", "4006", "4007", "4010", "5001", "5004", "5005", "5006", "5007", "5008", "5011", "6004",
        "11005", "11006", "11007", "12006", "12008", "14003", "14004", "14007", "14009", "14308", "14311",
        "14601", "14602", "14608", "14609", "14611", "14614", "14616", "14701", "19005", "19006", "21009", "21010", "30001",
        "35020", "35021", "35022", "35041", "35043", "35101", "35102", "35108", "35109", "35110", "35111", "35112",
        "41001", "41002", "41003", "41004", "41010", "41013", "41014", "41016", "41017", "44001", SwitchDefaultCode,
        };
    }

    /// <summary>Streamer.bot 1.0.7 sub-action codes with no Cuebot equivalent, and why. They import as disabled placeholders.</summary>
    public static readonly IReadOnlyDictionary<string, string> NoEquivalentSubActions = BuildNoEquivalentSubActions();

    private static Dictionary<string, string> BuildNoEquivalentSubActions()
    {
        var gaps = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["573"] = "Twitch Get Clip Download Urls: Cuebot has no clip download step",
            ["579"] = "Twitch Add Suspicious Status: Cuebot has no suspicious-user moderation step",
            ["580"] = "Twitch Remove Suspicious Status: Cuebot has no suspicious-user moderation step",
            ["14310"] = "Camera Hub Webcam Set White Balance Tint: Cuebot's direct (UVC) webcam control has no tint setting",
            ["14311"] = "Camera Hub Webcam Set Noise Reduction: Cuebot's direct (UVC) webcam control has no noise reduction setting",
            ["14314"] = "Camera Hub Webcam Set Dynamic Range: Cuebot's direct (UVC) webcam control has no dynamic range setting",
            ["14701"] = "Stream Deck Execute MCP Action: Cuebot's Stream Deck plugin has no MCP actions",
            ["20001"] = "Streamer.bot Remote Send Remote Trigger: Cuebot's remote link sends signals only",
            ["35104"] = "Kick Configure Rewards: Cuebot has no bulk Kick reward configuration",
            ["35109"] = "Kick Reset Reward Counter: Cuebot keeps no Kick reward counters",
            ["35110"] = "Kick Reset Reward User Counters: Cuebot keeps no Kick reward counters",
            ["35111"] = "Kick Set Group Enabled State: Cuebot has no Kick reward groups",
            ["35112"] = "Kick Set Group Paused State: Cuebot has no Kick reward groups",
            ["41012"] = "Meld Studio Set Text: Cuebot's Meld Studio steps cannot set a text layer",
            ["41016"] = "Meld Studio Set Media Layer Source: Cuebot's Meld Studio steps cannot change a media layer's file",
            ["44001"] = "Streamlabs Spin Wheel: Cuebot has no Streamlabs (cloud) wheel",
        };
        foreach (var (code, name) in new[] { ("14401", "Set AR Lens"), ("14402", "Set Camera"), ("14403", "Set Bitrate"), ("14404", "Set Flash Mode") })
            gaps[code] = $"Camera Hub EpocCam {name}: Cuebot has no EpocCam (phone camera) control";
        const string prompter = "Cuebot has no Camera Hub Prompter connection";
        foreach (var (code, name) in new[]
                 {
                     ("14601", "Fast Forward Chapter"), ("14602", "Rewind Chapter"), ("14603", "Set Chapter"), ("14604", "Set Font"),
                     ("14605", "Set Font Size"), ("14606", "Set Opacity"), ("14607", "Set Line Spacing"), ("14608", "Set Horizontal Margin"),
                     ("14609", "Set Vertical Margin"), ("14610", "Set Brightness"), ("14611", "Set Auto Scroll State"), ("14612", "Set Mode"),
                     ("14614", "Set Crosshair State"), ("14615", "Set Crosshair Image"), ("14616", "Get Selected Script"), ("14617", "Set Script"),
                 })
            gaps[code] = $"Camera Hub Prompter {name}: {prompter}";
        foreach (var (code, name) in new[]
                 {
                     ("37001", "Activate Lens"), ("37002", "Deactivate Lens"), ("37004", "Activate Lens by Name"), ("37011", "Activate Outfit"),
                     ("37012", "Reset Look"), ("37013", "Activate Outfit by Name"), ("37014", "Play Video Background"),
                     ("37015", "Play Video Background by Name"),
                 })
            gaps[code] = $"Streamfog {name}: Cuebot has no Streamfog connection";
        return gaps;
    }

    /// <summary>Cuebot parameters for one step, from its Streamer.bot settings; <c>label</c> names it in warnings.</summary>
    private delegate (string Key, string Value)[] SbParams(JsonObject source, ISbMapContext context, string label);

    private static void AddSubActions(StreamerBotMappingTable t)
    {
        // A rule per code: the label is Streamer.bot's sub-action name (for warnings), the rule name the label
        // without spaces. "Plain" steps carry no settings.
        void Custom(string code, string label, string target, SbSubActionMap map)
        {
            var name = RuleName(label);
            t.Add(new SbSubActionRule(name, target, [name, code], map));
        }
        void With(string code, string label, string target, SbParams parameters) =>
            Custom(code, label, target, (s, c) => Step(target, s, parameters(s, c, label)));
        void Plain(string code, string label, string target) => Custom(code, label, target, (s, _) => Step(target, s));
        void Obs(string code, string label, string target, SbParams? parameters = null) =>
            Custom(code, label, target, (s, c) =>
            {
                ObsConnection(s, c);
                return Step(target, s, [("connection", ""), .. parameters?.Invoke(s, c, label) ?? []]);
            });
        void Slobs(string code, string label, string target, SbParams? parameters = null) =>
            Custom(code, label, target, (s, c) =>
            {
                Connection(s, c, label, "Streamlabs Desktop");
                if (Str(s, "display") is { Length: > 0 } display && Normalize(display) != "horizontal")
                    c.Warn($"'{c.ActionName}': a {label} step targets the '{display}' display; Cuebot uses the main (horizontal) one.");
                return Step(target, s, parameters?.Invoke(s, c, label) ?? []);
            });
        void Meld(string code, string label, string target, SbParams? parameters = null) =>
            Custom(code, label, target, (s, c) =>
            {
                Connection(s, c, label, "Meld Studio");
                return Step(target, s, parameters?.Invoke(s, c, label) ?? []);
            });

        // ---------------------------------------------------------------- core
        With("2", "UDP Broadcast", "core.udpBroadcast", (s, _, _) => [("port", Text(Num(s, "port") ?? 0)), ("payload", Str(s, "payload") ?? "")]);
        With("3", "Write To File", "core.writeFile", (s, _, _) =>
            [("path", Str(s, "file") ?? ""), ("content", Str(s, "text") ?? ""), ("mode", Bool(s, "append") == true ? "Append" : "Overwrite")]);
        With("9", "Set Command State", "commands.setState", (s, c, l) =>
            [("command", IsRealId(Str(s, "commandId")) ? c.CommandRef(Str(s, "commandId")) : ""), ("state", EnableState(s, l))]);
        Plain("125", "Clear Non-Persisted Globals", "core.clearTemporaryGlobals");
        Plain("126", "Clear Non-Persisted User Globals", "core.clearTemporaryUserVariables");
        Custom("127", "Switch", "core.switch", MapSwitch);
        Plain("129", "Loop Break", "core.loopBreak");
        Plain("130", "Loop Continue", "core.loopContinue");
        With("1001", "Set Action Queue Pause State", "core.setActionQueuePause", (s, c, l) =>
            [("queue", QueueOrAll(s, c)), ("state", Pick(s, "state", l, "Pause", "Resume", "Toggle")), ("clear", Flag(Bool(s, "clear") ?? false))]);
        With("1004", "Set Action State", "core.setActionState", (s, c, l) => [("action", Action(s, c)), ("state", EnableState(s, l))]);
        With("1005", "Clear Action Queue", "core.clearActionQueue", (s, c, _) => [("queue", QueueOrAll(s, c))]);
        With("1006", "Read Lines From File", "core.readLines", (s, _, _) =>
            [("path", Str(s, "file") ?? ""), ("variable", Str(s, "variableName") is { Length: > 0 } v ? v : "line"), .. LineOptions(s)]);
        With("1008", "Pick Color", "core.pickColor", (s, c, l) =>
        {
            if (Bool(s, "random") == true) c.Warn($"'{c.ActionName}': a {l} step picks a random colour; Cuebot uses the colour it was given.");
            var argb = string.Concat(new[] { "alpha", "red", "green", "blue" }.Select(k => Math.Clamp(Int(s, k) ?? 0, 0, 255).ToString("X2", CultureInfo.InvariantCulture)));
            return [("color", "#" + argb), ("name", Str(s, "variableName") is { Length: > 0 } v ? v : "color")];
        });
        With("1010", "Set Command Group State", "commands.setGroupState", (s, _, l) => [("group", Str(s, "groupName") ?? ""), ("state", EnableState(s, l))]);
        With("1011", "Set Action Group State", "core.setActionGroupState", (s, _, l) => [("group", Str(s, "groupName") ?? ""), ("state", EnableState(s, l))]);
        Custom("1012", "Keyboard Press", "input.keyboardPress", (s, c) =>
        {
            // key/modifiers are not decoded: the step is imported disabled with an empty key, like the Key Press trigger.
            c.Warn($"'{c.ActionName}': a Keyboard Press step (key code {Str(s, "key") ?? "?"}, modifiers {Str(s, "modifiers") ?? "?"}) was imported "
                   + "disabled; set its keys in Cuebot and enable it.");
            var step = Step("input.keyboardPress", s, ("keys", ""));
            step.Enabled = false;
            return step;
        });
        With("1014", "Get Command State", "commands.getState", (s, c, _) => [("command", IsRealId(Str(s, "commandId")) ? c.CommandRef(Str(s, "commandId")) : "%commandName%")]);
        With("1015", "Get Command Group State", "commands.getGroupState", (s, _, _) => [("group", Str(s, "groupName") ?? "")]);
        With("1016", "Get Action State", "core.getActionState", (s, c, _) => [("action", Action(s, c)), ("name", "actionState")]);
        With("1017", "Get Action Group State", "core.getActionGroupState", (s, _, _) => [("group", Str(s, "groupName") ?? "")]);
        With("1018", "Set Voice Control Command", "voiceControl.setCommand", (s, c, l) =>
            [("command", Ref(s, c, l, "commandId", "voice control command")), ("phrase", Str(s, "command") ?? "")]);
        With("1019", "Set Voice Control Command State", "voiceControl.setCommandState", (s, c, l) =>
            [("command", Ref(s, c, l, "commandId", "voice control command")), ("state", Pick(s, "state", l, "Enabled", "Disabled", "Toggle"))]);
        With("1021", "Read Specific Line From File", "core.readSpecificLine", (s, _, _) =>
            [("path", Str(s, "file") ?? ""), ("variable", Str(s, "variableName") is { Length: > 0 } v ? v : "line"),
             ("lineNumber", Text(Math.Max(1, Num(s, "count", "lineNumber") ?? 1))), .. LineOptions(s)]);
        With("1022", "Set Action Queue Blocking State", "core.setActionQueueBlocking", (s, c, l) => [("queue", c.QueueRef(Str(s, "actionQueueId"))), ("state", EnableState(s, l))]);
        With("1023", "Get Input", "core.getInputDialog", (s, _, _) =>
            [("title", Str(s, "title") ?? ""), ("prompt", Str(s, "prompt") ?? ""), ("originalText", Str(s, "originalText") ?? ""),
             ("variable", Str(s, "outputVariable") is { Length: > 0 } v ? v : "inputResult"), ("autoType", Flag(Bool(s, "autoTypeOutput") ?? false))]);
        With("1025", "Set Voice Control Input", "voiceControl.setInput", (s, _, _) => [("device", Str(s, "audioDeviceName") is { Length: > 0 } d ? d : "Default")]);
        Plain("1026", "Reset Credits", "credits.reset");
        With("1027", "Reset First Words", "users.resetFirstWords", (_, _, _) => [("platform", "All")]);
        With("1028", "Set Sub-Action State", "core.setSubActionState", (s, c, l) =>
            [("action", Action(s, c)), ("subAction", Ref(s, c, l, "subActionId", "sub-action")), ("state", EnableState(s, l))]);
        With("1030", "Set File Tail State", "core.setWatcherState", (s, c, l) => [("watcher", Ref(s, c, l, "fileTailId", "file tail")), ("state", EnableState(s, l))]);
        With("1031", "Send Local Signal", "core.sendSignal", (s, _, _) =>
            [("name", Str(s, "signalName") ?? ""), ("includeArgs", Flag(Bool(s, "useArge", "useArgs") ?? false)), ("arguments", Lines(Get(s, "customArgs"))),
             ("queue", Flag(Bool(s, "queueSignal") ?? true))]);
        With("1032", "Wait For Local Signal", "core.waitForSignal", (s, _, _) =>
            [("name", Str(s, "signalName") ?? ""), ("timeoutMs", Text(Math.Max(0, Num(s, "timeout") ?? 0))), ("overwrite", Flag(Bool(s, "overwrite") ?? false))]);
        With("1033", "Set Trigger State", "core.setTriggerState", (s, c, l) =>
            [("action", Action(s, c)), ("trigger", Ref(s, c, l, "triggerId", "trigger")), ("state", EnableState(s, l))]);
        With("1034", "Custom Event Trigger", "core.triggerCustomEvent", (s, _, _) => [("eventName", Str(s, "eventName") ?? ""), ("useArgs", Flag(Bool(s, "useArgs") ?? false))]);
        Plain("1035", "Get Quote Count", "quotes.count");
        With("1038", "Set Sub-Action Weight", "core.setSubActionWeight", (s, c, l) =>
            [("action", Action(s, c)), ("subAction", Ref(s, c, l, "subActionId", "sub-action")), ("weight", Text(Math.Max(0, Num(s, "weight") ?? 1)))]);
        Custom("1039", "File Exists", "core.fileExists", (s, _) => Exists(s, Str(s, "file") ?? "", "File", "fileExists"));
        Custom("1040", "Folder Exists", "core.fileExists", (s, _) => Exists(s, Str(s, "folder") ?? "", "Folder", "folderExists"));
        With("9001", "MIDI Note On", "midi.noteOn", (s, c, l) =>
            [.. Midi(s, c, l), ("note", Str(s, "note") ?? "60"), ("velocity", Text(Num(s, "velocity") ?? 127)), ("duration", Text(Math.Max(0, Num(s, "duration") ?? 500))),
             ("sendNoteOff", Flag(Bool(s, "sendNoteOff") ?? false))]);
        With("9002", "MIDI Control Change", "midi.controlChange", (s, c, l) =>
            [.. Midi(s, c, l), ("controller", Text(Num(s, "controller") ?? 0)), ("value", Text(Num(s, "value") ?? 0))]);
        With("9003", "MIDI Generic Event", "midi.genericEvent", (s, c, l) =>
            [.. Midi(s, c, l), ("command", Pick(s, "command", l, "NoteOn", "NoteOff", "ControlChange", "PatchChange", "ChannelAfterTouch", "PitchWheelChange")),
             ("data1", Text(Num(s, "data1") ?? 0)), ("data2", Str(s, "data2") ?? "0")]);
        With("10001", "Clear Users From Group", "groups.clear", (s, c, l) => [("group", Ref(s, c, l, "groupId", "user group"))]);
        Plain("30001", "Stop All Sound Playback", "audio.stopSounds");
        With("30002", "Stop Sound Playback", "audio.stopSounds", (s, _, _) => [("tag", Str(s, "soundName") ?? "")]);

        // ---------------------------------------------------------------- Twitch
        With("11", "Twitch Update Redemption Status", "twitch.updateRedemption", (s, _, l) =>
            [("rewardId", "%rewardId%"), ("redemptionId", "%redemptionId%"), ("status", Pick(s, "status", l, "Fulfil", "Cancel"))]);
        With("12", "Twitch Subscriber Only", "twitch.subscriberMode", (s, _, l) => [("enabled", OnOff(s, "status", l))]);
        With("13", "Twitch Emote Only", "twitch.emoteOnly", (s, _, l) => [("enabled", OnOff(s, "status", l))]);
        With("14", "Twitch Slow Mode", "twitch.slowMode", (s, _, l) =>
            // Twitch allows 3–120 seconds; Streamer.bot's own exports use huge values to mean "as long as possible".
            [("enabled", OnOff(s, "status", l)), ("seconds", Text(Num(s, "duration") is { } d && d > 0 ? Math.Clamp(Math.Round(Math.Min(d, 1e6)), 3, 120) : 30))]);
        With("18", "Twitch Create Clip", "twitch.createClip", (s, c, l) =>
        {
            Ignored(s, c, l, "title", "Cuebot creates clips with Twitch's default title");
            return [("hasDelay", "false")];
        });
        With("19", "Twitch Run Commercial", "twitch.runAd", (s, _, _) =>
        {
            // The duration is the index of 30, 60, ... 180 seconds; a number of seconds is accepted too.
            var d = Num(s, "duration") ?? 0;
            var seconds = d < 6 ? (Math.Max(0, (int)d) + 1) * 30 : Math.Clamp(Math.Round(d / 30) * 30, 30, 180);
            return [("length", Text(seconds))];
        });
        With("22", "Twitch Add Random Users", "twitch.getRandomUsers", (s, c, l) =>
        {
            if (Arr(s, "groups") is { Count: > 0 } groups)
                c.Warn($"'{c.ActionName}': a {l} step only picks from the groups {string.Join(", ", groups.Select(AsString))}; Cuebot picks from everyone present.");
            if (Bool(s, "present") == false) c.Warn($"'{c.ActionName}': a {l} step picks from every known user; Cuebot picks from the present viewers.");
            return [("count", Text(Math.Max(1, Num(s, "count") ?? 1))), ("excludeBroadcaster", "true")];
        });
        With("23", "Twitch Send Announcement", "twitch.sendAnnouncement", Announcement);
        With("25", "Twitch Send Announcement (older layout)", "twitch.sendAnnouncement", Announcement);
        Plain("26", "Twitch Get Shield Mode Status", "twitch.getShieldMode");
        With("27", "Twitch Update Shield Mode Status", "twitch.setShieldMode", (s, _, l) => [("enabled", OnOff(s, "status", l))]);
        With("28", "Twitch Follow Mode", "twitch.followerMode", (s, _, l) => [("enabled", OnOff(s, "status", l)), ("minutes", Text(Math.Max(0, Num(s, "duration") ?? 0)))]);
        With("52", "Twitch Get Team Info for Target", "twitch.getTeamInfo", (s, c, _) => [("user", UserLogin(s, c))]);
        With("200", "Twitch Reward Set Enabled State", "twitch.setRewardEnabled", (s, _, l) => [Reward(s), ("enabled", OnOff(s, "state", l))]);
        With("201", "Twitch Reward Set Paused State", "twitch.setRewardPaused", (s, _, l) => [Reward(s), ("paused", OnOff(s, "state", l))]);
        With("202", "Twitch Reward Set Cost", "twitch.updateReward", (s, _, l) => [Reward(s), ("cost", Str(s, "cost") ?? ""), ("operator", CostOperator(s, l))]);
        With("203", "Twitch Configure Rewards", "twitch.configureRewards", (s, c, l) =>
        {
            // The per-reward states are not decoded; the step is imported empty and reported.
            if (Obj(s, "states") is { Count: > 0 } states)
                c.Warn($"'{c.ActionName}': a {l} step sets {states.Count} rewards; Cuebot could not read which state each gets. Fill in its lists.");
            return [("disableNotConfigured", Flag(Bool(s, "disableNotConfigured") ?? false))];
        });
        With("204", "Twitch Reward Set Global Cooldown", "twitch.setRewardCooldown", (s, c, l) =>
        {
            Ignored(s, c, l, "additive", "Cuebot sets the cooldown");
            return [Reward(s), ("seconds", Text(Math.Max(0, Num(s, "cooldown") ?? 0)))];
        });
        With("205", "Twitch Reward Set Title", "twitch.setRewardTitle", (s, _, _) => [Reward(s), ("title", Str(s, "title") ?? "")]);
        With("206", "Twitch Reward Set Prompt", "twitch.setRewardPrompt", (s, _, _) => [Reward(s), ("prompt", Str(s, "prompt") ?? "")]);
        With("207", "Twitch Reward Update", "twitch.updateReward", (s, _, l) =>
            [Reward(s), ("title", Str(s, "title") ?? ""), ("prompt", Str(s, "prompt") ?? ""), ("cost", Str(s, "cost") ?? ""), ("operator", CostOperator(s, l))]);
        With("208", "Twitch Get Reward Info", "twitch.getRewardInfo", (s, _, _) => [Reward(s)]);
        With("209", "Twitch Reset Reward Counter", "twitch.resetRewardCounter", (s, _, _) => [Reward(s)]);
        With("210", "Twitch Reset Reward User Counters", "twitch.resetRewardUserCounters", (s, _, _) => [Reward(s)]);
        With("211", "Twitch Reward Set Group Enabled State", "twitch.setRewardGroupEnabled", (s, _, l) =>
            [("group", Str(s, "groupName") ?? ""), ("state", Pick(s, "state", l, "Enabled", "Disabled", "Toggle"))]);
        With("212", "Twitch Reward Set Group Paused State", "twitch.setRewardGroupPaused", (s, _, l) =>
            [("group", Str(s, "groupName") ?? ""), ("state", Pick(s, "state", l, "Paused", "Unpaused", "Toggle"))]);
        With("213", "Twitch Reward Set Background Color", "twitch.setRewardColor", (s, _, _) =>
            [Reward(s), ("color", Str(s, "backgroundColor") is { Length: > 0 } color ? color : "#9147FF"), ("random", Flag(Bool(s, "random") ?? false))]);
        With("214", "Twitch Reward Set Max Per Stream", "twitch.setRewardMaxPerStream", (s, _, _) =>
            [Reward(s), ("max", Text(Math.Max(0, Num(s, "redeems") ?? 0))), ("additive", Flag(Bool(s, "additive") ?? false))]);
        With("215", "Twitch Reward Set Max per User per Stream", "twitch.setRewardMaxPerUser", (s, _, _) =>
            [Reward(s), ("max", Text(Math.Max(0, Num(s, "redeems") ?? 0))), ("additive", Flag(Bool(s, "additive") ?? false))]);
        Plain("251", "Twitch Clear Channel Tags", "twitch.clearTags");
        With("252", "Twitch Set Channel Tags", "twitch.setTags", (s, _, _) => [("tags", Str(s, "tags") ?? "")]);
        With("253", "Twitch Add Channel Tag", "twitch.addTag", (s, _, _) => [("tag", Str(s, "tag") ?? "")]);
        With("254", "Twitch Remove Channel Tag", "twitch.removeTag", (s, _, _) => [("tag", Str(s, "tag") ?? "")]);
        Plain("501", "Twitch Cancel Raid", "twitch.cancelRaid");
        With("502", "Twitch Add VIP", "twitch.addVip", (s, c, _) => [("user", UserLogin(s, c))]);
        With("503", "Twitch Remove VIP", "twitch.removeVip", (s, c, _) => [("user", UserLogin(s, c))]);
        With("504", "Twitch Add Moderator", "twitch.addModerator", (s, c, _) => [("user", UserLogin(s, c))]);
        With("505", "Twitch Remove Moderator", "twitch.removeModerator", (s, c, _) => [("user", UserLogin(s, c))]);
        Plain("507", "Twitch Get Subscriber Count", "twitch.getSubscriberCount");
        Plain("508", "Twitch Get Latest Follower", "twitch.getFollowerCount");
        Plain("509", "Twitch Get Latest Subscriber", "twitch.getLatestSubscriber");
        Plain("510", "Twitch Clear Chat Messages", "twitch.clearChat");
        With("512", "Twitch Unban User", "twitch.unban", (s, c, _) => [("user", UserLogin(s, c))]);
        With("513", "Twitch UnTimeout User", "twitch.untimeout", (s, c, _) => [("user", UserLogin(s, c))]);
        Plain("520", "Twitch Get Channel Guest Star Settings", "twitch.getGuestStarSettings");
        With("521", "Twitch Update Channel Guest Star Settings", "twitch.updateGuestStarSettings", (s, _, _) =>
            [("slotCount", Text(Math.Max(0, Num(s, "slotCount") ?? 0))), ("layout", GuestStarLayout(Str(s, "groupLayout"))),
             ("moderatorSendLive", Keep(Bool(s, "moderatorSendLiveEnabled"))), ("browserSourceAudio", Keep(Bool(s, "browserSourceAudioEnabled"))),
             ("regenerateBrowserSources", Flag(Bool(s, "regenerateBrowserSources") ?? false))]);
        Plain("522", "Twitch Get Guest Star Session", "twitch.getGuestStarSession");
        Plain("523", "Twitch Create Guest Star Session", "twitch.createGuestStarSession");
        Plain("524", "Twitch End Guest Star Session", "twitch.endGuestStarSession");
        Plain("525", "Twitch Get Guest Star Invites", "twitch.getGuestStarInvites");
        With("526", "Twitch Send Guest Star Invite", "twitch.sendGuestStarInvite", (s, c, _) => [("user", UserLogin(s, c))]);
        With("527", "Twitch Delete Guest Star Invite", "twitch.deleteGuestStarInvite", (s, c, _) => [("user", UserLogin(s, c))]);
        With("528", "Twitch Assign Guest Star Slot", "twitch.assignGuestStarSlot", (s, c, _) => [("user", UserLogin(s, c)), ("slot", Str(s, "slot") ?? "1")]);
        With("529", "Twitch Update Guest Star Slot", "twitch.updateGuestStarSlot", (s, _, _) =>
            [("slot", Str(s, "sourceSlot") ?? "1"), ("toSlot", Str(s, "destinationSlot") ?? "1")]);
        With("530", "Twitch Delete Guest Star Slot", "twitch.deleteGuestStarSlot", (s, c, _) => [("user", UserLogin(s, c)), ("slot", Str(s, "slot") ?? "1")]);
        With("531", "Twitch Update Guest Star Slot Settings", "twitch.updateGuestStarSlotSettings", (s, _, _) =>
            [("slot", Str(s, "slot") ?? "1"), ("audio", Keep(Bool(s, "audioEnabled"))), ("video", Keep(Bool(s, "videoEnabled"))), ("live", Keep(Bool(s, "live"))),
             ("volume", Text(Num(s, "volume") ?? -1))]);
        Plain("551", "Twitch Get Sub Counter Count", "twitch.getSubCounter");
        With("552", "Twitch Set Sub Counter Count", "twitch.setSubCounter", (s, _, _) => [("count", Str(s, "value") ?? "0")]);
        With("553", "Twitch Add User To Group", "groups.addUser", (s, c, _) => [.. UserGroup(s, c, "twitch"), ("create", "true")]);
        With("554", "Twitch Remove User from Group", "groups.removeUser", (s, c, _) => UserGroup(s, c, "twitch"));
        Custom("558", "Twitch User In Group", "groups.userInGroup", (s, c) => Step("groups.userInGroup", s, [.. UserGroup(s, c, "twitch"), ("argument", "inGroup")]));
        Plain("555", "Twitch Get Ad Schedule", "twitch.getAdSchedule");
        Plain("556", "Twitch Get Pre-roll Free Time", "twitch.getAdSchedule");
        Plain("557", "Twitch Snooze Ad", "twitch.snoozeAd");
        With("561", "Twitch Create Poll", "twitch.createPoll", (s, _, _) =>
            [("title", Str(s, "title") ?? ""), ("choices", Lines(Get(s, "choices"))), ("duration", Text(Math.Max(15, Num(s, "duration") ?? 60))),
             ("pointsPerVote", Text(Math.Max(0, Num(s, "channelPointsPerVote") ?? 0)))]);
        With("562", "Twitch Terminate Active Poll", "twitch.endPoll", (_, _, _) => [("mode", "Terminate")]);
        With("563", "Twitch Archive Active Poll", "twitch.endPoll", (_, _, _) => [("mode", "Archive")]);
        With("564", "Twitch Set Channel Branded Content", "twitch.setBrandedContent", (s, _, l) => [("enabled", OnOff(s, "status", l))]);
        With("565", "Twitch Warn User", "twitch.warn", (s, c, _) => [("user", UserLogin(s, c)), ("reason", Str(s, "reason") ?? "")]);
        Plain("566", "Twitch Get Active Poll", "twitch.getPoll");
        Plain("567", "Twitch Get Active Prediction", "twitch.getPrediction");
        With("568", "Twitch Create Prediction", "twitch.createPrediction", (s, _, _) =>
            [("title", Str(s, "title") ?? ""), ("outcomes", Lines(Get(s, "outcomes"))), ("window", Text(Math.Max(30, Num(s, "predictionWindow") ?? 120)))]);
        Plain("569", "Twitch Lock Active Prediction", "twitch.lockPrediction");
        Plain("570", "Twitch Cancel Active Prediction", "twitch.cancelPrediction");
        // Streamer.bot's winning index counts from 0; Cuebot numbers outcomes from 1.
        With("571", "Twitch Resolve Last Prediction", "twitch.resolvePrediction", (s, _, _) =>
            [("winner", Num(s, "winningIndex") is { } i ? Text(Math.Max(0, i) + 1) : Str(s, "winningIndex") ?? "1"), ("title", "")]);
        With("572", "Twitch Resolve Last Prediction By Title", "twitch.resolvePrediction", (s, _, _) => [("winner", Str(s, "outcomeTitle") ?? ""), ("title", "")]);
        Plain("575", "Twitch Get Pinned Chat Message", "twitch.getPinnedMessage");
        With("576", "Twitch Pin Chat Message", "twitch.pinMessage", (s, _, _) =>
            [("messageId", Str(s, "messageId") is { Length: > 0 } id ? id : "%messageId%"), ("duration", Text(Math.Max(0, Num(s, "duration") ?? 0)))]);
        With("577", "Twitch Update Pinned Chat Message Duration", "twitch.updatePinnedDuration", (s, _, _) =>
            [("messageId", ""), ("duration", Text(Math.Max(0, Num(s, "duration") ?? 60)))]);
        Plain("578", "Twitch Unpin Chat Message", "twitch.unpinMessage");

        // ---------------------------------------------------------------- OBS Studio
        Obs("31", "OBS Set Random Group Source Visible", "obs.randomGroupSource", (s, _, _) => [("group", Str(s, "groupName") ?? ""), ("resultArgument", "obsSourceName")]);
        Obs("32", "OBS Hide Group's Sources", "obs.hideGroupSources", (s, _, _) => [("group", Str(s, "groupName") ?? "")]);
        // A real export ends the stream with state 0 ("_Raid Out" in the official starter pack): 0 Stop, 1 Start.
        Obs("33", "OBS Streaming", "obs.streaming", (s, _, l) => [("mode", Pick(s, "state", l, "Stop", "Start", "Toggle"))]);
        Obs("36", "OBS Flip Source", "obs.flipSource", (s, _, l) =>
            [("scene", Str(s, "sceneName") ?? ""), ("source", Str(s, "sourceName") ?? ""), ("axis", Pick(s, "flipMode", l, "Horizontal", "Vertical"))]);
        Obs("37", "OBS Rotate Source", "obs.rotateSource", (s, _, _) =>
            [("scene", Str(s, "sceneName") ?? ""), ("source", Str(s, "sourceName") ?? ""), ("degrees", Str(s, "degrees") ?? "0"), ("relative", Flag(Bool(s, "additive") ?? false))]);
        Obs("40", "OBS Set Source Mute State", "obs.setInputMute", (s, _, l) => [("input", Str(s, "sourceName") ?? ""), ("state", Pick(s, "state", l, "Mute", "Unmute", "Toggle"))]);
        Obs("41", "OBS Hide Source's Filters", "obs.setAllFiltersState", (s, _, _) => [("source", Str(s, "sourceName") ?? ""), ("state", "Disable")]);
        Obs("42", "OBS Set State of a Random Filter", "obs.setRandomFilterState", (s, _, l) =>
            [("source", Str(s, "sourceName") ?? ""), ("state", Pick(s, "state", l, "Enable", "Disable", "Toggle")), ("resultArgument", "obsFilterName")]);
        Obs("43", "OBS Get Current Scene", "obs.getCurrentScene", (_, _, _) => [("resultArgument", "currentScene")]);
        Obs("44", "OBS Set Source Audio Track State", "obs.setAudioTrack", (s, _, l) =>
            [("input", Str(s, "sourceName") ?? ""), ("track", Text(Math.Clamp(Int(s, "track") ?? 0, 0, 5) + 1)), ("state", Pick(s, "state", l, "Enable", "Disable", "Toggle"))]);
        Obs("45", "OBS Recording", "obs.recording", (s, _, l) => [("mode", Pick(s, "state", l, "Stop", "Start", "Pause", "Resume"))]);
        Obs("48", "OBS Take Screenshot", "obs.screenshot", (s, _, _) =>
            [("source", Str(s, "sourceName") is { Length: > 0 } source ? source : Str(s, "sceneName") ?? ""), ("file", Str(s, "path") ?? ""),
             ("quality", Text(Num(s, "quality") is { } q && q > 0 ? q : -1)), ("resultArgument", "screenshotFile")]);
        Obs("49", "OBS Set Media State", "obs.mediaControl", (s, _, l) =>
            [("input", Str(s, "sourceName") ?? ""), ("command", Pick(s, "state", l, "Play", "Pause", "Restart", "Stop", "Next", "Previous"))]);
        Obs("321", "OBS Set Replay Buffer State", "obs.replayBuffer", (s, _, l) => [("mode", Pick(s, "state", l, "Start", "Stop", "Save"))]);
        Obs("324", "OBS Get Scene Item Properties", "obs.getSceneItemProperties", (s, _, _) => [("scene", Str(s, "sceneName") ?? ""), ("source", Str(s, "sourceName") ?? "")]);
        Obs("325", "OBS Hide Scene's Sources", "obs.hideSceneSources", (s, _, _) => [("scene", Str(s, "sceneName") ?? "")]);
        Obs("326", "OBS Set Random Scene Source Visible", "obs.randomSceneSource", (s, _, _) => [("scene", Str(s, "sceneName") ?? ""), ("resultArgument", "obsSourceName")]);
        Obs("327", "OBS Set Color Source Color", "obs.setColor", (s, c, l) =>
        {
            Ignored(s, c, l, "random", "Cuebot sets the colour it was given");
            return [("input", Str(s, "sourceName") ?? ""), ("color", Str(s, "color") is { Length: > 0 } color ? color : "#FFFFFF")];
        });
        Obs("328", "OBS Get Status", "obs.getStatus");

        // ---------------------------------------------------------------- Streamlabs Desktop (mirrors the OBS codes + 371)
        Slobs("401", "Streamlabs Desktop Set Source Visibility State", "slobs.setSourceVisibility", (s, _, l) =>
            [("scene", Str(s, "sceneName") ?? ""), ("source", Str(s, "sourceName") ?? ""), ("state", Pick(s, "state", l, "Visible", "Hidden", "Toggle"))]);
        Slobs("402", "Streamlabs Desktop Set Random Group Source Visible", "slobs.randomGroupSource", (s, _, _) =>
            [("scene", Str(s, "sceneName") ?? ""), ("group", Str(s, "groupName") ?? ""), ("resultArgument", "randomSource")]);
        Slobs("403", "Streamlabs Desktop Hide Group's Sources", "slobs.hideGroupSources", (s, _, _) => [("scene", Str(s, "sceneName") ?? ""), ("group", Str(s, "groupName") ?? "")]);
        Slobs("404", "Streamlabs Desktop Streaming", "slobs.streaming", (s, _, l) => [("mode", Pick(s, "state", l, "Stop", "Start", "Toggle"))]);
        Slobs("405", "Streamlabs Desktop Set Active Scene", "slobs.setScene", (s, _, _) => [("scene", Str(s, "sceneName") ?? "")]);
        Slobs("406", "Streamlabs Desktop Set Source Filter State", "slobs.setFilterState", (s, _, l) =>
            [("source", Str(s, "sourceName") ?? ""), ("filter", Str(s, "filterName") ?? ""), ("state", Pick(s, "state", l, "Enable", "Disable"))]);
        Slobs("407", "Streamlabs Desktop Flip Source", "slobs.flipSource", (s, _, l) =>
            [("scene", Str(s, "sceneName") ?? ""), ("source", Str(s, "sourceName") ?? ""), ("axis", Pick(s, "flipMode", l, "Horizontal", "Vertical"))]);
        Slobs("408", "Streamlabs Desktop Rotate Source", "slobs.rotateSource", (s, c, l) =>
        {
            Ignored(s, c, l, "additive", "Cuebot sets the rotation");
            return [("scene", Str(s, "sceneName") ?? ""), ("source", Str(s, "sourceName") ?? ""), ("degrees", Str(s, "degrees") ?? "0")];
        });
        Slobs("409", "Streamlabs Desktop Set Browser Source URL", "slobs.setBrowserUrl", (s, _, _) => [("source", Str(s, "sourceName") ?? ""), ("url", Str(s, "url") ?? "")]);
        Slobs("410", "Streamlabs Desktop Set GDI Text", "slobs.setText", (s, _, _) => [("source", Str(s, "sourceName") ?? ""), ("text", Str(s, "text") ?? "")]);
        Slobs("411", "Streamlabs Desktop Set Source Mute State", "slobs.setSourceMute", (s, _, l) =>
            [("source", Str(s, "sourceName") ?? ""), ("state", Pick(s, "state", l, "Mute", "Unmute", "Toggle"))]);
        Slobs("412", "Streamlabs Desktop Hide Source's Filters", "slobs.hideSourceFilters", (s, _, _) => [("scene", Str(s, "sceneName") ?? ""), ("source", Str(s, "sourceName") ?? "")]);
        Slobs("413", "Streamlabs Desktop Set State of a Random Filter", "slobs.setRandomFilterState", (s, _, l) =>
            [("scene", Str(s, "sceneName") ?? ""), ("source", Str(s, "sourceName") ?? ""), ("state", Pick(s, "state", l, "Visible", "Hidden", "Toggle")),
             ("resultArgument", "slobsFilterName")]);
        Slobs("414", "Streamlabs Desktop Get Current Scene", "slobs.getCurrentScene", (_, _, _) => [("resultArgument", "currentScene")]);
        Slobs("415", "Streamlabs Desktop Recording", "slobs.recording", (s, _, l) => [("mode", Pick(s, "state", l, "Stop", "Start", "Toggle"))]);
        Slobs("417", "Streamlabs Desktop Set Scene Filter State", "slobs.setSceneFilterState", (s, _, l) =>
            [("scene", Str(s, "sceneName") ?? ""), ("filter", Str(s, "filterName") ?? ""), ("state", Pick(s, "state", l, "Visible", "Hidden", "Toggle"))]);

        // ---------------------------------------------------------------- Speaker.bot (Cuebot speaks itself)
        With("602", "Speaker.bot Speak (WebSocket)", "speech.speak", (s, c, l) =>
        {
            Ignored(s, c, l, "delay", "Cuebot speaks at once");
            return [("alias", Str(s, "alias") ?? ""), ("text", Str(s, "message", "text") ?? ""), ("badWordFilter", Flag(Bool(s, "badWordFilter") ?? false)),
                    ("silent", Flag(Bool(s, "silent") ?? false))];
        });
        With("603", "Speaker.bot Set Enabled State", "speech.setEnabled", (s, _, l) => [("mode", Pick(s, "state", l, "Enable", "Disable", "Toggle"))]);
        With("604", "Speaker.bot Set Paused State", "speech.pause", (s, _, l) => [("mode", Pick(s, "state", l, "Pause", "Resume", "Toggle"))]);
        Plain("605", "Speaker.bot Stop Speaking", "speech.skip");
        Plain("606", "Speaker.bot Clear Pending", "speech.clearQueue");
        With("607", "Speaker.bot Activate VoiceGate Profile", "speech.activateVoiceGate", (s, _, _) => [("profile", Str(s, "profileName") ?? "")]);
        With("701", "PolyPop Trigger Alert", "polypop.triggerAlert", (s, _, _) => [("alertName", Str(s, "alertName") ?? ""), ("variables", Lines(Get(s, "variables")))]);

        // ---------------------------------------------------------------- YouTube
        With("4003", "YouTube Set Description", "youtube.setDescription", (s, _, _) => [("description", Str(s, "description") ?? ""), ("broadcastId", "")]);
        With("4004", "YouTube Set Title and Description", "youtube.setTitleAndDescription", (s, _, _) =>
            [("title", Str(s, "title") ?? ""), ("description", Str(s, "description") ?? ""), ("broadcastId", "")]);
        With("4005", "YouTube Add User To Group", "groups.addUser", (s, c, _) => [.. UserGroup(s, c, "youtube"), ("create", "true")]);
        With("4006", "YouTube Remove User from Group", "groups.removeUser", (s, c, _) => UserGroup(s, c, "youtube"));
        With("4007", "YouTube User In Group", "groups.userInGroup", (s, c, _) => [.. UserGroup(s, c, "youtube"), ("argument", "inGroup")]);
        With("4008", "YouTube Ban User", "youtube.banUser", (s, _, _) => [("user", Str(s, "userId") is { Length: > 0 } u ? u : "%userId%"), ("broadcastId", "")]);
        With("4009", "YouTube Timeout User", "youtube.timeoutUser", (s, _, _) =>
            [("user", Str(s, "userId") is { Length: > 0 } u ? u : "%userId%"), ("duration", Text(Math.Max(1, Num(s, "duration") ?? 300))), ("broadcastId", "")]);
        Plain("4010", "YouTube Add Broadcaster Information", "youtube.addBroadcasterInfo");
        With("4011", "YouTube Create Poll", "youtube.createPoll", (s, _, _) => [("question", Str(s, "question") ?? ""), ("options", Lines(Get(s, "options"))), ("broadcastId", "")]);
        With("4012", "YouTube End Poll", "youtube.endPoll", (_, _, _) => [("pollMessageId", "")]);

        // ---------------------------------------------------------------- integrations
        Plain("5001", "VoiceMod Get Current Voice", "voicemod.getCurrentVoice");
        With("5002", "VoiceMod Select Voice", "voicemod.selectVoice", (s, _, _) => [("voice", NameOr(s, "voiceName", "voiceId"))]);
        With("5003", "VoiceMod Select Random Voice", "voicemod.selectRandomVoice", (s, _, l) =>
            [("mode", Pick(s, "mode", l, "AllVoices", "FreeVoices", "FavoriteVoices", "CustomVoices"))]);
        With("5004", "VoiceMod Set Voice Changer State", "voicemod.setVoiceChanger", VoiceModState);
        With("5005", "VoiceMod Set Background Effect State", "voicemod.setBackgroundEffects", VoiceModState);
        With("5006", "VoiceMod Set Hear My Voice State", "voicemod.setHearMyself", VoiceModState);
        With("5007", "VoiceMod Set Censor State", "voicemod.setCensorBeep", (s, _, l) => [("state", Pick(s, "state", l, "On", "Off"))]);
        With("5008", "VoiceMod Set Mute State", "voicemod.setMute", VoiceModState);
        With("5009", "VoiceMod Select Voice By Id", "voicemod.selectVoice", (s, _, _) => [("voice", Str(s, "voiceId") ?? "")]);
        With("5010", "VoiceMod Play Sound", "voicemod.playSound", (s, _, _) => [("sound", NameOr(s, "soundName", "soundId"))]);
        Plain("5011", "VoiceMod Stop All Sounds", "voicemod.stopAllSounds");
        With("5012", "VoiceMod Select Voice by Name", "voicemod.selectVoice", (s, _, _) => [("voice", Str(s, "voiceName") ?? "")]);
        With("6001", "Lumia Stream Send Command", "lumia.sendCommand", (s, _, _) => [("command", Str(s, "command") ?? "")]);
        With("6002", "Lumia Stream Set Color", "lumia.setColor", (s, _, _) =>
            [("color", Str(s, "color") is { Length: > 0 } color ? color : "#FF4076"), ("brightness", Str(s, "brightness") ?? ""), ("duration", Str(s, "duration") ?? ""),
             ("transition", Str(s, "transition") ?? ""), ("default", Flag(Bool(s, "default") ?? false)), ("skipQueue", Flag(Bool(s, "skipQueue") ?? false)),
             ("lights", Lines(Get(s, "lights")).Replace('\n', ','))]);
        Plain("6004", "Lumia Stream Set To Default", "lumia.setToDefault");
        With("7001", "Discord Basic Webhook", "discord.basicWebhook", (s, c, l) =>
        {
            Ignored(s, c, l, "image", "Cuebot's basic webhook posts text only");
            return [("name", Str(s, "name") ?? ""), ("webhookUrl", Str(s, "webhookUrl") ?? ""), ("content", Str(s, "content") ?? ""),
                    ("username", Str(s, "username") ?? ""), ("avatarUrl", Str(s, "avatar") ?? ""), ("textToSpeech", Flag(Bool(s, "textToSpeech") ?? false))];
        });
        With("8001", "IFTTT Notification (Basic)", "ifttt.notificationBasic", (s, _, _) => [("eventName", Str(s, "eventName") ?? ""), ("text", Str(s, "text") ?? "")]);
        With("8002", "IFTTT Notification (Advanced)", "ifttt.notificationAdvanced", (s, _, _) =>
            [("eventName", Str(s, "eventName") ?? ""), ("arguments", Lines(Get(s, "args")))]);
        With("12001", "VTube Studio Load Model", "vtubestudio.loadModel", (s, _, _) => [("model", NameOr(s, "modelName", "modelId"))]);
        With("12002", "VTube Studio Load Model by Name", "vtubestudio.loadModel", (s, _, _) => [("model", Str(s, "modelName") ?? "")]);
        With("12003", "VTube Studio Trigger Hotkey", "vtubestudio.triggerHotkey", (s, _, _) => [("hotkey", NameOr(s, "hotkeyName", "hotkeyId"))]);
        With("12004", "VTube Studio Trigger Hotkey by Name", "vtubestudio.triggerHotkey", (s, _, _) => [("hotkey", Str(s, "hotkeyName") ?? "")]);
        With("12005", "VTube Studio Move Model", "vtubestudio.moveModel", (s, _, _) =>
            [("time", Str(s, "seconds") ?? "0.5"), ("relative", Flag(Bool(s, "relative") ?? false)), ("positionX", Str(s, "posX") ?? ""), ("positionY", Str(s, "posY") ?? ""),
             ("rotation", Str(s, "rotation") ?? ""), ("size", Str(s, "size") ?? "")]);
        Plain("12006", "VTube Studio Get Current Model", "vtubestudio.getCurrentModel");
        With("12007", "VTube Studio Color Tint", "vtubestudio.colorTint", (s, c, l) =>
        {
            Ignored(s, c, l, "random", "Cuebot tints with the colour it was given");
            Ignored(s, c, l, "mixWithSceneLighting", "Cuebot mixes fully (1)");
            // filterType/filterValues pick the art meshes; without them every mesh is tinted.
            var values = Lines(Get(s, "filterValues")).Replace('\n', ',');
            var byTag = Normalize(Str(s, "filterType")) is "1" or "tag" or "tags";
            return [("color", Str(s, "color") is { Length: > 0 } color ? color : "#FF8080"), ("nameContains", byTag ? "" : values),
                    ("tagContains", byTag ? values : ""), ("mix", "1")];
        });
        Plain("12008", "VTube Studio Reset All Color Tints", "vtubestudio.resetColorTint");
        With("12009", "VTube Studio Set Expression State", "vtubestudio.setExpression", (s, _, l) =>
            [("expression", NameOr(s, "expressionName", "expressionFile")), ("state", Pick(s, "state", l, "On", "Off", "Toggle")), ("fadeTime", Str(s, "fadeTime") ?? "0.25")]);
        With("12010", "VTube Studio Send Raw Request", "vtubestudio.rawRequest", VtsRaw);
        With("19001", "Throwing System Activate Trigger", "tits.activateTrigger", (s, _, _) => [("trigger", Str(s, "triggerId") ?? "")]);
        With("19002", "Throwing System Activate Trigger by Name", "tits.activateTriggerByName", (s, _, _) => [("trigger", Str(s, "triggerName") ?? "")]);
        With("19003", "Throwing System Throw Item", "tits.throwItem", (s, _, _) => [("item", Str(s, "itemId") ?? ""), .. Throw(s)]);
        With("19004", "Throwing System Throw Item by Name", "tits.throwItemByName", (s, _, _) => [("item", Str(s, "itemName") ?? ""), .. Throw(s)]);
        With("19005", "Throwing System Throw Items", "tits.throwItems", (s, _, _) => [("items", Lines(Get(s, "items"))), .. Throw(s)]);
        With("19006", "Throwing System Throw Items by Name", "tits.throwItemsByName", (s, _, _) => [("items", Lines(Get(s, "items"))), .. Throw(s)]);
        Plain("40000", "mtion Get Active Clubhouse", "mtion.getActiveClubhouse");
        With("40001", "mtion Send Trigger", "mtion.sendTrigger", (s, _, _) => [("trigger", NameOr(s, "triggerName", "triggerId")), ("parameters", Lines(Get(s, "parameters")))]);

        // ---------------------------------------------------------------- Elgato
        With("11004", "Stream Deck Set State", "streamDeck.setState", (s, _, _) => [("buttonId", Str(s, "buttonId") ?? ""), ("state", Str(s, "state") is "1" ? "1" : "0")]);
        With("11005", "Stream Deck Show Alert", "streamDeck.showAlert", (s, _, _) => [("buttonId", Str(s, "buttonId") ?? "")]);
        With("11006", "Stream Deck Show Ok", "streamDeck.showOk", (s, _, _) => [("buttonId", Str(s, "buttonId") ?? "")]);
        With("11007", "Stream Deck Toggle State", "streamDeck.toggleState", (s, _, _) => [("buttonId", Str(s, "buttonId") ?? "")]);
        With("14001", "Wave Link Mute Microphone", "wavelink.legacy.muteMicrophone", (s, _, l) =>
            [("inputDevice", NameOr(s, "microphoneName", "identifier")), ("state", Pick(s, "state", l, "Mute", "Unmute", "Toggle"))]);
        With("14002", "Wave Link Mute Output", "wavelink.legacy.muteOutput", (s, c, l) => [("mix", OutputMix(s, c, l)), ("state", Pick(s, "state", l, "Mute", "Unmute", "Toggle"))]);
        With("14003", "Wave Link Mute Input", "wavelink.legacy.muteInput", (s, _, l) =>
            [("channel", NameOr(s, "inputName", "identifier")), ("mix", Mix(s)), ("state", Pick(s, "state", l, "Mute", "Unmute", "Toggle"))]);
        With("14004", "Wave Link Set Filter Bypass State", "wavelink.legacy.setFilterBypass", (s, _, l) =>
            [("channel", NameOr(s, "inputName", "identifier")), ("mix", Mix(s)), ("state", Pick(s, "state", l, "Bypassed", "Not Bypassed", "Toggle"))]);
        With("14005", "Wave Link Set Filter State", "wavelink.legacy.setFilterState", (s, _, l) =>
            [("channel", NameOr(s, "inputName", "identifier")), ("effect", NameOr(s, "filterName", "filterId")), ("state", Pick(s, "state", l, "Enabled", "Disabled", "Toggle"))]);
        With("14006", "Wave Link Set Output Monitor Device", "wavelink.legacy.setOutputMonitorDevice", (s, _, _) => [("outputDevice", NameOr(s, "deviceName", "identifier"))]);
        Plain("14007", "Wave Link Get Selected Output", "wavelink.legacy.getSelectedOutput");
        With("14008", "Wave Link Set Output Volume", "wavelink.legacy.setOutputVolume", (s, c, l) => [("mix", OutputMix(s, c, l)), .. Volume(s, "volume", 100)]);
        Plain("14009", "Wave Link Get Output Volumes", "wavelink.legacy.getOutputVolumes");
        With("14010", "Wave Link Set Input Volume", "wavelink.legacy.setInputVolume", (s, _, _) =>
            [("channel", NameOr(s, "inputName", "identifier")), ("mix", Mix(s)), .. Volume(s, "volume", 100)]);
        With("14011", "Wave Link Get Input Information", "wavelink.legacy.getInputInfo", (s, _, _) => [("channel", NameOr(s, "inputName", "identifier"))]);
        With("14012", "Wave Link Get Microphone Information", "wavelink.legacy.getMicrophoneInfo", (s, _, _) => [("inputDevice", NameOr(s, "microphoneName", "identifier"))]);
        With("14013", "Wave Link Set Microphone Gain", "wavelink.legacy.setMicrophoneGain", (s, _, _) =>
            [("inputDevice", NameOr(s, "microphoneName", "identifier")), ("gain", Str(s, "gain") ?? "50"), ("relative", "false")]);
        With("14014", "Wave Link Set Microphone Output Volume", "wavelink.legacy.setMicrophoneOutputVolume", (s, _, _) =>
            [("inputDevice", NameOr(s, "microphoneName", "identifier")), .. Volume(s, "volume", 50)]);
        With("14015", "Wave Link Set Microphone Balance", "wavelink.legacy.setMicrophoneBalance", (s, _, _) =>
            [("inputDevice", NameOr(s, "microphoneName", "identifier")), ("balance", Str(s, "balance") ?? "50"), ("relative", "false")]);
        With("14016", "Wave Link Get Filter State", "wavelink.legacy.getFilterState", (s, _, _) =>
            [("channel", NameOr(s, "inputName", "identifier")), ("effect", NameOr(s, "filterName", "filterId"))]);
        foreach (var (code, field, setting) in new[]
                 {
                     ("14301", "contrast", "Contrast"), ("14302", "brightness", "Brightness"), ("14303", "saturation", "Saturation"), ("14304", "zoom", "Zoom"),
                     ("14305", "pan", "Pan"), ("14306", "tilt", "Tilt"), ("14307", "focus", "Focus"), ("14309", "whiteBalance", "WhiteBalance"),
                     ("14313", "sharpness", "Sharpness"),
                 })
        {
            var target = "elgato.cameraHub.webcam.set" + setting;
            Custom(code, $"Camera Hub Webcam Set {setting}", target, (s, c) =>
            {
                var step = Step(target, s, ("device", ""), ("adjustment", Flag(Bool(s, "adjustment") ?? false)));
                if (Str(s, field) is { Length: > 0 } value) step.Parameters["value"] = value;
                return step;
            });
        }
        With("14308", "Camera Hub Webcam Set Auto White Balance", "elgato.cameraHub.webcam.setAutoWhiteBalance", (s, _, l) =>
            [("device", ""), ("mode", Pick(s, "state", l, "Enabled", "Disabled", "Toggle"))]);
        With("14312", "Camera Hub Webcam Set Anti Flicker", "elgato.cameraHub.webcam.setAntiFlicker", (s, c, l) =>
            [("device", ""), ("antiFlicker", AntiFlicker(s, c, l))]);

        // ---------------------------------------------------------------- Streamer.bot remote and decks
        With("20002", "Send Remote Signal", "remote.sendSignal", (s, c, l) =>
            [("instance", Ref(s, c, l, "instanceId", "remote instance")), ("name", Str(s, "signalName") ?? ""),
             ("includeArgs", Flag(Bool(s, "useArge", "useArgs") ?? false)), ("arguments", Lines(Get(s, "customArgs")))]);
        With("20003", "Wait For Remote Signal", "remote.waitForSignal", (s, c, l) =>
            [("instance", Ref(s, c, l, "instanceId", "remote instance")), ("name", Str(s, "signalName") ?? ""),
             ("timeoutMs", Text(Math.Max(0, Num(s, "timeout") ?? 30000))), ("overwrite", Flag(Bool(s, "overwrite") ?? false))]);
        With("21001", "Deck Item Set State", "decks.itemSetState", (s, _, _) => [.. DeckItem(s), DeckState(s, "state")]);
        With("21002", "Deck Item Toggle State", "decks.itemToggleState", (s, _, _) => DeckItem(s));
        With("21003", "Deck Item Set Title", "decks.itemSetTitle", (s, _, _) =>
            [.. DeckItem(s), ("title", Str(s, "title") ?? ""), ("color", Str(s, "color") ?? ""), DeckState(s, "state")]);
        With("21004", "Deck Item Set Background", "decks.itemSetBackground", (s, c, l) =>
            [.. DeckItem(s), ("color", Str(s, "color") ?? ""), ("url", DeckUrl(s, c, l)), DeckState(s, "state")]);
        With("21005", "Deck Item Set Icon", "decks.itemSetIcon", (s, c, l) =>
            [.. DeckItem(s), ("color", Str(s, "color") ?? ""), ("name", Str(s, "name") ?? ""), ("url", DeckUrl(s, c, l)), DeckState(s, "state")]);
        With("21006", "Deck Item Set Value", "decks.itemSetValue", (s, _, _) => [.. DeckItem(s), ("value", Str(s, "value") ?? ""), DeckState(s, "state")]);
        With("21007", "Deck Notify", "decks.notify", (s, c, l) =>
        {
            Ignored(s, c, l, "color", "Cuebot's deck notices use the deck's colours");
            Ignored(s, c, l, "soundId", "Cuebot's deck notices play no sound");
            return [Deck(s), ("title", Str(s, "title") ?? ""), ("description", Str(s, "description") ?? "")];
        });
        With("21008", "Deck Page Set", "decks.pageSet", (s, _, _) => [Deck(s), ("page", Text(Math.Max(1, Num(s, "page") ?? 1)))]);
        With("21009", "Deck Page Next", "decks.pageNext", (s, _, _) => [Deck(s)]);
        With("21010", "Deck Page Previous", "decks.pagePrevious", (s, _, _) => [Deck(s)]);

        // ---------------------------------------------------------------- Kick
        With("35001", "Kick Send Message", "kick.sendMessage", (s, _, _) =>
            [("message", Str(s, "text") ?? ""), ("useBot", Flag(Bool(s, "useBot") ?? false)), ("fallback", Flag(Bool(s, "fallback") ?? false))]);
        With("35002", "Kick Reply To Message", "kick.replyToMessage", (s, _, _) =>
            [("message", Str(s, "text") ?? ""), ("replyTo", Str(s, "replyId") is { Length: > 0 } id ? id : "%messageId%"),
             ("useBot", Flag(Bool(s, "useBot") ?? false)), ("fallback", Flag(Bool(s, "fallback") ?? false))]);
        Plain("35010", "Kick Add Broadcaster Information", "kick.addBroadcasterInfo");
        With("35011", "Kick Get User Info for Target", "kick.getUserInfo", (s, c, _) => [("user", UserLogin(s, c, "%userName%")), ("prefix", "target")]);
        With("35012", "Kick Add Random Users", "kick.addRandomUsers", (s, c, l) =>
        {
            if (Arr(s, "groups") is { Count: > 0 } groups)
                c.Warn($"'{c.ActionName}': a {l} step only picks from the groups {string.Join(", ", groups.Select(AsString))}; Cuebot picks from everyone present.");
            return [("count", Text(Math.Max(1, Num(s, "count") ?? 1))), ("excludeCaller", Flag(Bool(s, "includeFrom") == false)), ("prefix", "randomUser")];
        });
        With("35020", "Kick Add User To Group", "kick.addUserToGroup", (s, c, _) => [.. KickGroup(s, c), ("create", "true")]);
        With("35021", "Kick Remove User from Group", "kick.removeUserFromGroup", (s, c, _) => KickGroup(s, c));
        With("35022", "Kick User In Group", "kick.userInGroup", (s, c, _) => [.. KickGroup(s, c), ("argument", "inGroup")]);
        With("35030", "Kick Set Channel Title", "kick.setTitle", (s, _, _) => [("title", Str(s, "title") ?? "")]);
        With("35031", "Kick Set Channel Category", "kick.setCategory", (s, c, l) =>
        {
            if (string.IsNullOrEmpty(Str(s, "categoryName")) && Str(s, "categoryId") is { Length: > 0 } id)
                c.Warn($"'{c.ActionName}': a {l} step picks category id {id}; Cuebot sets the category by name. Enter its name.");
            return [("category", Str(s, "categoryName") ?? "")];
        });
        With("35040", "Kick Ban User", "kick.banUser", (s, c, _) => [("user", UserLogin(s, c, "%userName%")), ("reason", Str(s, "reason") ?? "")]);
        With("35041", "Kick Unban User", "kick.unbanUser", (s, c, _) => [("user", UserLogin(s, c, "%userName%"))]);
        With("35042", "Kick Timeout User", "kick.timeoutUser", (s, c, _) =>
            [("user", UserLogin(s, c, "%userName%")), ("duration", Text(Math.Max(1, Num(s, "duration") ?? 300))), ("reason", Str(s, "reason") ?? "")]);
        With("35043", "Kick UnTimeout User", "kick.untimeoutUser", (s, c, _) => [("user", UserLogin(s, c, "%userName%"))]);
        With("35101", "Kick Reward Set Enabled State", "kick.setRewardEnabled", (s, _, l) => [Reward(s), ("enabled", OnOff(s, "state", l))]);
        With("35102", "Kick Reward Set Paused State", "kick.setRewardPaused", (s, _, l) => [Reward(s), ("paused", OnOff(s, "state", l))]);
        With("35103", "Kick Reward Set Cost", "kick.setRewardCost", (s, _, l) => [Reward(s), ("cost", KickCost(s, l))]);
        With("35105", "Kick Reward Set Title", "kick.setRewardTitle", (s, _, _) => [Reward(s), ("title", Str(s, "title") ?? "")]);
        With("35106", "Kick Reward Set Description", "kick.setRewardDescription", (s, _, _) => [Reward(s), ("description", Str(s, "description") ?? "")]);
        Custom("35107", "Kick Reward Update", "core.group", MapKickRewardUpdate);
        With("35108", "Kick Get Reward Info", "kick.getRewardInfo", (s, _, _) => [Reward(s)]);
        With("35113", "Kick Reward Set Background Color", "kick.setRewardColor", (s, c, l) =>
        {
            Ignored(s, c, l, "random", "Cuebot sets the colour it was given");
            return [Reward(s), ("color", Str(s, "backgroundColor") is { Length: > 0 } color ? color : "#00E701")];
        });

        // ---------------------------------------------------------------- Meld Studio
        Meld("41001", "Meld Studio Get Current Scene", "meldstudio.getCurrentScene");
        Meld("41002", "Meld Studio Get Status", "meldstudio.getStatus");
        Meld("41003", "Meld Studio Recording", "meldstudio.recording", (s, _, l) => [("state", Pick(s, "state", l, "Start", "Stop", "Toggle"))]);
        Meld("41004", "Meld Studio Streaming", "meldstudio.streaming", (s, _, l) => [("state", Pick(s, "state", l, "Start", "Stop", "Toggle"))]);
        Meld("41005", "Meld Studio Show Scene", "meldstudio.showScene", (s, _, _) => [("scene", Str(s, "sceneName") ?? "")]);
        Meld("41006", "Meld Studio Set Layer Visibility State", "meldstudio.setLayerVisibility", (s, _, l) =>
            [("scene", Str(s, "sceneName") ?? ""), ("layer", Str(s, "layerName") ?? ""), ("state", Pick(s, "state", l, "Visible", "Hidden", "Toggle"))]);
        Meld("41007", "Meld Studio Set Effect Enabled State", "meldstudio.setEffectState", (s, _, l) =>
            [("scene", Str(s, "sceneName") ?? ""), ("layer", Str(s, "layerName") ?? ""), ("effect", Str(s, "effectName") ?? ""),
             ("state", Pick(s, "state", l, "Enabled", "Disabled", "Toggle"))]);
        Meld("41008", "Meld Studio Set Layer Track Muted State", "meldstudio.setLayerTrackMuted", (s, c, l) =>
        {
            if (Str(s, "trackName") is { Length: > 0 } track)
                c.Warn($"'{c.ActionName}': a {l} step mutes the layer's '{track}' track; Cuebot mutes the layer's audio.");
            return [("scene", Str(s, "sceneName") ?? ""), ("layer", Str(s, "layerName") ?? ""), ("state", Pick(s, "state", l, "Muted", "Not Muted", "Toggle"))];
        });
        // The track field's name is obfuscated in Streamer.bot 1.0.7; it is the one text field next to "state".
        Meld("41009", "Meld Studio Set Track Muted State", "meldstudio.setTrackMuted", (s, _, l) =>
            [("track", Str(s, "trackName", "track") ?? s.Where(kv => kv.Key.StartsWith('_')).Select(kv => AsString(kv.Value)).FirstOrDefault(v => v is not null) ?? ""),
             ("state", Pick(s, "state", l, "Muted", "Not Muted", "Toggle"))]);
        Meld("41010", "Meld Studio Take Screenshot", "meldstudio.takeScreenshot", (_, _, _) => [("orientation", "Horizontal")]);
        Meld("41013", "Meld Studio Take Vertical Screenshot", "meldstudio.takeVerticalScreenshot");
        Meld("41014", "Meld Studio Set Browser Layer Url", "meldstudio.setBrowserLayerUrl", (s, _, _) =>
            [("scene", Str(s, "sceneName") ?? ""), ("layer", Str(s, "layerName") ?? ""), ("url", Str(s, "url") ?? "")]);
        Meld("41017", "Meld Studio Record Clip", "meldstudio.recordClip");

        // ---------------------------------------------------------------- structure (not in the menu)
        // A branch or case found outside its if/else or switch keeps its children in a group.
        foreach (var (code, label) in new[]
                 {
                     (IfTrueBranchCode, "If true branch"), (IfFalseBranchCode, "If false branch"), (SwitchCaseCode, "Switch case"),
                     (SwitchDefaultCode, "Switch default case"),
                 })
        {
            Custom(code, label, "core.group", (s, c) =>
            {
                var step = Step("core.group", s, ("name", label), ("random", Flag(Bool(s, "random") ?? false)), ("color", ""));
                step.Branches["Steps"] = c.MapChildren(ChildArray(s));
                return step;
            });
        }
    }

    // ---- helpers for the sub-action rules ----

    /// <summary>The documented option at the field's index; an index past the options cannot be converted.</summary>
    private static string Pick(JsonObject s, string field, string label, params string[] options)
    {
        var index = Int(s, field) ?? 0;
        if (index < 0 || index >= options.Length) throw new SbMappingException($"{label}: {field} {index} is not known");
        return options[index];
    }

    /// <summary>On/off switches: 0 on, 1 off. A toggle (2) has no Cuebot equivalent.</summary>
    private static string OnOff(JsonObject s, string field, string label) => Pick(s, field, label, "true", "false");

    /// <summary>Enabled / Disabled / Toggle in Cuebot's state choice.</summary>
    private static string EnableState(JsonObject s, string label) => Pick(s, "state", label, "Enable", "Disable", "Toggle");

    private static (string, string)[] VoiceModState(JsonObject s, ISbMapContext c, string label) => [("state", Pick(s, "state", label, "On", "Off", "Toggle"))];

    private static (string, string) Reward(JsonObject s) => ("reward", Str(s, "rewardId") ?? "");

    /// <summary>The value of a name field, else of its id field.</summary>
    private static string NameOr(JsonObject s, string nameField, string idField) =>
        Str(s, nameField) is { Length: > 0 } name ? name : Str(s, idField) is { } id && IsRealId(id) ? id : "";

    private static string Action(JsonObject s, ISbMapContext c) => IsRealId(Str(s, "actionId")) ? c.ActionRef(Str(s, "actionId")) : "";

    /// <summary>An id of a Streamer.bot object Cuebot cannot resolve: kept as text and reported.</summary>
    private static string Ref(JsonObject s, ISbMapContext c, string label, string field, string what)
    {
        var id = Str(s, field);
        if (!IsRealId(id)) return "";
        c.Warn($"'{c.ActionName}': a {label} step names {what} id {id}; Cuebot cannot match Streamer.bot ids. Choose it in Cuebot.");
        return id!.Trim();
    }

    private static string QueueOrAll(JsonObject s, ISbMapContext c) => c.QueueRef(Str(s, "actionQueueId")) is { Length: > 0 } queue ? queue : "All";

    /// <summary>Reports a field that holds a value Cuebot's step cannot use.</summary>
    private static void Ignored(JsonObject s, ISbMapContext c, string label, string field, string why)
    {
        if (Get(s, field) is not { } node || IsUnsetFilter(field, node)) return;
        c.Warn($"'{c.ActionName}': a {label} step sets '{field}' ({AsString(node) ?? node.ToJsonString()}); {why}.");
    }

    /// <summary>A connection chosen by id (Streamlabs Desktop, Meld Studio): Cuebot uses its one connection.</summary>
    private static void Connection(JsonObject s, ISbMapContext c, string label, string app)
    {
        if (IsRealId(Str(s, "connectionId")))
            c.Warn($"'{c.ActionName}': a {label} step uses a specific {app} connection (id {Str(s, "connectionId")}); Cuebot uses its {app} connection.");
    }

    /// <summary>Lists and key/value objects as lines ("key=value"); text as it is.</summary>
    private static string Lines(JsonNode? node) => node switch
    {
        JsonArray a => string.Join("\n", a.Select(n => n is JsonValue ? AsString(n) : n?.ToJsonString()).Where(v => !string.IsNullOrEmpty(v))),
        JsonObject o => string.Join("\n", o.Select(kv => $"{kv.Key}={(kv.Value is JsonValue ? AsString(kv.Value) : kv.Value?.ToJsonString()) ?? ""}")),
        _ => AsString(node) ?? "",
    };

    private static (string, string)[] LineOptions(JsonObject s) =>
        [("parseVariables", Flag(Bool(s, "parseVariables") ?? false)), ("autoType", Flag(Bool(s, "autoType") ?? false))];

    private static (string, string)[] Announcement(JsonObject s, ISbMapContext c, string label) =>
        [("message", Str(s, "text") ?? ""), ("color", Pick(s, "color", label, "primary", "blue", "green", "orange", "purple"))];

    /// <summary>Set Cost / Update: operator 0 set, 1 add, 2 subtract, 3 multiply, 4 divide (the wiki's order).</summary>
    private static string CostOperator(JsonObject s, string label) => Pick(s, "operator", label, "Set", "Add", "Subtract", "Multiply", "Divide");

    private static string KickCost(JsonObject s, string label)
    {
        if (CostOperator(s, label) != "Set") throw new SbMappingException($"{label}: Cuebot sets a Kick reward's cost; it cannot add to it");
        return Str(s, "cost") is { Length: > 0 } cost ? cost : "100";
    }

    /// <summary>A Guest Star setting left as null is not changed.</summary>
    private static string Keep(bool? value) => value switch { true => "On", false => "Off", _ => "Keep" };

    private static string GuestStarLayout(string? layout) => Normalize(layout) switch
    {
        "" => "Keep",
        "0" or "tiledlayout" or "tiled" => "TILED_LAYOUT",
        "1" or "screensharelayout" or "screenshare" => "SCREENSHARE_LAYOUT",
        "2" or "horizontallayout" or "horizontal" => "HORIZONTAL_LAYOUT",
        "3" or "verticallayout" or "vertical" => "VERTICAL_LAYOUT",
        var other => throw new SbMappingException($"Guest Star layout '{other}' is not known"),
    };

    /// <summary>A platform user group: a login names the user; none means the user who ran the action.</summary>
    private static (string, string)[] UserGroup(JsonObject s, ISbMapContext c, string platform)
    {
        var group = ("group", Str(s, "groupName") ?? "");
        return Str(s, "userLogin") is { Length: > 0 }
            ? [group, ("platform", platform), ("user", UserLogin(s, c)), ("userId", "")]
            : [group, ("platform", platform), ("user", "%user%"), ("userId", "%userId%")];
    }

    private static (string, string)[] KickGroup(JsonObject s, ISbMapContext c)
    {
        var group = ("group", Str(s, "groupName") ?? "");
        return Str(s, "userLogin") is { Length: > 0 }
            ? [group, ("user", UserLogin(s, c)), ("userId", "")]
            : [group, ("user", "%userName%"), ("userId", "%userId%")];
    }

    private static (string, string)[] Midi(JsonObject s, ISbMapContext c, string label)
    {
        if (IsRealId(Str(s, "device")))
            c.Warn($"'{c.ActionName}': a {label} step sends to MIDI device id {Str(s, "device")}; Cuebot sends to the device named in the step. Choose it in Cuebot.");
        return [("name", Str(s, "name") ?? ""), ("device", ""), ("channel", Str(s, "channel") ?? "")];
    }

    /// <summary>Wave Link mixer: com.elgato.mix.local / .stream, else both.</summary>
    private static string Mix(JsonObject s) => (Str(s, "mixerType") ?? "") switch
    {
        var m when m.EndsWith(".local", StringComparison.OrdinalIgnoreCase) => "Local",
        var m when m.EndsWith(".stream", StringComparison.OrdinalIgnoreCase) => "Stream",
        _ => "Both",
    };

    /// <summary>An output is the local (monitor) or the stream mix; "all" becomes the local one and is reported.</summary>
    private static string OutputMix(JsonObject s, ISbMapContext c, string label)
    {
        var mix = Mix(s);
        if (mix != "Both") return mix;
        c.Warn($"'{c.ActionName}': a {label} step changes both Wave Link outputs; Cuebot changes the local one. Add a second step for the stream mix.");
        return "Local";
    }

    /// <summary>Wave Link volumes: "adjustment" adds to the current value.</summary>
    private static (string, string)[] Volume(JsonObject s, string field, double fallback) =>
        [("volume", Text(Num(s, field) ?? fallback)), ("relative", Flag(Bool(s, "adjustment") ?? false))];

    private static string AntiFlicker(JsonObject s, ISbMapContext c, string label)
    {
        var value = Str(s, "antiFlicker");
        string[] choices = ["Off", "50 Hz", "60 Hz", "Auto"];
        if (string.IsNullOrEmpty(value)) return "60 Hz";
        if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) && i >= 0 && i < choices.Length) return choices[i];
        var match = choices.FirstOrDefault(ch => Normalize(ch) == Normalize(value));
        if (match is not null) return match;
        c.Warn($"'{c.ActionName}': a {label} step sets anti-flicker '{value}', which Cuebot does not know; it was set to 60 Hz.");
        return "60 Hz";
    }

    private static (string, string)[] Throw(JsonObject s) =>
        [("delay", Str(s, "delay") is { Length: > 0 } d ? d : "0.05"), ("amount", Str(s, "amount") is { Length: > 0 } a ? a : "1")];

    private static (string, string)[] VtsRaw(JsonObject s, ISbMapContext c, string label)
    {
        // "raw" holds the whole request ({"messageType": ..., "data": {...}}); "request" names its message type.
        JsonObject? raw = null;
        try
        {
            raw = Str(s, "raw") is { Length: > 0 } text ? JsonNode.Parse(text) as JsonObject : null;
        }
        catch (System.Text.Json.JsonException)
        {
            c.Warn($"'{c.ActionName}': a {label} step holds a raw request that is not JSON; it was imported without data.");
        }
        var type = Str(raw, "messageType") ?? Str(s, "request") ?? "";
        return [("messageType", type), ("data", Get(raw, "data")?.ToJsonString() ?? ""), ("resultArgument", "vtsResult"),
                ("variablePrefix", Str(s, "prefix") is { Length: > 0 } prefix ? prefix : "vtsRaw")];
    }

    private static (string, string) Deck(JsonObject s) => ("deck", NameOr(s, "deckName", "deckId"));

    private static (string, string)[] DeckItem(JsonObject s) =>
        [Deck(s), ("item", Str(s, "itemName") is { Length: > 0 } name ? name : Str(s, "itemId") is { Length: > 0 } id ? id : "%deckItemName%")];

    private static (string, string) DeckState(JsonObject s, string field) => ("state", Text(Math.Max(0, Num(s, field) ?? 0)));

    private static string DeckUrl(JsonObject s, ISbMapContext c, string label)
    {
        if (Str(s, "url") is { Length: > 0 } url) return url;
        if (Str(s, "fileId") is { Length: > 0 } file)
            c.Warn($"'{c.ActionName}': a {label} step uses Streamer.bot deck file {file}; give Cuebot's step a URL or path instead.");
        return "";
    }

    /// <summary>
    /// File / Folder Exists: Streamer.bot stores True/False in an argument; Cuebot branches, so each branch sets
    /// that argument for the steps after it.
    /// </summary>
    private static SubActionDefinition Exists(JsonObject s, string path, string kind, string fallback)
    {
        var variable = Str(s, "variableName") is { Length: > 0 } v ? v : fallback;
        var step = Step("core.fileExists", s, ("path", path), ("kind", kind));
        foreach (var (branch, value) in new[] { ("True", "True"), ("False", "False") })
        {
            step.Branches[branch] =
            [
                new SubActionDefinition { Type = "core.setArgument", Parameters = { ["name"] = variable, ["value"] = value, ["autoType"] = "true" } },
            ];
        }
        return step;
    }

    /// <summary>Switch: each 99903 child is a case (its "values" become one case line, separated by |); 99904 is the default.</summary>
    private static SubActionDefinition MapSwitch(JsonObject s, ISbMapContext c)
    {
        var step = Step("core.switch", s, ("input", Str(s, "input") ?? ""), ("operator", "equals"));
        var lines = new List<string>();
        var caseSensitive = false;
        foreach (var node in Objects(ChildArray(s)))
        {
            var key = TypeKeyOf(node);
            if (key == SwitchDefaultCode)
            {
                step.Branch(SwitchDefaultBranch).AddRange(c.MapChildren(ChildArray(node)));
                continue;
            }
            if (key != SwitchCaseCode)
            {
                c.Warn($"'{c.ActionName}': a switch holds a sub-action outside its cases (type {key}); it was dropped.");
                continue;
            }
            var values = Arr(node, "values")?.Select(AsString).OfType<string>().Select(v => v.Trim()).Where(v => v.Length > 0).ToList() ?? [];
            if (values.Any(v => v.Contains('|')))
                c.Warn($"'{c.ActionName}': a switch case value contains '|', which Cuebot reads as a separator between values. Please check the case.");
            caseSensitive |= Bool(node, "caseSensitive") == true && values.Any(v => v.Any(char.IsLetter));
            var line = string.Join(" | ", values);
            if (line.Length == 0)
            {
                c.Warn($"'{c.ActionName}': a switch case has no values; its sub-actions were dropped.");
                continue;
            }
            if (!lines.Contains(line)) lines.Add(line);
            step.Branch(line).AddRange(c.MapChildren(ChildArray(node)));
        }
        if (caseSensitive) c.Warn($"'{c.ActionName}': a switch matches case-sensitively; Cuebot compares text ignoring case.");
        step.Parameters["cases"] = string.Join("\n", lines);
        return step;
    }

    private const string SwitchDefaultBranch = "Default";

    /// <summary>Kick Update: Cuebot sets a Kick reward's title, description and cost with one step each.</summary>
    private static SubActionDefinition MapKickRewardUpdate(JsonObject s, ISbMapContext c)
    {
        var reward = Reward(s);
        var group = Step("core.group", s, ("name", "Update Kick reward"), ("random", "false"), ("color", ""));
        var steps = group.Branch("Steps");
        if (Str(s, "title") is { Length: > 0 } title)
            steps.Add(new SubActionDefinition { Type = "kick.setRewardTitle", Parameters = { [reward.Item1] = reward.Item2, ["title"] = title } });
        if (Str(s, "description") is { Length: > 0 } description)
            steps.Add(new SubActionDefinition { Type = "kick.setRewardDescription", Parameters = { [reward.Item1] = reward.Item2, ["description"] = description } });
        if (Str(s, "cost") is { Length: > 0 })
            steps.Add(new SubActionDefinition { Type = "kick.setRewardCost", Parameters = { [reward.Item1] = reward.Item2, ["cost"] = KickCost(s, "Kick Reward Update") } });
        return group;
    }
}
