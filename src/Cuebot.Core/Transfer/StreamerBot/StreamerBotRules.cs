using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using static Cuebot.Core.Transfer.StreamerBot.SbJson;

namespace Cuebot.Core.Transfer.StreamerBot;

/// <summary>
/// The built-in Streamer.bot rules. Each rule lists descriptive type names and the numeric type code Streamer.bot
/// really stores. The sub-action codes and the settings each saves were read from a real Streamer.bot 1.0.7 (see
/// StreamerBotSubActionRules.cs, which holds the rules for the codes not handled here); rules marked
/// <see cref="SbRule.Tentative"/> hold codes whose Streamer.bot name is only likely. Trigger codes were read from the
/// same install (StreamerBotTriggerRules.cs). docs/streamerbot-import-codes.md lists every code.
/// </summary>
public sealed partial class StreamerBotMappingTable
{
    /// <summary>Child sub-action types of a Streamer.bot if/else: the "true" and the "false" branch.</summary>
    public const string IfTrueBranchCode = "99901";
    public const string IfFalseBranchCode = "99902";

    public static StreamerBotMappingTable CreateDefault()
    {
        var t = new StreamerBotMappingTable();

        // ---------------------------------------------------------------- core: flow and variables
        t.Add(new SbSubActionRule("Delay", "core.delay", ["Delay", "Wait", "1002"], MapDelay)
        {
            Signatures = [["delay"], ["maxDelay"]],
        });
        t.Add(new SbSubActionRule("SetGlobal", "core.setGlobal",
            ["SetGlobal", "GlobalSet", "SetGlobalVariable", "GlobalVariableSet", "122"], MapSetGlobal)
        {
            Signatures = [["variableName", "value", "persisted"]],
        });
        t.Add(new SbSubActionRule("GetGlobal", "core.getGlobal",
            ["GetGlobal", "GlobalGet", "GetGlobalVariable", "GlobalVariableGet", "121"], MapGetGlobal)
        {
            Signatures = [["variableName", "destinationVariable"]],
        });
        t.Add(new SbSubActionRule("SetArgument", "core.setArgument", ["SetArgument", "SetArg", "ArgumentSet", "123"],
            (s, _) => Step("core.setArgument", s, ("name", Str(s, "variableName", "argumentName", "name") ?? ""), ("value", Str(s, "value") ?? ""),
                ("autoType", Flag(Bool(s, "autoType") ?? false)))));
        t.Add(new SbSubActionRule("RunAction", "core.runAction", ["RunAction", "ActionRun", "RunActionById", "4"], (s, c) =>
        {
            var target = Str(s, "actionId", "actionName", "action");
            var step = Step("core.runAction", s, ("action", c.ActionRef(target)),
                // "runImmedately" is how Streamer.bot spells it in its exports.
                ("wait", Flag(Bool(s, "runImmediately", "runImmedately", "wait") ?? true)));
            if (!IsRealId(target))
            {
                step.Enabled = false;
                c.Warn($"'{c.ActionName}': a Run action step names no action; it was imported disabled.");
            }
            return step;
        })
        {
            Signatures = [["actionId"]],
            Excludes = ["state", "enabled", "operation"],
        });
        t.Add(new SbSubActionRule("IfElse", "core.if", ["IfElse", "If", "Condition", "IfCondition", "120"], MapIf)
        {
            Signatures = [["input", "operation", "value"]],
        });
        t.Add(new SbSubActionRule("While", "core.while", ["While", "128"], MapWhile));
        t.Add(new SbSubActionRule("Group", "core.group", ["Group", "SubActionGroup", "ActionGroup", "99900"], (s, c) =>
        {
            var step = Step("core.group", s, ("name", Str(s, "name", "groupName") ?? "Group"),
                ("random", Flag(Bool(s, "random") ?? false)), ("color", Str(s, "color", "colour") ?? ""));
            step.Branches["Steps"] = c.MapChildren(ChildArray(s));
            return step;
        }));
        t.Add(new SbSubActionRule("Comment", "core.comment", ["Comment", "Note", "1009"],
            (s, _) => Step("core.comment", s, ("text", Str(s, "text", "comment", "value") ?? ""), ("color", Str(s, "color", "colour") ?? ""))));
        t.Add(new SbSubActionRule("LogMessage", "core.log", ["LogMessage", "Log", "WriteLog", "WriteToLog", "1020"],
            (s, _) => Step("core.log", s, ("message", Str(s, "message", "text", "value") ?? ""), ("level", LogLevel(Str(s, "logLevel", "level"))))));
        t.Add(new SbSubActionRule("Break", "core.stop", ["Break", "BreakAction", "StopAction", "Stop", "124"], (s, _) => Step("core.stop", s)));
        t.Add(new SbSubActionRule("RandomNumber", "core.randomNumber", ["GetRandomNumber", "RandomNumber", "1003"], (s, _) =>
        {
            // Type 0 is "Between"; type 1 is "Next Float" (the order the documentation lists them in).
            return Int(s, "randomType") switch
            {
                null or 0 => Step("core.randomNumber", s, ("name", "randomNumber"), ("min", Str(s, "min") ?? "1"), ("max", Str(s, "max") ?? "100")),
                1 => Step("core.randomNumber", s, ("type", "Next Float")),
                var kind => throw new SbMappingException($"random number type {kind} has no Cuebot equivalent"),
            };
        }));
        t.Add(new SbSubActionRule("TimerState", "timers.setState", ["SetTimerState", "TimerState", "8"], (s, c) =>
            Step("timers.setState", s, ("timer", IsRealId(Str(s, "timerId", "timer", "timerName")) ? c.TimerRef(Str(s, "timerId", "timer", "timerName")) : ""), ("state", Normalize(Str(s, "state")) switch
            {
                "0" or "enable" or "enabled" => "Enable",
                "1" or "disable" or "disabled" => "Disable",
                "2" or "toggle" => "Toggle",
                var other => throw new SbMappingException($"timer state '{other}' is not known"),
            }))));
        t.Add(new SbSubActionRule("FetchUrl", "core.httpRequest", ["FetchUrl", "1007"], MapFetchUrl));
        t.Add(new SbSubActionRule("ReadRandomLine", "core.readRandomLine", ["ReadRandomLine", "5"], (s, _) =>
            Step("core.readRandomLine", s, ("path", Str(s, "file", "path") ?? ""), ("variable", Str(s, "variableName") ?? "randomLine"),
                ("count", Text(Math.Max(1, Int(s, "count") ?? 1))), ("parseVariables", Flag(Bool(s, "parseVariables") ?? false)),
                ("autoType", Flag(Bool(s, "autoType") ?? false)))));
        t.Add(new SbSubActionRule("RunProgram", "core.runProgram", ["RunProgram", "RunAProgram", "6"], (s, c) =>
        {
            // Streamer.bot's "Wait for Exit" is a number of seconds; waiting fills %outputCount% / %output#%.
            var seconds = Num(s, "waitForExit") ?? 0;
            var wait = seconds > 0;
            return Step("core.runProgram", s, ("path", Str(s, "command", "path") ?? ""), ("arguments", Str(s, "arguments") ?? ""),
                ("workingDirectory", Str(s, "workingDir", "workingDirectory") ?? ""), ("wait", Flag(wait)),
                ("waitSeconds", Text(Math.Max(0, seconds))), ("captureOutput", Flag(wait)),
                ("environment", Obj(s, "envVars") is { Count: > 0 } env ? string.Join("\n", env.Select(kv => $"{kv.Key}={AsString(kv.Value)}")) : ""));
        }));
        t.Add(new SbSubActionRule("ToastNotification", "desktop.showNotification", ["ToastNotification", "1024"],
            (s, _) => Step("desktop.showNotification", s, ("title", Str(s, "title") ?? ""), ("message", Str(s, "text", "message") ?? ""),
                ("icon", Str(s, "iconPath", "icon") ?? ""), ("id", Str(s, "toastId") ?? ""))));
        t.Add(new SbSubActionRule("GetCommands", "commands.getCommands", ["GetCommands", "1013"], (s, _) =>
        {
            var group = Str(s, "groupName") ?? "";
            // "Ignore Aliases" lists only each command's first text; otherwise every alias is listed.
            return Step("commands.getCommands", s, ("argument", Str(s, "variableName") is { Length: > 0 } v ? v : "commands"),
                ("group", group.Equals("All", StringComparison.OrdinalIgnoreCase) ? "" : group),
                ("show", Bool(s, "ignoreAliases") == false ? "All aliases" : "First alias"), ("includeAll", Flag(Bool(s, "includeAll") ?? false)),
                ("onlyPermitted", Flag(Bool(s, "hasPermission") ?? false)));
        }));
        t.Add(new SbSubActionRule("GetQuote", "quotes.get", ["GetQuote", "1000"], (s, _) =>
            // Quote type 0 is a random quote, 1 a quote by id; Cuebot picks a random one when the id is empty.
            Step("quotes.get", s, ("id", Int(s, "quoteType") == 1 ? Str(s, "quoteId") ?? "" : ""))));
        t.Add(new SbSubActionRule("AddQuote", "quotes.add", ["AddQuote", "1037"],
            (s, _) => Step("quotes.add", s, ("text", Str(s, "quoteVariable", "quote", "text") ?? "%rawInput%"))));
        t.Add(new SbSubActionRule("DeleteQuote", "quotes.delete", ["DeleteQuote", "1036"],
            (s, _) => Step("quotes.delete", s, ("id", Str(s, "quoteId") ?? "%input0%"))));

        // ---------------------------------------------------------------- code
        t.Add(new SbSubActionRule("ExecuteCode", "code.csharp", ["ExecuteCode", "ExecuteCSharpCode", "CSharpCode", "InlineCode", "ExecuteCSharp", "99999"], MapCode)
        {
            Signatures = [["byteCode"], ["code", "references"]],
        });
        t.Add(new SbSubActionRule("ExecuteMethod", "code.csharp", ["ExecuteMethod", "ExecuteCSharpMethod", "99998"], MapExecuteMethod)
        {
            Signatures = [["executeCodeId", "method"]],
        });

        // ---------------------------------------------------------------- audio, speech
        t.Add(new SbSubActionRule("PlaySound", "audio.playSound", ["PlaySound", "SoundPlay", "PlaySoundFile", "1"],
            (s, c) => Step("audio.playSound", s, [("file", Str(s, "soundFile", "fileName", "file", "path") ?? ""), ("volume", Volume(s)),
                ("wait", Flag(Bool(s, "finishBeforeContinuing", "wait", "waitUntilFinished") ?? false)), .. SoundOptions(s, c)]))
        {
            Signatures = [["soundFile"]],
        });
        t.Add(new SbSubActionRule("PlaySoundFromFolder", "audio.playSoundFromFolder", ["PlaySoundFromFolder", "7"],
            (s, c) => Step("audio.playSoundFromFolder", s, [("folder", Str(s, "folder") ?? ""), ("subfolders", Flag(Bool(s, "recursive") ?? false)),
                ("volume", Volume(s)), ("wait", Flag(Bool(s, "finishBeforeContinuing") ?? false)), .. SoundOptions(s, c)])));
        // 601 is Speaker.bot's original (UDP) speak; 602 (StreamerBotSubActionRules.cs) the websocket one.
        t.Add(new SbSubActionRule("Speak", "speech.speak", ["SpeakerBotSpeak", "Speak", "601"],
            (s, _) => Step("speech.speak", s, ("alias", Str(s, "alias") ?? ""), ("text", Str(s, "message", "text") ?? ""),
                ("badWordFilter", Flag(Bool(s, "badWordFilter") ?? false)))));

        // ---------------------------------------------------------------- Twitch
        t.Add(new SbSubActionRule("SendMessage", "twitch.sendMessage", ["SendMessage", "TwitchSendMessage", "ChatSendMessage", "SendChatMessage", "10"],
            (s, c) =>
            {
                if (Bool(s, "pin") == true) c.Warn($"'{c.ActionName}': a Twitch message is pinned when sent; Cuebot sends it unpinned (add a Pin chat message step).");
                return Step("twitch.sendMessage", s, ("message", Str(s, "text", "message") ?? ""), ("sendAs", SendAs(s)));
            })
        {
            Signatures = [["text", "useBot"]],
        });
        t.Add(new SbSubActionRule("ReplyToMessage", "twitch.replyToMessage", ["ReplyToMessage", "TwitchReply", "560"],
            (s, _) => Step("twitch.replyToMessage", s, ("message", Str(s, "text", "message") ?? ""),
                ("messageId", Str(s, "replyId", "messageId") ?? "%msgId%"), ("sendAs", SendAs(s))))
        {
            Signatures = [["text", "replyId"]],
        });
        t.Add(new SbSubActionRule("SetTitle", "twitch.setTitle", ["SetChannelTitle", "TwitchSetTitle", "15"],
            (s, _) => Step("twitch.setTitle", s, ("title", Str(s, "title") ?? ""))));
        t.Add(new SbSubActionRule("SetGame", "twitch.setGame", ["SetChannelGame", "TwitchSetGame", "16"], (s, c) =>
        {
            // Source 0 types the name (or a variable); 1 picks a game, saved with its name and id.
            var game = Str(s, "game", "gameName") ?? "";
            var step = Step("twitch.setGame", s, ("game", game));
            if (game.Length == 0)
            {
                c.Warn($"'{c.ActionName}': a Set Channel Game step names no category" + (Str(s, "gameId") is { Length: > 0 } id ? $" (only its id {id})" : "")
                       + "; it was imported disabled. Enter the category and enable it.");
                step.Enabled = false;
            }
            return step;
        }));
        t.Add(new SbSubActionRule("CreateMarker", "twitch.createMarker", ["CreateStreamMarker", "TwitchCreateMarker", "17"],
            (s, _) => Step("twitch.createMarker", s, ("description", Str(s, "description") ?? ""))));
        t.Add(new SbSubActionRule("Timeout", "twitch.timeout", ["TimeoutUser", "TwitchTimeout", "20"], (s, c) =>
            Step("twitch.timeout", s, ("user", UserLogin(s, c)), ("duration", Str(s, "duration") ?? "600"), ("reason", Str(s, "reason") ?? ""))));
        t.Add(new SbSubActionRule("Shoutout", "twitch.sendShoutout", ["SendShoutout", "TwitchShoutout", "29"],
            (s, c) => Step("twitch.sendShoutout", s, ("user", UserLogin(s, c)))));
        t.Add(new SbSubActionRule("GetUserInfo", "twitch.getUserInfo", ["GetUserInfoForTarget", "50"], (s, c) =>
        {
            if (Str(s, "userLogin") is null && Normalize(Str(s, "sourceType")) == "0")
            {
                // Source type 0 is the broadcaster: fetch the broadcaster's login first, then their info.
                var group = Step("core.group", s, ("name", "Get user info for the broadcaster"), ("random", "false"), ("color", ""));
                group.Branches["Steps"] =
                [
                    new SubActionDefinition { Type = "twitch.getBroadcasterInfo" },
                    new SubActionDefinition { Type = "twitch.getUserInfo", Parameters = { ["user"] = "%broadcasterUserName%" } },
                ];
                return group;
            }
            return Step("twitch.getUserInfo", s, ("user", TargetUser(s, c)));
        }));
        t.Add(new SbSubActionRule("GetFollowAge", "twitch.getFollowAge", ["GetFollowAgeInfoForTarget", "51"],
            (s, c) => Step("twitch.getFollowAge", s, ("user", TargetUser(s, c)))));
        t.Add(new SbSubActionRule("GetFollowerCount", "twitch.getFollowerCount", ["GetFollowerCount", "506"], (s, _) => Step("twitch.getFollowerCount", s)));
        t.Add(new SbSubActionRule("AddPresentUser", "twitch.getPresentUser", ["AddPresentUser", "550"],
            (s, _) => Step("twitch.getPresentUser", s, ("index", Str(s, "userIndex", "index") ?? "0"))));
        t.Add(new SbSubActionRule("AddBroadcasterInfo", "twitch.getBroadcasterInfo", ["AddBroadcasterInformation", "21"],
            (s, _) => Step("twitch.getBroadcasterInfo", s)));
        t.Add(new SbSubActionRule("AddViewerCount", "twitch.getStreamInfo", ["AddViewerCount", "24"], (s, _) => Step("twitch.getStreamInfo", s)));
        t.Add(new SbSubActionRule("AddPronouns", "pronouns.addForUser", ["AddPronounsForUser", "15001"],
            (s, c) => Step("pronouns.addForUser", s, ("user", UserLogin(s, c, "%userName%")), ("prefix", Str(s, "prefix") ?? ""))));

        t.Add(new SbSubActionRule("StartRaid", "twitch.startRaid", ["StartRaid", "TwitchStartRaid", "500"],
            (s, c) => Step("twitch.startRaid", s, ("user", UserLogin(s, c)))));
        t.Add(new SbSubActionRule("Ban", "twitch.ban", ["BanUser", "TwitchBan", "511"],
            (s, c) => Step("twitch.ban", s, ("user", UserLogin(s, c)), ("reason", Str(s, "reason") ?? ""))));

        // ---------------------------------------------------------------- YouTube
        t.Add(new SbSubActionRule("YouTubeSendMessage", "youtube.sendMessage", ["YouTubeSendMessage", "4001"],
            (s, _) => Step("youtube.sendMessage", s, ("message", Str(s, "text", "message") ?? ""), ("useBot", Flag(Bool(s, "useBot") ?? false)),
                ("fallback", Flag(Bool(s, "fallback") ?? false)), ("broadcastId", ""))));
        t.Add(new SbSubActionRule("YouTubeSetTitle", "youtube.setTitle", ["YouTubeSetTitle", "4002"],
            (s, _) => Step("youtube.setTitle", s, ("title", Str(s, "title") ?? ""), ("broadcastId", ""))));

        // ---------------------------------------------------------------- OBS
        t.Add(new SbSubActionRule("ObsSetScene", "obs.setScene", ["ObsSetScene", "SetScene", "ObsSetCurrentScene", "ObsScene", "34"],
            (s, c) => Obs(Step("obs.setScene", s, ("scene", Str(s, "sceneName", "scene") ?? "")), s, c))
        {
            Signatures = [["sceneName"]],
            Excludes = ["sourceName", "filterName", "itemName", "inputName", "groupName"],
        });
        t.Add(new SbSubActionRule("ObsSetSourceVisibility", "obs.setSourceVisibility",
            ["ObsSetSourceVisibility", "ObsSourceVisibility", "SetSourceVisibility", "ObsSetSourceVisibilityState", "30"],
            (s, c) => Obs(Step("obs.setSourceVisibility", s, ("scene", Str(s, "sceneName", "scene") ?? ""),
                ("source", Str(s, "sourceName", "source", "itemName") ?? ""), ("state", VisibilityState(s, c))), s, c))
        {
            Signatures = [["sceneName", "sourceName", "state"], ["sceneName", "sourceName", "visible"]],
        });
        t.Add(new SbSubActionRule("ObsSetBrowserUrl", "obs.setBrowserUrl", ["ObsSetBrowserSource", "ObsSetBrowserUrl", "38"],
            (s, c) => Obs(Step("obs.setBrowserUrl", s, ("input", Str(s, "sourceName", "source") ?? ""), ("url", Str(s, "url") ?? "")), s, c)));
        t.Add(new SbSubActionRule("ObsSetText", "obs.setText", ["ObsSetGdiText", "ObsSetText", "39"],
            (s, c) => Obs(Step("obs.setText", s, ("input", Str(s, "sourceName", "source") ?? ""), ("text", Str(s, "text") ?? "")), s, c)));
        t.Add(new SbSubActionRule("ObsSetFilterState", "obs.setFilterState", ["ObsSetSceneFilterState", "47"],
            (s, c) => Obs(Step("obs.setFilterState", s, ("source", Str(s, "sceneName", "sourceName") ?? ""), ("filter", Str(s, "filterName") ?? ""),
                ("state", FilterState(s))), s, c)));
        t.Add(new SbSubActionRule("ObsSetSourceFilterState", "obs.setFilterState", ["ObsSetSourceFilterState", "35"],
            (s, c) => Obs(Step("obs.setFilterState", s, ("source", Str(s, "sourceName") ?? ""), ("filter", Str(s, "filterName") ?? ""),
                ("state", FilterState(s))), s, c)));
        t.Add(new SbSubActionRule("ObsSetMediaFile", "obs.setMediaFile", ["ObsSetMediaSourceFile", "322"],
            (s, c) => Obs(Step("obs.setMediaFile", s, ("input", Str(s, "sourceName") ?? ""), ("file", Str(s, "fileName", "file") ?? "")), s, c)));
        t.Add(new SbSubActionRule("ObsSetImageFile", "obs.setImageFile", ["ObsSetImageSourceFile", "323"],
            (s, c) => Obs(Step("obs.setImageFile", s, ("input", Str(s, "sourceName") ?? ""), ("file", Str(s, "fileName", "file") ?? "")), s, c)));
        t.Add(new SbSubActionRule("ObsRaw", "obs.rawRequest", ["ObsRaw", "ObsSendRaw", "46"], MapObsRaw));
        t.Add(new SbSubActionRule("ObsRecordChapter", "obs.createRecordChapter", ["ObsCreateRecordChapter", "329"],
            (s, c) => Obs(Step("obs.createRecordChapter", s, ("chapterName", Str(s, "chapterName", "name") ?? "")), s, c)));

        // ---------------------------------------------------------------- Stream Deck
        t.Add(new SbSubActionRule("StreamDeckSetTitle", "streamDeck.setTitle", ["StreamDeckSetTitle", "11001"],
            (s, _) => Step("streamDeck.setTitle", s, ("buttonId", Str(s, "buttonId") ?? ""), ("title", Str(s, "title") ?? ""), ("state", DeckState(s)))));
        t.Add(new SbSubActionRule("StreamDeckSetBackground", "streamDeck.setBackground", ["StreamDeckSetBackground", "11002"], (s, c) =>
        {
            if (Str(s, "color") is { Length: > 0 } color && string.IsNullOrEmpty(Str(s, "image")))
                c.Warn($"'{c.ActionName}': a Stream Deck background colour ({color}) was dropped; Cuebot sets key images only.");
            return Step("streamDeck.setBackground", s, ("buttonId", Str(s, "buttonId") ?? ""), ("image", Str(s, "image") ?? ""), ("state", DeckState(s)));
        }));
        t.Add(new SbSubActionRule("StreamDeckSetValue", "streamDeck.setValue", ["StreamDeckSetValue", "11008"],
            (s, _) => Step("streamDeck.setValue", s, ("buttonId", Str(s, "buttonId") ?? ""), ("value", Str(s, "value") ?? ""))));

        AddSubActions(t);
        AddTriggers(t);
        // A rule is tentative when the Streamer.bot name of one of its codes is only likely or unsure.
        for (var i = 0; i < t._subActions.Count; i++)
            t._subActions[i] = t._subActions[i] with { Tentative = t._subActions[i].TypeKeys.Any(TentativeSubActionCodes.Contains) };
        return t;
    }

    // ---- helpers shared by the rules ----

    /// <summary>A new step with the source's enabled flag and weight, plus the given parameters.</summary>
    public static SubActionDefinition Step(string type, JsonObject source, params (string Key, string Value)[] parameters)
    {
        var step = new SubActionDefinition { Type = type, Enabled = Bool(source, "enabled") ?? true };
        if (Num(source, "weight") is { } w && w >= 0) step.Weight = w;
        foreach (var (key, value) in parameters) step.Parameters[key] = value;
        return step;
    }

    public static TriggerDefinition Trigger(string type, JsonObject source, params (string Key, string Value)[] settings)
    {
        var trigger = new TriggerDefinition { Type = type, Enabled = Bool(source, "enabled") ?? true };
        foreach (var (key, value) in settings) trigger.Settings[key] = value;
        return trigger;
    }

    /// <summary>Nested children of a group-like sub-action, if the export uses the nested layout.</summary>
    public static JsonArray? ChildArray(JsonObject source) => Arr(source, "subActions", "actions", "children", "items");

    private static string Flag(bool value) => value ? "true" : "false";

    /// <summary>A real id: not empty and not the all-zero GUID Streamer.bot writes for "none".</summary>
    internal static bool IsRealId(string? value) =>
        !string.IsNullOrWhiteSpace(value) && !(Guid.TryParse(value, out var g) && g == Guid.Empty);

    /// <summary>Streamer.bot stores volume as a fraction (1 = 100 %, a little above 1 boosts); Cuebot uses 0–100.</summary>
    private static string Volume(JsonObject s)
    {
        var volume = Num(s, "volume") ?? 1;
        if (volume <= 5) volume *= 100;
        return Text(Math.Round(Math.Clamp(volume, 0, 100)));
    }

    /// <summary>
    /// "Sound Name" tags the sound for Stop Sound Playback (or names it after its file); a specific output device is
    /// chosen by id, which Cuebot cannot match, so the default device is used.
    /// </summary>
    private static (string, string)[] SoundOptions(JsonObject s, ISbMapContext c)
    {
        if (IsRealId(Str(s, "device")))
            c.Warn($"'{c.ActionName}': plays on audio device id {Str(s, "device")}; it was set to the default device.");
        return [("device", ""), ("tag", Str(s, "soundName") ?? ""), ("useFileName", Flag(Bool(s, "useFileNameAsName") ?? false))];
    }

    /// <summary>"useBot" (newer exports) picks the account; "preferredAccount" (older ones) is not decoded, so Default.</summary>
    private static string SendAs(JsonObject s) =>
        Bool(s, "useBot", "bot", "sendAsBot") switch { true => "Bot", false => "Broadcaster", _ => "Default" };

    /// <summary>
    /// A "User Login" field. Older exports sometimes hold a bare argument name ("user"); those become %user%.
    /// </summary>
    private static string UserLogin(JsonObject s, ISbMapContext c, string fallback = "")
    {
        var login = Str(s, "userLogin", "user", "userName") ?? fallback;
        if (login is "user" or "userName" or "targetUser" or "targetUserName" or "input0")
        {
            c.Warn($"'{c.ActionName}': a user field held the bare name '{login}'; it was imported as %{login}%. Please check.");
            return $"%{login}%";
        }
        return login;
    }

    /// <summary>
    /// Whose info a "… for Target" step fetches: "userLogin" in newer exports; older ones use a source type, seen as
    /// 0 = the broadcaster (Get User Info only), 1 = the triggering user, 2 = the first word of the input and
    /// 3 = the argument named in "variable".
    /// </summary>
    private static string TargetUser(JsonObject s, ISbMapContext c)
    {
        if (Str(s, "userLogin") is not null) return UserLogin(s, c);
        var variable = Str(s, "variable") ?? "";
        return Normalize(Str(s, "sourceType")) switch
        {
            "" or "1" => "",
            "2" => "%input0%",
            "3" when variable.Length > 0 => variable.Contains('%') ? variable : $"%{variable}%",
            var other => throw new SbMappingException($"user source type '{other}' is not known"),
        };
    }

    /// <summary>Filter state in the documented order: 0 on (visible), 1 off (hidden), 2 toggle.</summary>
    private static string FilterState(JsonObject s) => Normalize(Str(s, "state")) switch
    {
        "0" => "Enable",
        "1" => "Disable",
        "2" => "Toggle",
        var other => throw new SbMappingException($"filter state '{other}' is not known"),
    };

    private static string DeckState(JsonObject s) => Str(s, "state") is { Length: > 0 } state and ("0" or "1") ? state : "Current";

    /// <summary>OBS connections are referenced by id, which a Cuebot profile cannot resolve; the default one is used.</summary>
    private static SubActionDefinition Obs(SubActionDefinition step, JsonObject s, ISbMapContext c)
    {
        ObsConnection(s, c);
        step.Parameters["connection"] = "";
        return step;
    }

    private static void ObsConnection(JsonObject s, ISbMapContext c)
    {
        if (IsRealId(Str(s, "connectionId", "obsId")))
            c.Warn($"'{c.ActionName}': uses a specific OBS connection (id {Str(s, "connectionId", "obsId")}); it was set to the default connection.");
    }

    private static SubActionDefinition MapDelay(JsonObject s, ISbMapContext c)
    {
        var raw = Str(s, "delay", "value", "milliseconds", "minDelay", "min") ?? "1000";
        var min = Num(s, "delay", "value", "milliseconds", "minDelay", "min");
        var max = Num(s, "maxDelay", "max", "maxValue");
        // A variable or inline function ("%adLengthMs%") is kept; Cuebot resolves it when the step runs.
        var step = Step("core.delay", s, ("milliseconds", min is { } ms ? Text(Math.Max(0, ms)) : raw));
        // "Random" waits a random time between the delay and "To".
        if (max is { } m && min is { } lo && m > lo && Bool(s, "random") != false)
        {
            step.Parameters["random"] = "true";
            step.Parameters["to"] = Text(m);
        }
        return step;
    }

    private static SubActionDefinition MapSetGlobal(JsonObject s, ISbMapContext c)
    {
        var name = Str(s, "variableName", "name", "variable") ?? "";
        var value = Str(s, "value") ?? "";
        // Numeric exports: "source" 0 = copy the argument named in Value, 1 = the value itself, 2 = increment and
        // 3 = decrement by Value (empty = 1).
        var mode = "Value";
        switch (Normalize(Str(s, "source")))
        {
            case "" or "1" or "value": break;
            case "0" or "argument": value = value.Length == 0 || value.Contains('%') ? value : $"%{value}%"; break;
            case "2" or "increment": mode = "Increment"; break;
            case "3" or "decrement": mode = "Decrement"; break;
            case var other: throw new SbMappingException($"value source '{other}' is not known");
        }
        var persisted = ("persisted", Flag(Bool(s, "persisted", "persist") ?? true));
        var autoType = ("autoType", Flag(Bool(s, "autoType") ?? false));
        return UserScope(Str(s, "destination")) switch
        {
            null => Step("core.setGlobal", s, ("name", name), ("mode", mode), ("value", value), persisted, autoType),
            var user => Step("core.setUserVariable", s, [("name", name), .. user, ("mode", mode), ("value", value), persisted, ("autoType", "true")]),
        };
    }

    private static SubActionDefinition MapGetGlobal(JsonObject s, ISbMapContext c)
    {
        var name = Str(s, "variableName", "name", "variable") ?? "";
        var destination = ("destination", Str(s, "destinationVariable", "destination", "argumentName") ?? "");
        var fallback = ("defaultValue", Str(s, "defaultValue", "default") ?? "");
        return UserScope(Str(s, "source")) switch
        {
            null => Step("core.getGlobal", s, ("name", name), destination, fallback, ("autoType", "true")),
            var user => Step("core.getUserVariable", s, [("name", name), .. user, destination, fallback, ("autoType", "true")]),
        };
    }

    /// <summary>
    /// Global variable scope ("source" of Get, "destination" of Set): 0 = global (null here), 1 = the user who ran
    /// the action, 2 = the target user (%targetUser%), in the documented order.
    /// </summary>
    private static (string, string)[]? UserScope(string? scope) => Normalize(scope) switch
    {
        "" or "0" or "global" => null,
        "1" or "user" or "userredeemer" => [("platform", ""), ("userId", ""), ("userName", "")],
        "2" or "usertarget" => [("platform", "twitch"), ("userId", "%targetUserId%"), ("userName", "%targetUser%")],
        var other => throw new SbMappingException($"global variable scope '{other}' is not known"),
    };

    private static SubActionDefinition MapFetchUrl(JsonObject s, ISbMapContext c)
    {
        var headers = new StringBuilder();
        var headerNode = Get(s, "headers");
        if (headerNode is JsonValue v && AsString(v) is { Length: > 0 } text)
        {
            try
            {
                headerNode = JsonNode.Parse(text);
            }
            catch (JsonException)
            {
                headerNode = null;
            }
        }
        if (headerNode is JsonObject o)
            foreach (var (key, value) in o)
                if (AsString(value) is { } header) headers.Append(key).Append(": ").Append(header).Append('\n');
        return Step("core.httpRequest", s, ("method", "GET"), ("url", Str(s, "url") ?? ""), ("headers", headers.ToString().TrimEnd('\n')),
            ("variable", Str(s, "variableName") ?? "httpResponse"), ("parseJson", Flag(Bool(s, "parseAsJson") == true)));
    }

    private static SubActionDefinition MapObsRaw(JsonObject s, ISbMapContext c)
    {
        JsonObject? raw = null;
        try
        {
            raw = Str(s, "raw", "json", "request") is { Length: > 0 } text ? JsonNode.Parse(text) as JsonObject : null;
        }
        catch (JsonException)
        {
        }
        var empty = string.IsNullOrWhiteSpace(Str(s, "raw", "json", "request"));
        var type = Str(raw, "requestType") ?? (empty ? "" : throw new SbMappingException("the raw request has no requestType"));
        var prefix = Str(s, "prefix") ?? "";
        if (empty)
        {
            c.Warn($"'{c.ActionName}': an OBS raw step holds no request; it was imported disabled.");
            var blank = Obs(Step("obs.rawRequest", s, ("requestType", ""), ("requestData", ""), ("resultArgument", prefix.Length > 0 ? prefix : "obsResult")), s, c);
            blank.Enabled = false;
            return blank;
        }
        if (Bool(s, "resultsToArgs") == true)
            c.Warn($"'{c.ActionName}': an OBS raw request spread its result into %{prefix}.…% arguments; Cuebot stores the response JSON in %{(prefix.Length > 0 ? prefix : "obsResult")}%.");
        return Obs(Step("obs.rawRequest", s, ("requestType", type), ("requestData", Get(raw, "requestData")?.ToJsonString() ?? ""),
            ("resultArgument", prefix.Length > 0 ? prefix : "obsResult")), s, c);
    }

    private static string VisibilityState(JsonObject s, ISbMapContext c)
    {
        if (Bool(s, "visible") is { } visible) return visible ? "Visible" : "Hidden";
        return Normalize(Str(s, "state")) switch
        {
            // Numeric exports use the documented order (Visible, Hidden, Toggle); public alert exports confirm 0/1.
            "visible" or "show" or "true" or "on" or "0" => "Visible",
            "hidden" or "hide" or "false" or "off" or "1" => "Hidden",
            _ => "Toggle",
        };
    }

    /// <summary>Level names, or the numeric levels of the exports (0 verbose, 1 debug, 2 info, 3 warning, 4 error).</summary>
    private static string LogLevel(string? level) => Normalize(level) switch
    {
        "debug" or "verbose" or "0" or "1" => "Debug",
        "warn" or "warning" or "3" => "Warning",
        "error" or "fatal" or "4" or "5" => "Error",
        _ => "Info",
    };

    /// <summary>Streamer.bot operator names → Cuebot <see cref="Conditions.Operators"/>.</summary>
    private static readonly Dictionary<string, string> Operators = new()
    {
        ["equals"] = "equals", ["equal"] = "equals", ["eq"] = "equals", ["isequal"] = "equals",
        ["notequals"] = "not equals", ["notequal"] = "not equals", ["ne"] = "not equals", ["doesnotequal"] = "not equals",
        ["contains"] = "contains", ["doesnotcontain"] = "does not contain", ["notcontains"] = "does not contain",
        ["startswith"] = "starts with", ["endswith"] = "ends with",
        ["greaterthan"] = "greater than", ["gt"] = "greater than", ["lessthan"] = "less than", ["lt"] = "less than",
        ["isnullorempty"] = "is empty", ["isempty"] = "is empty", ["isnotnullorempty"] = "is not empty", ["isnotempty"] = "is not empty",
        ["regex"] = "matches regex", ["matchesregex"] = "matches regex", ["regexmatch"] = "matches regex",
    };

    /// <summary>Marks "Does Not Exist", which needs its own left/right form.</summary>
    private const string NotExists = "does not exist";

    /// <summary>
    /// If/Else operator codes, in the documented order (Equals, Not Equals, Contains, Regex Match, Less Than,
    /// Greater Than, Does Not Exist, Equals ignore case, Not Equals ignore case, Is Null or Empty). Public exports
    /// confirm 0, 1, 2, 3, 4, 6, 8 and 9 from how they are used. Cuebot compares text case-insensitively.
    /// </summary>
    private static string? IfOperator(int code) => code switch
    {
        0 or 7 => "equals",
        1 or 8 => "not equals",
        2 => "contains",
        3 => "matches regex",
        4 => "less than",
        5 => "greater than",
        6 => NotExists,
        9 => "is empty",
        _ => null,
    };

    private static SubActionDefinition MapIf(JsonObject s, ISbMapContext c)
    {
        var op = Str(s, "operation", "operator", "comparison") ?? "equals";
        var mapped = int.TryParse(op, NumberStyles.Integer, CultureInfo.InvariantCulture, out var code)
            ? IfOperator(code)
            : Operators.GetValueOrDefault(Normalize(op));
        if (mapped is null) c.Warn($"'{c.ActionName}': if/else operator '{op}' is not known; imported as 'equals' and disabled. Please check it.");

        // Newer exports: "input" holds text such as "%user%". Older ones: "variableName" holds a bare argument name.
        var left = Str(s, "input", "left", "variable");
        if (left is null && Str(s, "variableName") is { } name) left = name.Contains('%') ? name : $"%{name}%";
        left ??= "";
        var right = Str(s, "value", "right", "compareTo") ?? "";
        if (mapped == NotExists)
        {
            // Does Not Exist takes the argument's name. $parse(name)$ stays as written when the name is unknown, so it
            // equals the escaped literal exactly when the argument does not exist.
            var bare = left.Trim().Trim('%');
            (left, mapped, right) = ($"$parse({bare})$", "equals", $"\\$parse({bare})$");
        }
        var step = Step("core.if", s, ("left", left), ("operator", mapped ?? "equals"), ("right", right));
        if (mapped is null) step.Enabled = false;

        var children = ChildArray(s);
        var trueNode = Objects(children).FirstOrDefault(n => StreamerBotMappingTable.TypeKeyOf(n) == IfTrueBranchCode);
        var falseNode = Objects(children).FirstOrDefault(n => StreamerBotMappingTable.TypeKeyOf(n) == IfFalseBranchCode);
        step.Branches["True"] = c.MapChildren(trueNode is null ? Arr(s, "true", "trueActions", "trueSubActions", "then", "thenActions") : ChildArray(trueNode));
        step.Branches["False"] = c.MapChildren(falseNode is null ? Arr(s, "false", "falseActions", "falseSubActions", "else", "elseActions") : ChildArray(falseNode));

        // Older exports: an action to run for each outcome, then continue (0) or break (1).
        AddRunBranch(step.Branches["True"], Str(s, "trueAction", "trueActionId", "actionTrue", "actionId"),
            Bool(s, "trueRunImmediately", "runImmediately"), Str(s, "afterAction"), c);
        AddRunBranch(step.Branches["False"], Str(s, "falseAction", "falseActionId", "actionFalse", "elseActionId"),
            Bool(s, "falseRunImmediately", "elseRunImmediately"), Str(s, "elseAfterAction"), c);

        if (trueNode is null && falseNode is null
            && Get(s, "true", "trueActions", "trueSubActions", "then", "thenActions", "false", "falseActions", "falseSubActions", "else", "elseActions") is null
            && children is not null)
            step.Branches["True"].AddRange(c.MapChildren(children));
        return step;
    }

    private static void AddRunBranch(List<SubActionDefinition> branch, string? action, bool? wait, string? after, ISbMapContext c)
    {
        if (IsRealId(action))
        {
            branch.Add(new SubActionDefinition
            {
                Type = "core.runAction",
                Parameters = { ["action"] = c.ActionRef(action), ["wait"] = Flag(wait ?? true) },
            });
        }
        switch (Normalize(after))
        {
            case "" or "0" or "continue": break;
            case "1" or "break": branch.Add(new SubActionDefinition { Type = "core.stop" }); break;
            default: c.Warn($"'{c.ActionName}': if/else 'then' option '{after}' is not known; it continues."); break;
        }
    }

    /// <summary>While: input/operation/value with the body in its children. Codes 0, 1 and 3 are known.</summary>
    private static SubActionDefinition MapWhile(JsonObject s, ISbMapContext c)
    {
        var op = Normalize(Str(s, "operation")) switch
        {
            "0" => "equals",
            "1" => "not equals",
            "2" => "greater than",
            "3" => "less than",
            var other => throw new SbMappingException($"while operator '{other}' is not known"),
        };
        var step = Step("core.while", s, ("left", Str(s, "input") ?? ""), ("operator", op), ("right", Str(s, "value") ?? ""), ("maxIterations", "10000"));
        step.Branches["Body"] = c.MapChildren(ChildArray(s));
        return step;
    }

    private static SubActionDefinition MapCode(JsonObject s, ISbMapContext c)
    {
        // Newer exports carry the source as text in "code"; older ones as base64 UTF-8 text in "byteCode".
        var code = Str(s, "code", "source", "sourceCode");
        if (string.IsNullOrEmpty(code) && Str(s, "byteCode") is { Length: > 0 } encoded)
        {
            try
            {
                code = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            }
            catch (FormatException)
            {
                code = null;
            }
        }
        // A step that never had any code (a freshly added one) imports empty; code that is there but unreadable does not.
        if (string.IsNullOrEmpty(code) && (!string.IsNullOrEmpty(Str(s, "byteCode")) || Has(s, "code"))) throw new SbMappingException("the C# code could not be read");
        code ??= "";
        c.Warn($"'{c.ActionName}': contains C# code. The action was imported disabled; read the code before enabling it.");
        var step = Step("code.csharp", s, ("code", code));
        if (Str(s, "name") is { Length: > 0 } name) step.Parameters["name"] = name;
        if (Str(s, "description") is { Length: > 0 } description) step.Parameters["description"] = description;
        CodeOptions(s, step);
        CodeReferences(s, step, code, c);
        if (Bool(s, "precompile") == true)
            c.Warn($"'{c.ActionName}': a C# step precompiles at startup; that starts once you enable the action.");
        return step;
    }

    /// <summary>
    /// Streamer.bot's "references" list mostly names the .NET Framework and the assemblies Streamer.bot ships with
    /// (mscorlib.dll, .\Newtonsoft.Json.dll, ...); Cuebot always references the framework, so those are dropped.
    /// A DLL the user added (in Streamer.bot's dlls folder, or anywhere else) is kept under the step's References:
    /// a full path as is, a path relative to Streamer.bot's folder as a comment to replace with the full path.
    /// </summary>
    private static void CodeReferences(JsonObject s, SubActionDefinition step, string code, ISbMapContext c)
    {
        var all = Arr(s, "references")?.Select(AsString).OfType<string>().Select(r => r.Trim()).Where(r => r.Length > 0).ToList() ?? [];
        var lines = new List<string>();
        var custom = new List<string>();
        foreach (var reference in all.Where(IsCustomReference))
        {
            var file = reference.Replace('\\', '/').Split('/')[^1];
            custom.Add(file);
            lines.Add(Path.IsPathFullyQualified(reference)
                ? reference
                : $"# {file} (was {reference} in Streamer.bot): replace this line with the full path to your copy");
        }
        if (lines.Count > 0)
        {
            step.Parameters["references"] = string.Join("\n", lines);
            c.Warn($"'{c.ActionName}': its C# code uses {string.Join(", ", custom)}; check the paths under References.");
        }
        if (all.Any(r => r.EndsWith("System.Windows.Forms.dll", StringComparison.OrdinalIgnoreCase) || r.EndsWith("PresentationFramework.dll", StringComparison.OrdinalIgnoreCase))
            || code.Contains("System.Windows.Forms", StringComparison.Ordinal) || code.Contains("System.Windows.Controls", StringComparison.Ordinal))
            c.Warn($"'{c.ActionName}': its C# code uses Windows Forms or WPF, which Cuebot does not provide; it will not compile until that part is rewritten.");
    }

    /// <summary>True for a DLL the user added, false for the framework and the assemblies Streamer.bot ships with.</summary>
    private static bool IsCustomReference(string reference)
    {
        var path = reference.Replace('/', '\\');
        if (path.Contains(@"\Microsoft.NET\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\Reference Assemblies\", StringComparison.OrdinalIgnoreCase)
            || path.Contains(@"\Windows\assembly\", StringComparison.OrdinalIgnoreCase)) return false;
        // A bare name or a file in Streamer.bot's own folder ships with Streamer.bot or .NET; its "dlls" folder is the user's.
        var folder = path.Contains('\\') ? path[..path.LastIndexOf('\\')] : "";
        return folder.EndsWith(@"\dlls", StringComparison.OrdinalIgnoreCase) || IsAbsolute(reference);
    }

    // A Windows path (C:\ or \\server) counts even on Linux, where Path.IsPathFullyQualified says no.
    private static bool IsAbsolute(string path) =>
        Path.IsPathFullyQualified(path) || (path.Length > 2 && char.IsAsciiLetter(path[0]) && path[1] == ':' && path[2] is '\\' or '/') || path.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>"Precompile on Application Start", "Delayed Start" and "Save Result to Variable" of C# steps.</summary>
    private static void CodeOptions(JsonObject s, SubActionDefinition step)
    {
        if (Bool(s, "precompile") == true) step.Parameters["precompile"] = "true";
        if (Bool(s, "delayStart", "delayedStart") == true) step.Parameters["delayedStart"] = "true";
        if (Bool(s, "saveResultToVariable") == true)
        {
            step.Parameters["saveResult"] = "true";
            step.Parameters["resultVariable"] = Str(s, "saveToVariable", "variableName") ?? "";
        }
    }

    /// <summary>"Execute C# Method": a small script calling <c>CPH.ExecuteMethod</c> on the referenced code.</summary>
    private static SubActionDefinition MapExecuteMethod(JsonObject s, ISbMapContext c)
    {
        var method = Str(s, "method") ?? "";
        var unset = method.Length == 0;
        if (unset)
        {
            c.Warn($"'{c.ActionName}': an Execute C# Method step names no method; it was imported disabled calling Execute.");
            method = "Execute";
        }
        var target = IsRealId(Str(s, "executeCodeId", "codeId")) ? c.CodeRef(Str(s, "executeCodeId", "codeId")) : "";
        c.Warn($"'{c.ActionName}': contains C# code. The action was imported disabled; read the code before enabling it.");
        var code = $$"""
            using System;

            public class CPHInline
            {
                public bool Execute()
                {
                    // Imported from a Streamer.bot "Execute C# Method" sub-action.
                    return CPH.ExecuteMethod({{Literal(target)}}, {{Literal(method)}});
                }
            }
            """;
        var step = Step("code.csharp", s, ("code", code));
        if (unset) step.Enabled = false;
        CodeOptions(s, step);
        return step;
    }

    private static string Literal(string text) => JsonSerializer.Serialize(text); // a valid C# string literal for any text
}
