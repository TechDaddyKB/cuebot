using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Commands;
using static Cuebot.Core.Transfer.StreamerBot.SbJson;

namespace Cuebot.Core.Transfer.StreamerBot;

/// <summary>
/// Converts Streamer.bot export JSON (already decoded by <see cref="TransferCodec.Decode"/>) into a Cuebot
/// <see cref="TransferBundle"/> plus an <see cref="ImportReport"/>. Clean-room and tolerant: the layout handled is the
/// publicly described one (an optional "meta" header and a "data" object holding "actions", "queues", "commands" and
/// "timers"; sub-actions either nested or flat with "parentId"/"index"), with field names matched loosely. Items no
/// rule maps become disabled placeholder steps carrying the original type and JSON, so nothing is dropped silently.
/// Nothing is executed: code is copied as text and actions containing code are imported disabled.
/// </summary>
public sealed class StreamerBotImporter(StreamerBotMappingTable? table = null)
{
    public const string PlaceholderSource = "streamerbot";
    private const int MaxPlaceholderJson = 32 * 1024;
    private const string SyntheticGroupKey = "cuebotGroupFromNames";

    private readonly StreamerBotMappingTable _table = table ?? StreamerBotMappingTable.Default;

    public (TransferBundle Bundle, ImportReport Report) Convert(string json)
    {
        JsonObject root;
        try
        {
            root = JsonNode.Parse(json, null, TransferCodec.DocumentOptions) as JsonObject
                   ?? throw new TransferFormatException("The Streamer.bot export is not a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new TransferFormatException("The Streamer.bot export is not valid JSON: " + ex.Message, ex);
        }

        var run = new Run(_table);
        var bundle = run.Convert(root);
        if (bundle.IsEmpty) throw new TransferFormatException("No actions, commands, queues or timers were found in this Streamer.bot export.");
        return (bundle, run.Report);
    }

    private sealed class Run(StreamerBotMappingTable table) : ISbMapContext
    {
        private readonly Dictionary<string, (Guid Id, string Name)> _actions = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Guid> _commands = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _timers = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _queues = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _codeNames = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, int> _mapped = [];
        private Dictionary<string, List<JsonObject>>? _flatChildren;

        public ImportReport Report { get; } = new();
        public string ActionName { get; private set; } = "";

        public TransferBundle Convert(JsonObject root)
        {
            var data = Obj(root, "data") ?? root;
            var meta = Obj(root, "meta");
            var bundle = new TransferBundle
            {
                Header = new TransferHeader
                {
                    Name = Str(meta, "name") ?? "", Author = Str(meta, "author") ?? "", Description = Str(meta, "description") ?? "",
                    BundleVersion = Str(meta, "version") ?? "",
                    Generator = "Streamer.bot import" + (Str(root, "version") is { } v ? $" (export version {v}" +
                        (Str(root, "exportedFrom") is { } from ? $", from {from})" : ")") : ""),
                },
            };

            var actionNodes = Objects(Arr(data, "actions")).ToList();
            // A copied single action (no "data" wrapper, its own triggers/sub-actions at the top level).
            if (actionNodes.Count == 0 && Has(data, "triggers") && Str(data, "name") is not null) actionNodes.Add(data);

            foreach (var q in Objects(Arr(data, "queues", "actionQueues")))
            {
                var queue = new QueueDefinition { Id = IdOf(q), Name = Str(q, "name") ?? "Queue", Blocking = Bool(q, "blocking") ?? false };
                if (Str(q, "id") is { } raw) _queues[raw] = queue.Name;
                bundle.Queues.Add(queue);
            }

            // Ids first, so references resolve whatever the order.
            foreach (var a in actionNodes)
            {
                if (Str(a, "id") is { } raw) _actions[raw] = (IdOf(a), Str(a, "name") ?? "Imported action");
                IndexCode(Arr(a, "subActions", "actions"), Str(a, "name") ?? "Imported action");
            }
            var commandNodes = Objects(Arr(data, "commands")).ToList();
            foreach (var c in commandNodes)
                if (Str(c, "id") is { } raw) _commands[raw] = IdOf(c);
            var timerNodes = Objects(Arr(data, "timers", "timedActions")).ToList();
            foreach (var t in timerNodes)
                if (Str(t, "id") is { } raw) _timers[raw] = Str(t, "name") ?? "Timer";

            foreach (var a in actionNodes) bundle.Actions.Add(MapAction(a));
            foreach (var c in commandNodes) bundle.Commands.Add(MapCommand(c));
            foreach (var t in timerNodes) bundle.Timers.Add(MapTimer(t));

            foreach (var (line, count) in _mapped.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                Report.Mapped.Add(count > 1 ? $"{line} ×{count}" : line);
            if (bundle.Commands.Count > 0) Report.Mapped.Add($"Commands: {bundle.Commands.Count}");
            if (bundle.Timers.Count > 0) Report.Mapped.Add($"Timers: {bundle.Timers.Count}");
            if (bundle.Queues.Count > 0) Report.Mapped.Add($"Queues: {bundle.Queues.Count}");
            return bundle;
        }

        // ---- actions ----

        private ActionDefinition MapAction(JsonObject a)
        {
            var raw = Str(a, "id");
            var (id, name) = raw is not null && _actions.TryGetValue(raw, out var known) ? known : (IdOf(a), Str(a, "name") ?? "Imported action");
            ActionName = name;
            var action = new ActionDefinition
            {
                Id = id, Name = name, Group = Str(a, "group") ?? "", Enabled = Bool(a, "enabled") ?? true,
                Concurrent = Bool(a, "concurrent") ?? false, RandomAction = Bool(a, "randomAction", "random") ?? false,
                AlwaysRun = Bool(a, "alwaysRun") ?? false, ExcludeFromHistory = Bool(a, "excludeFromHistory") ?? false,
                Queue = QueueRef(Str(a, "queue", "queueId")),
            };

            var steps = GroupByName(a, Objects(Arr(a, "subActions", "actions")).ToList());
            _flatChildren = null;
            if (steps.Any(s => IsRealId(Str(s, "parentId"))))
            {
                // Flat layout: every sub-action in one list, children pointing at their parent.
                var ids = steps.Select(s => Str(s, "id")).Where(s => s is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
                _flatChildren = steps.Where(s => IsRealId(Str(s, "parentId")) && ids.Contains(Str(s, "parentId")!))
                    .GroupBy(s => Str(s, "parentId")!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);
                steps = steps.Where(s => !IsRealId(Str(s, "parentId")) || !ids.Contains(Str(s, "parentId")!)).ToList();
            }
            action.SubActions.AddRange(MapList(steps));

            var triggerPlaceholders = new List<SubActionDefinition>();
            foreach (var t in Objects(Arr(a, "triggers")))
            {
                var rule = table.FindTrigger(t, out var inferred);
                if (rule is not null)
                {
                    try
                    {
                        action.Triggers.Add(rule.Map(t, this));
                        Count(rule, "Trigger", inferred);
                        continue;
                    }
                    catch (Exception ex) when (ex is SbMappingException or FormatException or InvalidOperationException)
                    {
                        triggerPlaceholders.Add(Placeholder(t, "trigger", ": " + ex.Message));
                        continue;
                    }
                }
                triggerPlaceholders.Add(Placeholder(t, "trigger", ""));
            }
            action.SubActions.InsertRange(0, triggerPlaceholders);

            var all = TransferReferences.Walk(action.SubActions).ToList();
            if (all.Any(s => s.Type.StartsWith("code.", StringComparison.OrdinalIgnoreCase)))
                action.Enabled = false;
            if (all.Any(s => s.Type == "core.runProgram" && s.Enabled))
            {
                action.Enabled = false;
                Warn($"'{ActionName}': runs a program. The action was imported disabled; check the program before enabling it.");
            }
            return action;
        }

        /// <summary>
        /// Older exports keep every sub-action in one list; a sub-action inside a group names the group in its "group"
        /// field, and the action lists its groups in "actionGroups" (id, name, random, index). Groups and ungrouped
        /// sub-actions share one index order. Each group becomes a nested group node here.
        /// </summary>
        private List<JsonObject> GroupByName(JsonObject action, List<JsonObject> steps)
        {
            var groups = Objects(Arr(action, "actionGroups")).Where(g => !string.IsNullOrWhiteSpace(Str(g, "name"))).ToList();
            if (groups.Count == 0) return steps;
            var names = groups.Select(g => Str(g, "name")!).ToHashSet(StringComparer.Ordinal);
            var result = steps.Where(s => Str(s, "group") is not { } g || !names.Contains(g)).ToList();
            foreach (var g in groups.DistinctBy(g => Str(g, "name"), StringComparer.Ordinal))
            {
                var name = Str(g, "name")!;
                var children = new JsonArray();
                foreach (var child in steps.Where(s => Str(s, "group") == name)) children.Add(child.DeepClone());
                result.Add(new JsonObject
                {
                    ["type"] = "Group", ["name"] = name, ["random"] = Bool(g, "random") ?? false,
                    ["index"] = Int(g, "index") ?? int.MaxValue, ["enabled"] = true, ["subActions"] = children, [SyntheticGroupKey] = true,
                });
            }
            return result;
        }

        /// <summary>Remembers each "Execute C# Code" sub-action's name for "Execute C# Method" references.</summary>
        private void IndexCode(JsonArray? steps, string actionName)
        {
            foreach (var step in Objects(steps))
            {
                if (StreamerBotMappingTable.TypeKeyOf(step) == "99999" && Str(step, "id") is { } id)
                    _codeNames[id] = Str(step, "name") is { Length: > 0 } name ? name : actionName;
                IndexCode(StreamerBotMappingTable.ChildArray(step), actionName);
            }
        }

        private List<SubActionDefinition> MapList(IEnumerable<JsonObject> nodes)
        {
            var result = new List<SubActionDefinition>();
            foreach (var node in nodes.Select((n, i) => (n, i)).OrderBy(x => Int(x.n, "index") ?? int.MaxValue).ThenBy(x => x.i).Select(x => x.n))
                result.AddRange(MapStep(node));
            return result;
        }

        public List<SubActionDefinition> MapChildren(JsonArray? children) => children is null ? [] : MapList(Objects(children));

        /// <summary>One sub-action (plus, in the flat layout, children that could not be attached).</summary>
        private IEnumerable<SubActionDefinition> MapStep(JsonObject node)
        {
            List<JsonObject>? flat = null;
            if (_flatChildren is not null && Str(node, "id") is { } nodeId) flat = _flatChildren.GetValueOrDefault(nodeId);
            var rule = table.FindSubAction(node, out var inferred);
            SubActionDefinition step;
            if (rule is null)
            {
                var children = MapChildren(StreamerBotMappingTable.ChildArray(node));
                if (flat is not null) children.AddRange(MapList(flat));
                return [Placeholder(node, "sub-action", "", children)];
            }
            try
            {
                step = rule.Map(node, this);
            }
            catch (Exception ex) when (ex is SbMappingException or FormatException or InvalidOperationException)
            {
                var children = MapChildren(StreamerBotMappingTable.ChildArray(node));
                if (flat is not null) children.AddRange(MapList(flat));
                return [Placeholder(node, "sub-action", ": " + ex.Message, children)];
            }
            if (!Has(node, SyntheticGroupKey)) Count(rule, "Sub-action", inferred);
            if (flat is null) return [step];

            switch (step.Type)
            {
                case GroupSubAction.Id:
                    step.Branch(GroupSubAction.Steps).AddRange(MapList(flat));
                    return [step];
                case "core.if":
                    step.Branch("True").AddRange(MapList(flat.Where(k => !IsFalseBranch(k))));
                    step.Branch("False").AddRange(MapList(flat.Where(IsFalseBranch)));
                    return [step];
                default:
                    Warn($"'{ActionName}': sub-action children of a {rule.Name} step were placed after it.");
                    return [step, .. MapList(flat)];
            }
        }

        private static bool IsFalseBranch(JsonObject node) =>
            Normalize(Str(node, "branch", "conditionBranch")) is "false" or "else" or "1"
            || Bool(node, "isElse", "elseBranch") == true;

        private SubActionDefinition Placeholder(JsonObject node, string kind, string reason, List<SubActionDefinition>? children = null)
        {
            var type = StreamerBotMappingTable.TypeKeyOf(node);
            if (type.Length == 0) type = "unknown";
            var copy = (JsonObject)node.DeepClone();
            foreach (var key in new[] { "subActions", "actions", "children", "items", "true", "false", "trueActions", "falseActions" })
                foreach (var name in copy.Select(kv => kv.Key).Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToArray())
                    copy.Remove(name);
            var json = copy.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            if (json.Length > MaxPlaceholderJson) json = json[..MaxPlaceholderJson] + "\n… (truncated)";
            Report.Placeholders.Add(new ImportPlaceholder(ActionName, kind, type, json));

            var title = $"Streamer.bot {kind} not converted (type {type}){reason}";
            var step = children is { Count: > 0 }
                ? new SubActionDefinition
                {
                    Type = GroupSubAction.Id, Enabled = false,
                    Parameters = { ["name"] = title, ["random"] = "false", ["color"] = "" },
                    Branches = { [GroupSubAction.Steps] = children },
                }
                : new SubActionDefinition
                {
                    Type = CommentSubAction.Id, Enabled = false,
                    Parameters = { ["text"] = $"{title}. Original JSON:\n{json}" },
                };
            step.Parameters[BundleImporter.PlaceholderMarkerKey] = PlaceholderSource;
            step.Parameters[BundleImporter.PlaceholderTypeKey] = type;
            step.Parameters["sourceJson"] = json;
            return step;
        }

        private void Count(SbRule rule, string kind, bool inferred)
        {
            var line = $"{kind} {rule.Name} → {rule.TargetType}" + (inferred ? " (recognised by its fields; please check)"
                : rule.Tentative ? " (type code meaning inferred; please check)" : "");
            _mapped[line] = _mapped.GetValueOrDefault(line) + 1;
        }

        // ---- commands, timers ----

        private CommandDefinition MapCommand(JsonObject c)
        {
            var command = new CommandDefinition
            {
                Id = Str(c, "id") is { } raw && _commands.TryGetValue(raw, out var id) ? id : IdOf(c),
                Name = Str(c, "name") ?? "Imported command",
                Group = Str(c, "group") ?? "",
                Enabled = Bool(c, "enabled") ?? true,
                Mode = Normalize(Str(c, "mode")) is "1" or "regex" ? CommandMode.Regex : CommandMode.Basic,
                Location = Normalize(Str(c, "location")) switch
                {
                    "1" or "exact" => CommandLocation.Exact,
                    "2" or "anywhere" => CommandLocation.Anywhere,
                    _ => CommandLocation.Start,
                },
                CaseSensitive = Bool(c, "caseSensitive") ?? false,
                IgnoreBotAccount = Bool(c, "ignoreBotAccount") ?? true,
                IgnoreInternal = Bool(c, "ignoreInternal") ?? true,
                GlobalCooldownSeconds = Math.Max(0, Num(c, "globalCooldown", "cooldown") ?? 0),
                UserCooldownSeconds = Math.Max(0, Num(c, "userCooldown") ?? 0),
                PersistCounter = Bool(c, "persistCounter") ?? false,
                PersistUserCounter = Bool(c, "persistUserCounter") ?? false,
                Include = Bool(c, "include") ?? true,
                Sources = CommandSources(c, Str(c, "name") ?? "Imported command"),
            };
            command.Commands = Strings(Get(c, "command", "commands"));
            if (command.Commands.Count == 0) Warn($"Command '{command.Name}' has no command text.");

            // Permissions: grant type 0 = Allow (only the listed users/groups), 1 = Deny (everyone except them). An
            // empty Allow list allows everyone; an empty Deny list denies everyone (the broadcaster always may).
            var users = Strings(Get(c, "permittedUsers", "allowedUsers"));
            var groups = Strings(Get(c, "permittedGroups", "allowedGroups"));
            if (Normalize(Str(c, "grantType")) is "1" or "deny")
            {
                command.DeniedUsers = users;
                command.DeniedGroups = groups;
                if (users.Count == 0 && groups.Count == 0) command.MinimumRole = ChatPermission.Broadcaster;
            }
            else if (users.Count > 0 || groups.Count > 0)
            {
                command.AllowedUsers = users;
                command.AllowedGroups = groups;
                command.MinimumRole = ChatPermission.Broadcaster;
            }
            return command;
        }

        /// <summary>
        /// Chat sources, documented as Twitch Message, YouTube Message, Twitch Whisper, Twitch (re)sub message; older
        /// exports list their indexes ([0, 1]), newer ones a number (1 = Twitch Message, read here as bit flags in the
        /// same order). Anything unknown keeps every source, with a warning.
        /// </summary>
        private List<string> CommandSources(JsonObject c, string name)
        {
            var node = Get(c, "sources");
            var codes = new List<int>();
            if (node is JsonArray list)
            {
                foreach (var item in list)
                    if (int.TryParse(AsString(item), out var code)) codes.Add(code);
            }
            else if (int.TryParse(AsString(node), out var flags) && flags > 0)
            {
                for (var bit = 0; bit < 31; bit++)
                    if ((flags & (1 << bit)) != 0) codes.Add(bit);
            }
            if (codes.Count == 0) return [];
            var sources = new List<string>();
            foreach (var code in codes.Distinct())
            {
                switch (code)
                {
                    case 0: sources.Add("Twitch:Message"); break;
                    case 1: sources.Add("YouTube:Message"); break;
                    case 2: sources.Add("Twitch:Whisper"); break;
                    default:
                        Warn($"Command '{name}' listens to a chat source Cuebot does not know (code {code}); it was set to every source.");
                        return [];
                }
            }
            return sources;
        }

        private TimerDefinition MapTimer(JsonObject t)
        {
            var interval = Math.Max(1, Int(t, "interval", "intervalSeconds") ?? 300);
            return new TimerDefinition
            {
                Id = IdOf(t), Name = Str(t, "name") ?? "Timer", Enabled = Bool(t, "enabled") ?? true,
                IntervalSeconds = interval, Repeat = Bool(t, "repeat") ?? true,
                RandomInterval = Bool(t, "randomInterval", "random") ?? false,
                MinIntervalSeconds = Math.Max(1, Int(t, "minInterval", "minimumInterval", "lowerInterval") ?? interval),
                // Streamer.bot writes 0 for an unused upper bound.
                MaxIntervalSeconds = Math.Max(1, Int(t, "maxInterval", "maximumInterval", "upperInterval") is { } max and > 0 ? max : interval),
                MinChatLines = Math.Max(0, Int(t, "lines", "minLines", "minChatLines") ?? 0),
            };
        }

        /// <summary>A JSON array of strings, or one string with one entry per line.</summary>
        private static List<string> Strings(JsonNode? node) => node switch
        {
            JsonArray a => a.Select(AsString).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!.Trim()).ToList(),
            JsonValue => (AsString(node) ?? "").Split('\n').Select(s => s.Trim()).Where(s => s.Length > 0).ToList(),
            _ => [],
        };

        // ---- references ----

        public string ActionRef(string? idOrName)
        {
            if (string.IsNullOrWhiteSpace(idOrName)) return "";
            if (_actions.TryGetValue(idOrName.Trim(), out var a)) return a.Name;
            if (Guid.TryParse(idOrName, out _)) Warn($"'{ActionName}': runs action id {idOrName}, which is not in this export.");
            return idOrName.Trim();
        }

        public string CommandRef(string? idOrName)
        {
            if (string.IsNullOrWhiteSpace(idOrName)) return "";
            if (_commands.TryGetValue(idOrName.Trim(), out var id)) return id.ToString();
            if (Guid.TryParse(idOrName, out _)) Warn($"'{ActionName}': command trigger names command id {idOrName}, which is not in this export.");
            return idOrName.Trim();
        }

        public string CodeRef(string? subActionId)
        {
            if (string.IsNullOrWhiteSpace(subActionId)) return "";
            if (_codeNames.TryGetValue(subActionId.Trim(), out var name)) return name;
            if (Guid.TryParse(subActionId, out _)) Warn($"'{ActionName}': calls C# code id {subActionId}, which is not in this export.");
            return subActionId.Trim();
        }

        public string TimerRef(string? idOrName)
        {
            if (string.IsNullOrWhiteSpace(idOrName)) return "";
            if (_timers.TryGetValue(idOrName.Trim(), out var name)) return name;
            if (Guid.TryParse(idOrName, out _)) Warn($"'{ActionName}': timer trigger names timer id {idOrName}, which is not in this export.");
            return idOrName.Trim();
        }

        private string QueueRef(string? idOrName)
        {
            if (!IsRealId(idOrName)) return "";
            if (_queues.TryGetValue(idOrName!.Trim(), out var name)) return name;
            if (Guid.TryParse(idOrName, out _))
            {
                Warn($"'{ActionName}': uses queue id {idOrName}, which is not in this export; it was set to the default queue.");
                return "";
            }
            return idOrName.Trim();
        }

        public void Warn(string message)
        {
            if (!Report.Warnings.Contains(message)) Report.Warnings.Add(message);
        }

        private static bool IsRealId(string? value) =>
            !string.IsNullOrWhiteSpace(value) && !(Guid.TryParse(value, out var g) && g == Guid.Empty);

        private static Guid IdOf(JsonObject node) =>
            Guid.TryParse(Str(node, "id"), out var id) && id != Guid.Empty ? id : Guid.NewGuid();
    }
}
