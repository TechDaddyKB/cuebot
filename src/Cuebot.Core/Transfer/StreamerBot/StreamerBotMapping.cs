using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using static Cuebot.Core.Transfer.StreamerBot.SbJson;

namespace Cuebot.Core.Transfer.StreamerBot;

/// <summary>What a mapping function can ask the importer for while converting one item.</summary>
public interface ISbMapContext
{
    /// <summary>The Cuebot action being built (for messages).</summary>
    string ActionName { get; }

    /// <summary>Converts a nested list of Streamer.bot sub-actions (children of a group or an if/else branch).</summary>
    List<SubActionDefinition> MapChildren(JsonArray? children);

    /// <summary>Name of the imported action with this Streamer.bot id (or the text itself when unknown).</summary>
    string ActionRef(string? idOrName);

    /// <summary>The Cuebot reference (new id as text) for a Streamer.bot command id, or the text itself.</summary>
    string CommandRef(string? idOrName);

    /// <summary>Name of the imported timer with this Streamer.bot id (or the text itself).</summary>
    string TimerRef(string? idOrName);

    void Warn(string message);
}

/// <summary>Converts one Streamer.bot sub-action object into a Cuebot step.</summary>
public delegate SubActionDefinition SbSubActionMap(JsonObject source, ISbMapContext context);

/// <summary>Converts one Streamer.bot trigger object into a Cuebot trigger.</summary>
public delegate TriggerDefinition SbTriggerMap(JsonObject source, ISbMapContext context);

/// <summary>
/// One mapping rule. A Streamer.bot item matches when its type key (the "type" value, or the class name in a
/// "$type" field, compared after <see cref="SbJson.Normalize"/>) is one of <see cref="TypeKeys"/>, or a user
/// alias points at <see cref="Name"/>. Failing that, the importer may infer the rule from the item's fields: every
/// field of one of the <see cref="Signatures"/> must be present and none of <see cref="Excludes"/>.
/// </summary>
public abstract record SbRule(string Name, string TargetType, IReadOnlyList<string> TypeKeys)
{
    public IReadOnlyList<IReadOnlyList<string>> Signatures { get; init; } = [];
    public IReadOnlyList<string> Excludes { get; init; } = [];
}

public sealed record SbSubActionRule(string Name, string TargetType, IReadOnlyList<string> TypeKeys, SbSubActionMap Map)
    : SbRule(Name, TargetType, TypeKeys);

public sealed record SbTriggerRule(string Name, string TargetType, IReadOnlyList<string> TypeKeys, SbTriggerMap Map)
    : SbRule(Name, TargetType, TypeKeys);

/// <summary>
/// The pluggable Streamer.bot → Cuebot mapping table. <see cref="Default"/> holds the built-in rules (see the table
/// in docs and <see cref="Describe"/>). Streamer.bot stores sub-action and trigger types as numeric codes that are
/// not publicly documented, so the defaults match descriptive type names, CLR class names and field shapes; users
/// (or later releases) add numeric codes with <see cref="AddAliases"/> or a JSON file
/// (<c>{"subActions": {"&lt;code&gt;": "SendMessage"}, "triggers": {"&lt;code&gt;": "Follow"}}</c>).
/// </summary>
public sealed class StreamerBotMappingTable
{
    private readonly List<SbSubActionRule> _subActions = [];
    private readonly List<SbTriggerRule> _triggers = [];
    private readonly Dictionary<string, string> _subActionAliases = [];
    private readonly Dictionary<string, string> _triggerAliases = [];

    public IReadOnlyList<SbSubActionRule> SubActions => _subActions;
    public IReadOnlyList<SbTriggerRule> Triggers => _triggers;

    public StreamerBotMappingTable Add(SbSubActionRule rule)
    {
        _subActions.RemoveAll(r => r.Name == rule.Name);
        _subActions.Add(rule);
        return this;
    }

    public StreamerBotMappingTable Add(SbTriggerRule rule)
    {
        _triggers.RemoveAll(r => r.Name == rule.Name);
        _triggers.Add(rule);
        return this;
    }

    /// <summary>Maps extra type keys (e.g. numeric codes) to rule names.</summary>
    public StreamerBotMappingTable AddAliases(IReadOnlyDictionary<string, string>? subActions, IReadOnlyDictionary<string, string>? triggers)
    {
        foreach (var (key, rule) in subActions ?? new Dictionary<string, string>()) _subActionAliases[Normalize(key)] = rule;
        foreach (var (key, rule) in triggers ?? new Dictionary<string, string>()) _triggerAliases[Normalize(key)] = rule;
        return this;
    }

    /// <summary>Reads aliases from JSON (<c>{"subActions": {...}, "triggers": {...}}</c>). Bad JSON throws <see cref="TransferFormatException"/>.</summary>
    public StreamerBotMappingTable AddAliasesJson(string json)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject ?? throw new TransferFormatException("Mapping file must be a JSON object.");
            static Dictionary<string, string> Read(JsonObject? o) =>
                o?.Where(kv => AsString(kv.Value) is not null).ToDictionary(kv => kv.Key, kv => AsString(kv.Value)!) ?? [];
            return AddAliases(Read(Obj(root, "subActions")), Read(Obj(root, "triggers")));
        }
        catch (JsonException ex)
        {
            throw new TransferFormatException("Mapping file is not valid JSON: " + ex.Message, ex);
        }
    }

    /// <summary>Finds the rule for a sub-action. <paramref name="inferred"/> is true when only the field shape matched.</summary>
    public SbSubActionRule? FindSubAction(JsonObject source, out bool inferred) => Find(_subActions, _subActionAliases, source, out inferred);

    public SbTriggerRule? FindTrigger(JsonObject source, out bool inferred) => Find(_triggers, _triggerAliases, source, out inferred);

    /// <summary>The type key used for matching and reports: the "type" value, else the "$type" class name.</summary>
    public static string TypeKeyOf(JsonObject source)
    {
        var type = Str(source, "type", "subActionType", "triggerType", "kind");
        if (!string.IsNullOrWhiteSpace(type)) return type.Trim();
        return ClassName(source) ?? "";
    }

    private static string? ClassName(JsonObject source)
    {
        if (Str(source, "$type") is not { Length: > 0 } clr) return null;
        var name = clr.Split(',')[0].Trim();
        name = name[(name.LastIndexOf('.') + 1)..];
        name = name[(name.LastIndexOf('+') + 1)..];
        foreach (var suffix in new[] { "SubAction", "Trigger", "Action" })
            if (name.Length > suffix.Length && name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return name[..^suffix.Length];
        return name;
    }

    private static T? Find<T>(List<T> rules, Dictionary<string, string> aliases, JsonObject source, out bool inferred) where T : SbRule
    {
        inferred = false;
        foreach (var key in new[] { Str(source, "type", "subActionType", "triggerType", "kind"), ClassName(source) })
        {
            var norm = Normalize(key);
            if (norm.Length == 0) continue;
            if (aliases.TryGetValue(norm, out var alias) && rules.FirstOrDefault(r => r.Name == alias) is { } aliased) return aliased;
            if (rules.FirstOrDefault(r => r.TypeKeys.Any(k => Normalize(k) == norm)) is { } byKey) return byKey;
        }
        var best = rules
            .SelectMany(r => r.Signatures.Select(sig => (Rule: r, Sig: sig)))
            .Where(x => x.Sig.Count > 0 && x.Sig.All(f => Has(source, f)) && !x.Rule.Excludes.Any(f => Has(source, f)))
            .OrderByDescending(x => x.Sig.Count)
            .FirstOrDefault();
        inferred = best.Rule is not null;
        return best.Rule;
    }

    /// <summary>"Name → target (type keys)" lines for documentation and the UI.</summary>
    public IEnumerable<string> Describe() =>
        _subActions.Select(r => $"Sub-action {r.Name} → {r.TargetType}")
            .Concat(_triggers.Select(r => $"Trigger {r.Name} → {r.TargetType}"));

    /// <summary>The built-in rules.</summary>
    public static StreamerBotMappingTable Default { get; } = CreateDefault();

    public static StreamerBotMappingTable CreateDefault()
    {
        var t = new StreamerBotMappingTable();

        // ---- sub-actions ----
        t.Add(new SbSubActionRule("SendMessage", "twitch.sendMessage", ["SendMessage", "TwitchSendMessage", "ChatSendMessage", "SendChatMessage"],
            (s, _) => Step("twitch.sendMessage", s, ("message", Str(s, "text", "message") ?? ""),
                ("sendAs", Bool(s, "useBot", "bot", "sendAsBot") switch { true => "Bot", false => "Broadcaster", _ => "Default" })))
        {
            Signatures = [["text", "useBot"]],
        });
        t.Add(new SbSubActionRule("Delay", "core.delay", ["Delay", "Wait"], (s, c) =>
        {
            var min = Num(s, "delay", "value", "milliseconds", "minDelay", "min") ?? 1000;
            var max = Num(s, "maxDelay", "max", "maxValue");
            if (max is { } m && m > min) c.Warn($"'{c.ActionName}': random delay {Text(min)}–{Text(m)} ms imported as a fixed {Text(min)} ms delay.");
            return Step("core.delay", s, ("milliseconds", Text(Math.Max(0, min))));
        })
        {
            Signatures = [["delay"], ["maxDelay"]],
        });
        t.Add(new SbSubActionRule("SetGlobal", "core.setGlobal", ["SetGlobal", "GlobalSet", "SetGlobalVariable", "GlobalVariableSet"],
            (s, _) => Step("core.setGlobal", s, ("name", Str(s, "variableName", "name", "variable") ?? ""), ("mode", "Value"),
                ("value", Str(s, "value") ?? ""), ("persisted", (Bool(s, "persisted", "persist") ?? true) ? "true" : "false"),
                ("autoType", (Bool(s, "autoType") ?? false) ? "true" : "false")))
        {
            Signatures = [["variableName", "value", "persisted"]],
        });
        t.Add(new SbSubActionRule("GetGlobal", "core.getGlobal", ["GetGlobal", "GlobalGet", "GetGlobalVariable", "GlobalVariableGet"],
            (s, _) => Step("core.getGlobal", s, ("name", Str(s, "variableName", "name", "variable") ?? ""),
                ("destination", Str(s, "destinationVariable", "destination", "argumentName") ?? ""),
                ("defaultValue", Str(s, "defaultValue", "default") ?? ""), ("autoType", "true")))
        {
            Signatures = [["variableName", "destinationVariable"]],
        });
        t.Add(new SbSubActionRule("SetArgument", "core.setArgument", ["SetArgument", "SetArg", "ArgumentSet"],
            (s, _) => Step("core.setArgument", s, ("name", Str(s, "variableName", "argumentName", "name") ?? ""), ("value", Str(s, "value") ?? ""),
                ("autoType", (Bool(s, "autoType") ?? false) ? "true" : "false"))));
        t.Add(new SbSubActionRule("PlaySound", "audio.playSound", ["PlaySound", "SoundPlay", "PlaySoundFile"], (s, _) =>
        {
            var volume = Num(s, "volume") ?? 1;
            if (volume <= 1) volume *= 100; // a 0–1 fraction (assumed) → Cuebot's 0–100
            return Step("audio.playSound", s, ("file", Str(s, "soundFile", "fileName", "file", "path") ?? ""),
                ("volume", Text(Math.Round(Math.Clamp(volume, 0, 100)))),
                ("wait", (Bool(s, "finishBeforeContinuing", "wait", "waitUntilFinished") ?? false) ? "true" : "false"));
        })
        {
            Signatures = [["soundFile"]],
        });
        t.Add(new SbSubActionRule("ObsSetScene", "obs.setScene", ["ObsSetScene", "SetScene", "ObsSetCurrentScene", "ObsScene"],
            (s, _) => Step("obs.setScene", s, ("scene", Str(s, "sceneName", "scene") ?? "")))
        {
            Signatures = [["sceneName"]],
            Excludes = ["sourceName", "filterName", "itemName", "inputName", "groupName"],
        });
        t.Add(new SbSubActionRule("ObsSetSourceVisibility", "obs.setSourceVisibility",
            ["ObsSetSourceVisibility", "ObsSourceVisibility", "SetSourceVisibility", "ObsSetSourceVisibilityState"],
            (s, c) => Step("obs.setSourceVisibility", s, ("scene", Str(s, "sceneName", "scene") ?? ""),
                ("source", Str(s, "sourceName", "source", "itemName") ?? ""), ("state", VisibilityState(s, c))))
        {
            Signatures = [["sceneName", "sourceName", "state"], ["sceneName", "sourceName", "visible"]],
        });
        t.Add(new SbSubActionRule("RunAction", "core.runAction", ["RunAction", "ActionRun", "RunActionById"],
            (s, c) => Step("core.runAction", s, ("action", c.ActionRef(Str(s, "actionId", "actionName", "action"))),
                ("wait", (Bool(s, "runImmediately", "wait") ?? true) ? "true" : "false")))
        {
            Signatures = [["actionId"]],
            Excludes = ["state", "enabled"],
        });
        t.Add(new SbSubActionRule("IfElse", "core.if", ["IfElse", "If", "Condition", "IfCondition"], MapIf)
        {
            Signatures = [["input", "operation", "value"]],
        });
        t.Add(new SbSubActionRule("ExecuteCode", "code.csharp", ["ExecuteCode", "ExecuteCSharpCode", "CSharpCode", "InlineCode", "ExecuteCSharp"], MapCode)
        {
            Signatures = [["byteCode"], ["code", "references"]],
        });
        t.Add(new SbSubActionRule("Group", "core.group", ["Group", "SubActionGroup", "ActionGroup"], (s, c) =>
        {
            var step = Step("core.group", s, ("name", Str(s, "name", "groupName") ?? "Group"),
                ("random", (Bool(s, "random") ?? false) ? "true" : "false"), ("color", Str(s, "color", "colour") ?? ""));
            step.Branches["Steps"] = c.MapChildren(ChildArray(s));
            return step;
        }));
        t.Add(new SbSubActionRule("Comment", "core.comment", ["Comment", "Note"],
            (s, _) => Step("core.comment", s, ("text", Str(s, "text", "comment", "value") ?? ""))));
        t.Add(new SbSubActionRule("LogMessage", "core.log", ["LogMessage", "Log", "WriteLog", "WriteToLog"],
            (s, _) => Step("core.log", s, ("message", Str(s, "message", "text", "value") ?? ""), ("level", LogLevel(Str(s, "logLevel", "level"))))));
        t.Add(new SbSubActionRule("Break", "core.stop", ["Break", "BreakAction", "StopAction", "Stop"], (s, _) => Step("core.stop", s)));

        // ---- triggers ----
        t.Add(new SbTriggerRule("Command", "command.triggered", ["Command", "CommandTriggered", "CommandTrigger"],
            (s, c) => Trigger("command.triggered", s, ("command", c.CommandRef(Str(s, "commandId", "command", "commandName")))))
        {
            Signatures = [["commandId"]],
        });
        t.Add(new SbTriggerRule("Follow", "twitch.follow", ["Follow", "TwitchFollow"], (s, _) => Trigger("twitch.follow", s)));
        t.Add(new SbTriggerRule("Sub", "twitch.sub", ["Sub", "Subscription", "TwitchSub", "TwitchSubscription"],
            (s, _) => Trigger("twitch.sub", s, ("tier", Tier(Str(s, "tier"))))));
        t.Add(new SbTriggerRule("ReSub", "twitch.resub", ["ReSub", "Resubscription", "TwitchReSub"], (s, _) => Trigger("twitch.resub", s)));
        t.Add(new SbTriggerRule("GiftSub", "twitch.giftSub", ["GiftSub", "TwitchGiftSub"], (s, _) => Trigger("twitch.giftSub", s)));
        t.Add(new SbTriggerRule("GiftBomb", "twitch.giftBomb", ["GiftBomb", "TwitchGiftBomb"], (s, _) => Trigger("twitch.giftBomb", s)));
        t.Add(new SbTriggerRule("Cheer", "twitch.cheer", ["Cheer", "TwitchCheer"],
            (s, _) => Trigger("twitch.cheer", s, ("minBits", Text(Math.Max(1, Num(s, "min", "minBits", "minimum") ?? 1))),
                ("maxBits", Num(s, "max", "maxBits", "maximum") is { } max and > 0 ? Text(max) : ""))));
        t.Add(new SbTriggerRule("Raid", "twitch.raid", ["Raid", "TwitchRaid"],
            (s, _) => Trigger("twitch.raid", s, ("minViewers", Text(Math.Max(0, Num(s, "min", "minViewers", "minimum") ?? 0))))));
        t.Add(new SbTriggerRule("RewardRedemption", "twitch.rewardRedemption",
            ["RewardRedemption", "ChannelPointReward", "TwitchRewardRedemption", "Reward"], (s, c) =>
            {
                var title = Str(s, "rewardName", "rewardTitle", "title", "name") ?? "";
                if (title.Length == 0 && Str(s, "rewardId") is { Length: > 0 } id)
                    c.Warn($"'{c.ActionName}': reward trigger names reward id {id} only; set the reward title in Cuebot.");
                return Trigger("twitch.rewardRedemption", s, ("rewardTitle", title));
            })
        {
            Signatures = [["rewardId"], ["rewardName"]],
        });
        t.Add(new SbTriggerRule("TimedAction", "core.timer", ["TimedAction", "TimedActions", "Timer", "TimerTrigger"],
            (s, c) => Trigger("core.timer", s, ("timer", c.TimerRef(Str(s, "timerId", "timer", "timerName")))))
        {
            Signatures = [["timerId"]],
        });
        t.Add(new SbTriggerRule("FirstWords", "twitch.firstWords", ["FirstWords", "FirstWord", "TwitchFirstWords"], (s, _) => Trigger("twitch.firstWords", s)));
        t.Add(new SbTriggerRule("ChatMessage", "twitch.chatMessage", ["ChatMessage", "TwitchChatMessage"], (s, _) => Trigger("twitch.chatMessage", s)));
        t.Add(new SbTriggerRule("StreamOnline", "twitch.streamOnline", ["StreamOnline", "TwitchStreamOnline"], (s, _) => Trigger("twitch.streamOnline", s)));
        t.Add(new SbTriggerRule("StreamOffline", "twitch.streamOffline", ["StreamOffline", "TwitchStreamOffline"], (s, _) => Trigger("twitch.streamOffline", s)));
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

    private static string VisibilityState(JsonObject s, ISbMapContext c)
    {
        if (Bool(s, "visible") is { } visible) return visible ? "Visible" : "Hidden";
        var state = Str(s, "state") ?? "";
        switch (Normalize(state))
        {
            case "visible" or "show" or "true" or "on": return "Visible";
            case "hidden" or "hide" or "false" or "off": return "Hidden";
            case "toggle": return "Toggle";
            // Numeric codes are assumed to follow the UI order (Visible, Hidden, Toggle); flagged for review.
            case "0": c.Warn($"'{c.ActionName}': source visibility state code 0 read as Visible; please check."); return "Visible";
            case "1": c.Warn($"'{c.ActionName}': source visibility state code 1 read as Hidden; please check."); return "Hidden";
            default: return "Toggle";
        }
    }

    private static string Tier(string? tier) => Normalize(tier) switch
    {
        "1000" or "1" or "tier1" or "prime" => "1",
        "2000" or "2" or "tier2" => "2",
        "3000" or "3" or "tier3" => "3",
        _ => "Any",
    };

    private static string LogLevel(string? level) => Normalize(level) switch
    {
        "debug" or "verbose" => "Debug",
        "warn" or "warning" => "Warning",
        "error" => "Error",
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

    private static SubActionDefinition MapIf(JsonObject s, ISbMapContext c)
    {
        var op = Str(s, "operation", "operator", "comparison") ?? "equals";
        var known = Operators.TryGetValue(Normalize(op), out var mapped);
        if (!known) c.Warn($"'{c.ActionName}': if/else operator '{op}' is not known; imported as 'equals' and disabled. Please check it.");
        var step = Step("core.if", s, ("left", Str(s, "input", "left", "variable") ?? ""), ("operator", mapped ?? "equals"),
            ("right", Str(s, "value", "right", "compareTo") ?? ""));
        if (!known) step.Enabled = false;

        // Nested layout: child lists per branch. Older layout: an action to run for each outcome.
        step.Branches["True"] = c.MapChildren(Arr(s, "true", "trueActions", "trueSubActions", "then", "thenActions"));
        step.Branches["False"] = c.MapChildren(Arr(s, "false", "falseActions", "falseSubActions", "else", "elseActions"));
        AddRunBranch(step.Branches["True"], Str(s, "trueAction", "trueActionId", "actionTrue"), Bool(s, "trueRunImmediately"), c);
        AddRunBranch(step.Branches["False"], Str(s, "falseAction", "falseActionId", "actionFalse"), Bool(s, "falseRunImmediately"), c);
        if (Get(s, "true", "trueActions", "trueSubActions", "then", "thenActions", "false", "falseActions", "falseSubActions", "else", "elseActions") is null
            && ChildArray(s) is { } flat)
            step.Branches["True"].AddRange(c.MapChildren(flat));
        return step;
    }

    private static void AddRunBranch(List<SubActionDefinition> branch, string? action, bool? wait, ISbMapContext c)
    {
        if (string.IsNullOrWhiteSpace(action) || action == Guid.Empty.ToString()) return;
        branch.Add(new SubActionDefinition
        {
            Type = "core.runAction",
            Parameters = { ["action"] = c.ActionRef(action), ["wait"] = (wait ?? true) ? "true" : "false" },
        });
    }

    private static SubActionDefinition MapCode(JsonObject s, ISbMapContext c)
    {
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
        if (string.IsNullOrEmpty(code)) throw new SbMappingException("the C# code could not be read");
        c.Warn($"'{c.ActionName}': contains C# code. The action was imported disabled; read the code before enabling it.");
        return Step("code.csharp", s, ("code", code));
    }
}

/// <summary>Thrown by a mapping when the item matched a rule but cannot be converted; the importer keeps a placeholder.</summary>
public sealed class SbMappingException(string message) : Exception(message);
