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
                if (Str(a, "id") is { } raw) _actions[raw] = (IdOf(a), Str(a, "name") ?? "Imported action");
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

            var steps = Objects(Arr(a, "subActions", "actions")).ToList();
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

            if (TransferReferences.Walk(action.SubActions).Any(s => s.Type.StartsWith("code.", StringComparison.OrdinalIgnoreCase)))
                action.Enabled = false;
            return action;
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
            Count(rule, "Sub-action", inferred);
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
            var line = $"{kind} {rule.Name} → {rule.TargetType}" + (inferred ? " (recognised by its fields; please check)" : "");
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
                AllowedUsers = Strings(Get(c, "permittedUsers", "allowedUsers")),
                AllowedGroups = Strings(Get(c, "permittedGroups", "allowedGroups")),
            };
            command.Commands = Strings(Get(c, "command", "commands"));
            if (command.Commands.Count == 0) Warn($"Command '{command.Name}' has no command text.");
            return command;
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
                MaxIntervalSeconds = Math.Max(1, Int(t, "maxInterval", "maximumInterval", "upperInterval") ?? interval),
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
