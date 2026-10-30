using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Polls;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Storage;
using Cuebot.Core.Transfer;
using Cuebot.Core.Variables;
using Cuebot.Server.StreamerBot;

namespace Cuebot.Server.Api;

/// <summary>
/// Every operation the REST API and the MCP tools offer, against the live services. Edits go through
/// <see cref="ProfileService.EditAsync"/>, <see cref="CommandStore"/> and <see cref="GlobalVariables"/>, so the UI
/// updates at once. Failures throw <see cref="ApiException"/>. Settings files (platform tokens, servers.json) are never
/// read or returned here.
/// </summary>
public sealed class ApiOperations(ServerServices services, Func<IReadOnlyList<ServerStatus>>? statuses = null)
{
    private const string LogSource = "API";

    /// <summary>actions.json shape (camelCase, enums as strings), not indented.</summary>
    public static readonly JsonSerializerOptions JsonOptions = new(Json.Options) { WriteIndented = false };

    private ProfileService Profile => services.Profile;
    private Registry Registry => services.Registry ?? throw ApiException.Unavailable("The action registry is not available");
    private CommandStore Commands => services.Commands ?? throw ApiException.Unavailable("Commands are not available");

    // ---- Actions ---------------------------------------------------------------------------------------------------

    public JsonObject ListActions(string? group = null, string? search = null)
    {
        var actions = Profile.Snapshot.Actions
            .Where(a => string.IsNullOrWhiteSpace(group) || string.Equals(a.Group, group.Trim(), StringComparison.OrdinalIgnoreCase))
            .Where(a => string.IsNullOrWhiteSpace(search) || a.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                        || a.Group.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new JsonObject
        {
            ["actions"] = new JsonArray([.. actions.Select(a => (JsonNode)ActionSummary(a))]),
            ["count"] = actions.Count,
        };
    }

    public static JsonObject ActionSummary(ActionDefinition a) => new()
    {
        ["id"] = a.Id.ToString(),
        ["name"] = a.Name,
        ["group"] = a.Group,
        ["enabled"] = a.Enabled,
        ["queue"] = a.Queue,
        ["triggers"] = new JsonArray([.. a.Triggers.Select(t => (JsonNode)new JsonObject { ["type"] = t.Type, ["enabled"] = t.Enabled })]),
        ["subActionCount"] = TransferReferences.Walk(a.SubActions).Count(),
    };

    /// <summary>An action by id, else by name (case-insensitive).</summary>
    public ActionDefinition FindAction(string? idOrName)
    {
        var text = (idOrName ?? "").Trim();
        var snapshot = Profile.Snapshot;
        var found = (Guid.TryParse(text, out var id) ? snapshot.FindAction(id) : null) ?? (text.Length > 0 ? snapshot.FindAction(text) : null);
        return found ?? throw ApiException.NotFound($"Action '{text}' not found");
    }

    public JsonObject GetAction(string idOrName) => ToObject(FindAction(idOrName));

    public async Task<JsonObject> CreateActionAsync(JsonNode? body)
    {
        var action = FromNode<ActionDefinition>(body, "action");
        if (action.Id == Guid.Empty) action.Id = Guid.NewGuid();
        if (Profile.Snapshot.FindAction(action.Id) is not null) throw ApiException.Conflict($"An action with id {action.Id} already exists");
        var result = ValidateOrThrow(action);
        await Profile.EditAsync(p => p.Actions.Add(action)).ConfigureAwait(false);
        services.Log.Info(LogSource, $"Action '{action.Name}' created");
        return Saved("action", Profile.Snapshot.FindAction(action.Id) ?? action, result.Warnings);
    }

    /// <summary>Replaces an action (<paramref name="merge"/> false) or changes only the given top-level fields.</summary>
    public async Task<JsonObject> UpdateActionAsync(string idOrName, JsonNode? body, bool merge)
    {
        var existing = FindAction(idOrName);
        var patch = body as JsonObject ?? throw ApiException.BadRequest("The body must be a JSON object (an action)");
        var obj = merge ? Merge(ToObject(existing), patch) : (JsonObject)patch.DeepClone();
        obj["id"] = existing.Id.ToString();
        var action = FromNode<ActionDefinition>(obj, "action");
        var result = ValidateOrThrow(action);
        var found = false;
        await Profile.EditAsync(p =>
        {
            var index = p.Actions.FindIndex(a => a.Id == existing.Id);
            if (index < 0) return;
            p.Actions[index] = action;
            found = true;
        }).ConfigureAwait(false);
        if (!found) throw ApiException.NotFound($"Action '{idOrName}' not found");
        services.Log.Info(LogSource, $"Action '{action.Name}' updated");
        return Saved("action", Profile.Snapshot.FindAction(action.Id) ?? action, result.Warnings);
    }

    public async Task<JsonObject> DeleteActionAsync(string idOrName)
    {
        var existing = FindAction(idOrName);
        var removed = 0;
        await Profile.EditAsync(p => removed = p.Actions.RemoveAll(a => a.Id == existing.Id)).ConfigureAwait(false);
        if (removed == 0) throw ApiException.NotFound($"Action '{idOrName}' not found");
        services.Log.Info(LogSource, $"Action '{existing.Name}' deleted");
        return new JsonObject { ["deleted"] = true, ["id"] = existing.Id.ToString(), ["name"] = existing.Name };
    }

    public async Task<JsonObject> SetActionEnabledAsync(string idOrName, bool enabled)
    {
        var existing = FindAction(idOrName);
        await Profile.EditAsync(p =>
        {
            if (p.Actions.FirstOrDefault(a => a.Id == existing.Id) is { } action) action.Enabled = enabled;
        }).ConfigureAwait(false);
        services.Log.Info(LogSource, $"Action '{existing.Name}' {(enabled ? "enabled" : "disabled")}");
        return ActionSummary(Profile.Snapshot.FindAction(existing.Id) ?? existing);
    }

    /// <summary>
    /// Runs an action with <paramref name="args"/> (an object; values become %arguments%). Without
    /// <paramref name="wait"/> it is queued and this returns at once.
    /// </summary>
    public async Task<JsonObject> RunActionAsync(string idOrName, JsonNode? args, bool wait, double timeoutSeconds, string source,
        CancellationToken ct = default)
    {
        var action = FindAction(idOrName);
        if (!action.Enabled) throw ApiException.Conflict($"Action '{action.Name}' is disabled; enable it first");
        if (args is not null and not JsonObject) throw ApiException.BadRequest("args must be a JSON object");
        var runArgs = StreamerBotRequests.ToArgs(args is null ? default : JsonSerializer.SerializeToElement(args));
        services.Log.Info(LogSource, $"{source} runs action '{action.Name}'");
        var started = DateTimeOffset.Now;
        var run = services.Engine.RunAsync(action, runArgs, CancellationToken.None, source);
        var info = new JsonObject { ["id"] = action.Id.ToString(), ["name"] = action.Name };
        if (!wait)
        {
            _ = run.ContinueWith(t => services.Log.Error(LogSource, $"Action '{action.Name}' failed", t.Exception?.GetBaseException()),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            return new JsonObject { ["queued"] = true, ["action"] = info };
        }

        try
        {
            var success = await run.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300)), ct).ConfigureAwait(false);
            var entry = services.Engine.History.Entries.FirstOrDefault(e => e.ActionId == action.Id && e.Source == source && e.QueuedAt >= started.AddSeconds(-1));
            var result = new JsonObject { ["queued"] = true, ["completed"] = true, ["success"] = success, ["action"] = info };
            if (entry is not null) result["run"] = RunJson(entry, includeArgs: true);
            return result;
        }
        catch (TimeoutException)
        {
            return new JsonObject
            {
                ["queued"] = true, ["completed"] = false, ["action"] = info,
                ["message"] = "The action is still running (or waiting in its queue); check get_history / GET /api/v1/history later",
            };
        }
    }

    private ActionValidator.Result ValidateOrThrow(ActionDefinition action)
    {
        var result = ActionValidator.Validate(action, Registry);
        if (!result.Ok) throw ApiException.BadRequest("The action is not valid", result.Errors);
        return result;
    }

    // ---- Catalogue -------------------------------------------------------------------------------------------------

    public JsonObject ListTriggers(string? category = null, string? search = null)
    {
        var all = Registry.Triggers;
        var list = all.Where(t => CatalogJson.Matches(t.TypeId, t.DisplayName, t.Category, category, search)).ToList();
        return new JsonObject
        {
            ["triggers"] = new JsonArray([.. list.Select(t => (JsonNode)CatalogJson.Trigger(t, detailed: false))]),
            ["count"] = list.Count,
            ["categories"] = CatalogJson.Strings(all.Select(t => t.Category).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)),
        };
    }

    public JsonObject DescribeTrigger(string typeId) =>
        CatalogJson.Trigger(Registry.GetTrigger(typeId.Trim()) ?? throw ApiException.NotFound($"Trigger type '{typeId}' not found"), detailed: true);

    public JsonObject ListSubActions(string? category = null, string? search = null)
    {
        var all = Registry.SubActions;
        var list = all.Where(h => CatalogJson.Matches(h.TypeId, h.DisplayName, h.Category, category, search)).ToList();
        return new JsonObject
        {
            ["subActions"] = new JsonArray([.. list.Select(h => (JsonNode)CatalogJson.SubAction(h, detailed: false))]),
            ["count"] = list.Count,
            ["categories"] = CatalogJson.Strings(all.Select(h => h.Category).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)),
        };
    }

    public JsonObject DescribeSubAction(string typeId) =>
        CatalogJson.SubAction(Registry.GetSubAction(typeId.Trim()) ?? throw ApiException.NotFound($"Sub-action type '{typeId}' not found"), detailed: true);

    public JsonObject ListScriptFunctions(string? search = null)
    {
        var list = (services.Scripts?.Functions ?? [])
            .Where(f => string.IsNullOrWhiteSpace(search) || f.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase)
                        || f.Description.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();
        return new JsonObject
        {
            ["functions"] = new JsonArray([.. list.Select(f => (JsonNode)CatalogJson.ScriptFunction(f))]),
            ["count"] = list.Count,
        };
    }

    // ---- Commands --------------------------------------------------------------------------------------------------

    public JsonObject ListCommands()
    {
        var all = Commands.All();
        return new JsonObject { ["commands"] = new JsonArray([.. all.Select(c => (JsonNode)ToObject(c))]), ["count"] = all.Count };
    }

    public CommandDefinition FindCommand(string? idOrName) =>
        Commands.Find(idOrName ?? "") ?? throw ApiException.NotFound($"Command '{idOrName}' not found");

    /// <summary>The command plus the actions it runs (actions with a "Command triggered" trigger for it).</summary>
    public JsonObject GetCommand(string idOrName)
    {
        var command = FindCommand(idOrName);
        var json = ToObject(command);
        json["linkedActions"] = new JsonArray([.. LinkedActions(command).Select(a => (JsonNode)new JsonObject { ["id"] = a.Id.ToString(), ["name"] = a.Name })]);
        return json;
    }

    private IEnumerable<ActionDefinition> LinkedActions(CommandDefinition command) =>
        Profile.Snapshot.Actions.Where(a => a.Triggers.Any(t => string.Equals(t.Type, "command.triggered", StringComparison.OrdinalIgnoreCase)
            && t.Settings.GetValueOrDefault("command") is { } wanted
            && (string.Equals(wanted.Trim(), command.Name, StringComparison.OrdinalIgnoreCase)
                || string.Equals(wanted.Trim(), command.Id.ToString(), StringComparison.OrdinalIgnoreCase))));

    public JsonObject CreateCommand(JsonNode? body)
    {
        var command = FromNode<CommandDefinition>(body, "command");
        if (command.Id == Guid.Empty) command.Id = Guid.NewGuid();
        if (Commands.Get(command.Id) is not null) throw ApiException.Conflict($"A command with id {command.Id} already exists");
        var warnings = ValidateCommand(command);
        Commands.Save(command);
        services.Log.Info(LogSource, $"Command '{command.Name}' created");
        return Saved("command", Commands.Get(command.Id) ?? command, warnings);
    }

    public JsonObject UpdateCommand(string idOrName, JsonNode? body, bool merge)
    {
        var existing = FindCommand(idOrName);
        var patch = body as JsonObject ?? throw ApiException.BadRequest("The body must be a JSON object (a command)");
        var obj = merge ? Merge(ToObject(existing), patch) : (JsonObject)patch.DeepClone();
        obj["id"] = existing.Id.ToString();
        var command = FromNode<CommandDefinition>(obj, "command");
        var warnings = ValidateCommand(command);
        Commands.Save(command);
        services.Log.Info(LogSource, $"Command '{command.Name}' updated");
        return Saved("command", Commands.Get(command.Id) ?? command, warnings);
    }

    public JsonObject DeleteCommand(string idOrName)
    {
        var existing = FindCommand(idOrName);
        if (!Commands.Remove(existing.Id)) throw ApiException.NotFound($"Command '{idOrName}' not found");
        services.Log.Info(LogSource, $"Command '{existing.Name}' deleted");
        return new JsonObject { ["deleted"] = true, ["id"] = existing.Id.ToString(), ["name"] = existing.Name };
    }

    private JsonArray ValidateCommand(CommandDefinition command)
    {
        var errors = new JsonArray();
        var warnings = new JsonArray();
        command.Name = (command.Name ?? "").Trim();
        command.Group ??= "";
        command.Commands = (command.Commands ?? []).Select(c => (c ?? "").Trim()).Where(c => c.Length > 0).ToList();
        command.Sources ??= [];
        command.AllowedUsers ??= [];
        command.AllowedGroups ??= [];
        command.DeniedUsers ??= [];
        command.DeniedGroups ??= [];
        if (command.Name.Length == 0) errors.Add(Problem("name", "The command needs a name"));
        if (command.GlobalCooldownSeconds < 0) errors.Add(Problem("globalCooldownSeconds", "Must be 0 or more"));
        if (command.UserCooldownSeconds < 0) errors.Add(Problem("userCooldownSeconds", "Must be 0 or more"));
        if (command.Mode == CommandMode.Regex)
        {
            for (var i = 0; i < command.Commands.Count; i++)
            {
                try
                {
                    _ = new System.Text.RegularExpressions.Regex(command.Commands[i], System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromSeconds(1));
                }
                catch (ArgumentException ex)
                {
                    errors.Add(Problem($"commands[{i}]", $"Invalid regular expression: {ex.Message}"));
                }
            }
        }
        if (command.Commands.Count == 0) warnings.Add(Problem("commands", "No trigger text: the command never matches (e.g. [\"!hello\"])"));
        if (errors.Count > 0) throw ApiException.BadRequest("The command is not valid", errors);
        return warnings;
    }

    // ---- Timers ----------------------------------------------------------------------------------------------------

    public JsonObject ListTimers()
    {
        var timers = Profile.Snapshot.Timers;
        return new JsonObject { ["timers"] = new JsonArray([.. timers.Select(t => (JsonNode)TimerJson(t))]), ["count"] = timers.Count };
    }

    public TimerDefinition FindTimer(string? idOrName)
    {
        var text = (idOrName ?? "").Trim();
        var timers = Profile.Snapshot.Timers;
        return (Guid.TryParse(text, out var id) ? timers.FirstOrDefault(t => t.Id == id) : null)
               ?? timers.FirstOrDefault(t => string.Equals(t.Name, text, StringComparison.OrdinalIgnoreCase))
               ?? throw ApiException.NotFound($"Timer '{text}' not found");
    }

    public JsonObject GetTimer(string idOrName) => TimerJson(FindTimer(idOrName));

    /// <summary>The timer definition plus <c>state</c> (runtime: enabled, interval, next fire) when timers are running.</summary>
    private JsonObject TimerJson(TimerDefinition timer)
    {
        var json = ToObject(timer);
        if (services.Timers?.GetState(timer.Id.ToString()) is { } state)
        {
            json["state"] = new JsonObject
            {
                ["enabled"] = state.Enabled,
                ["intervalSeconds"] = state.IntervalSeconds,
                ["nextFire"] = state.NextFire?.ToString("o"),
                ["chatLines"] = state.ChatLines,
            };
        }
        return json;
    }

    public async Task<JsonObject> CreateTimerAsync(JsonNode? body)
    {
        var timer = FromNode<TimerDefinition>(body, "timer");
        if (timer.Id == Guid.Empty) timer.Id = Guid.NewGuid();
        if (Profile.Snapshot.Timers.Any(t => t.Id == timer.Id)) throw ApiException.Conflict($"A timer with id {timer.Id} already exists");
        ValidateTimer(timer);
        await Profile.EditAsync(p => p.Timers.Add(timer)).ConfigureAwait(false);
        services.Log.Info(LogSource, $"Timer '{timer.Name}' created");
        return new JsonObject { ["timer"] = TimerJson(FindTimer(timer.Id.ToString())), ["hint"] = TimerHint(timer) };
    }

    public async Task<JsonObject> UpdateTimerAsync(string idOrName, JsonNode? body, bool merge)
    {
        var existing = FindTimer(idOrName);
        var patch = body as JsonObject ?? throw ApiException.BadRequest("The body must be a JSON object (a timer)");
        var obj = merge ? Merge(ToObject(existing), patch) : (JsonObject)patch.DeepClone();
        obj["id"] = existing.Id.ToString();
        var timer = FromNode<TimerDefinition>(obj, "timer");
        ValidateTimer(timer);
        var found = false;
        await Profile.EditAsync(p =>
        {
            var index = p.Timers.FindIndex(t => t.Id == existing.Id);
            if (index < 0) return;
            p.Timers[index] = timer;
            found = true;
        }).ConfigureAwait(false);
        if (!found) throw ApiException.NotFound($"Timer '{idOrName}' not found");
        services.Log.Info(LogSource, $"Timer '{timer.Name}' updated");
        return new JsonObject { ["timer"] = TimerJson(FindTimer(timer.Id.ToString())) };
    }

    public async Task<JsonObject> DeleteTimerAsync(string idOrName)
    {
        var existing = FindTimer(idOrName);
        await Profile.EditAsync(p => p.Timers.RemoveAll(t => t.Id == existing.Id)).ConfigureAwait(false);
        services.Log.Info(LogSource, $"Timer '{existing.Name}' deleted");
        return new JsonObject { ["deleted"] = true, ["id"] = existing.Id.ToString(), ["name"] = existing.Name };
    }

    /// <summary>Runtime enable/disable or interval change (like the "Set timer state" sub-action; not saved).</summary>
    public JsonObject SetTimerState(string idOrName, bool? enabled, int? intervalSeconds)
    {
        var timer = FindTimer(idOrName);
        var timers = services.Timers ?? throw ApiException.Unavailable("Timers are not running");
        if (intervalSeconds is < 0) throw ApiException.BadRequest("intervalSeconds must be 0 or more");
        if (!timers.SetState(timer.Id.ToString(), enabled, intervalSeconds)) throw ApiException.NotFound($"Timer '{idOrName}' not found");
        return TimerJson(timer);
    }

    private static string TimerHint(TimerDefinition timer) =>
        $"A timer only fires events. To run an action, add a trigger {{\"type\": \"core.timer\", \"settings\": {{\"timer\": \"{timer.Name}\"}}}} to it.";

    private static void ValidateTimer(TimerDefinition timer)
    {
        var errors = new JsonArray();
        timer.Name = (timer.Name ?? "").Trim();
        if (timer.Name.Length == 0) errors.Add(Problem("name", "The timer needs a name"));
        if (!string.IsNullOrWhiteSpace(timer.Cron))
        {
            if (!CronExpression.TryParse(timer.Cron, out _, out var error)) errors.Add(Problem("cron", error));
        }
        else if (timer.RandomInterval)
        {
            if (timer.MinIntervalSeconds < 1) errors.Add(Problem("minIntervalSeconds", "Must be at least 1"));
            if (timer.MaxIntervalSeconds < timer.MinIntervalSeconds) errors.Add(Problem("maxIntervalSeconds", "Must be at least minIntervalSeconds"));
        }
        else if (timer.IntervalSeconds < 1)
        {
            errors.Add(Problem("intervalSeconds", "Must be at least 1"));
        }
        if (timer.MinChatLines < 0) errors.Add(Problem("minChatLines", "Must be 0 or more"));
        if (errors.Count > 0) throw ApiException.BadRequest("The timer is not valid", errors);
    }

    // ---- Global variables ------------------------------------------------------------------------------------------

    public JsonObject GetGlobals(bool? persisted = null)
    {
        var list = services.Globals.Snapshot(persisted);
        return new JsonObject { ["variables"] = new JsonArray([.. list.Select(v => (JsonNode)GlobalJson(v))]), ["count"] = list.Count };
    }

    public JsonObject GetGlobal(string name)
    {
        var found = services.Globals.Snapshot().FirstOrDefault(v => string.Equals(v.Name, name.Trim(), StringComparison.OrdinalIgnoreCase))
                    ?? throw ApiException.NotFound($"Global variable '{name}' not found");
        return GlobalJson(found);
    }

    public JsonObject SetGlobal(string name, JsonNode? value, bool persisted)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0) throw ApiException.BadRequest("A variable name is required");
        var element = value is null ? default : JsonSerializer.SerializeToElement(value);
        services.Globals.SetValue(name, value is null ? null : VariableValues.Normalize(element), persisted);
        services.Log.Info(LogSource, $"Global variable '{name}' set");
        return GetGlobal(name);
    }

    public JsonObject DeleteGlobal(string name)
    {
        if (!services.Globals.Remove(name.Trim())) throw ApiException.NotFound($"Global variable '{name}' not found");
        services.Log.Info(LogSource, $"Global variable '{name}' deleted");
        return new JsonObject { ["deleted"] = true, ["name"] = name.Trim() };
    }

    private static JsonObject GlobalJson(GlobalVariableInfo v) => new()
    {
        ["name"] = v.Name,
        ["value"] = Value(v.Value),
        ["type"] = v.TypeName,
        ["persisted"] = v.Persisted,
        ["lastWrite"] = v.LastWrite.ToString("o"),
    };

    // ---- Status, history, chat -------------------------------------------------------------------------------------

    public JsonObject Status()
    {
        var snapshot = Profile.Snapshot;
        var json = new JsonObject
        {
            ["app"] = new JsonObject
            {
                ["name"] = "Cuebot",
                ["version"] = typeof(ApiOperations).Assembly.GetName().Version?.ToString(3) ?? "0.0.0",
                ["runtime"] = RuntimeInformation.FrameworkDescription,
                ["os"] = RuntimeInformation.OSDescription,
                ["instanceId"] = StreamerBotRequests.InstanceId,
                ["apiVersion"] = "v1",
            },
            ["connections"] = new JsonArray([.. services.Connections.Select(c => (JsonNode)new JsonObject
            {
                ["name"] = c.Name,
                ["state"] = c.State.ToString(),
                ["status"] = c.StatusMessage,
            })]),
            ["counts"] = new JsonObject
            {
                ["actions"] = snapshot.Actions.Count,
                ["enabledActions"] = snapshot.Actions.Count(a => a.Enabled),
                ["timers"] = snapshot.Timers.Count,
                ["queues"] = snapshot.Queues.Count,
                ["commands"] = services.Commands?.Snapshot.Count ?? 0,
                ["globals"] = services.Globals.Snapshot().Count,
                ["triggerTypes"] = services.Registry?.Triggers.Count ?? 0,
                ["subActionTypes"] = services.Registry?.SubActions.Count ?? 0,
            },
        };
        if (services.Chat is { } chat)
        {
            json["chat"] = new JsonObject
            {
                ["platforms"] = CatalogJson.Strings(chat.Platforms),
                ["sendablePlatforms"] = CatalogJson.Strings(chat.SendablePlatforms),
            };
        }
        if (statuses is not null)
        {
            json["servers"] = new JsonArray([.. statuses().Select(s => (JsonNode)new JsonObject
            {
                ["kind"] = s.Kind.ToString(),
                ["name"] = s.Name,
                ["enabled"] = s.Enabled,
                ["running"] = s.Running,
                ["port"] = s.Port,
                ["status"] = s.Message,
            })]);
        }
        return json;
    }

    public JsonObject History(int limit = 50, string? action = null, bool includeArgs = false)
    {
        Guid? actionId = string.IsNullOrWhiteSpace(action) ? null : FindAction(action).Id;
        var runs = services.Engine.History.Entries
            .Where(e => actionId is null || e.ActionId == actionId)
            .Take(Math.Clamp(limit, 1, ActionHistory.DefaultCapacity))
            .ToList();
        return new JsonObject { ["runs"] = new JsonArray([.. runs.Select(r => (JsonNode)RunJson(r, includeArgs))]), ["count"] = runs.Count };
    }

    public static JsonObject RunJson(ActionRunEntry e, bool includeArgs)
    {
        var json = new JsonObject
        {
            ["runId"] = e.RunId.ToString(),
            ["actionId"] = e.ActionId.ToString(),
            ["actionName"] = e.ActionName,
            ["queue"] = e.Queue,
            ["source"] = e.Source,
            ["state"] = e.State.ToString(),
            ["queuedAt"] = e.QueuedAt.ToString("o"),
            ["startedAt"] = e.StartedAt?.ToString("o"),
            ["completedAt"] = e.CompletedAt?.ToString("o"),
            ["durationMs"] = e.Duration is { } d ? (long)d.TotalMilliseconds : null,
            ["error"] = e.Error,
            ["depth"] = e.Depth,
        };
        if (includeArgs)
        {
            json["args"] = Args(e.ArgsWhenQueued);
            if (e.ArgsAfterRun is { } after) json["argsAfterRun"] = Args(after);
        }
        return json;
    }

    /// <summary>Cuebot speech's state: <c>{enabled, paused, queueLength, current}</c>.</summary>
    public JsonObject Speech()
    {
        var speech = services.Speaker ?? throw ApiException.Unavailable("Speech is not available");
        return new JsonObject
        {
            ["enabled"] = speech.Enabled,
            ["paused"] = speech.Paused,
            ["queueLength"] = speech.QueueLength,
            ["current"] = speech.CurrentText,
        };
    }

    /// <summary>
    /// A speech queue control: <c>next</c> (stop the text playing; while paused, let one waiting text play),
    /// <c>pause</c>, <c>resume</c> or <c>skip</c> (stop the text playing). Returns the state, with <c>stepped</c> for next.
    /// </summary>
    public JsonObject SpeechControl(string command)
    {
        var speech = services.Speaker ?? throw ApiException.Unavailable("Speech is not available");
        bool? stepped = null;
        switch (command.ToLowerInvariant())
        {
            case "next": stepped = speech.SpeakNext(); break;
            case "pause": speech.Paused = true; break;
            case "resume": speech.Paused = false; break;
            case "skip": speech.Stop(); break;
            default: throw ApiException.NotFound($"Unknown speech command '{command}' (next, pause, resume or skip)");
        }
        var json = Speech();
        if (stepped is { } s) json["stepped"] = s;
        return json;
    }

    /// <summary>The overlay alert queue: <c>{paused, muted}</c>.</summary>
    public JsonObject Alerts() =>
        (services.AlertQueue ?? throw ApiException.Unavailable("Overlays are not available")).Snapshot();

    /// <summary>An alert queue control: <c>pause</c>, <c>resume</c>, <c>skip</c>, <c>mute</c> or <c>unmute</c>; returns the state.</summary>
    public JsonObject AlertControl(string command)
    {
        var queue = services.AlertQueue ?? throw ApiException.Unavailable("Overlays are not available");
        if (command.ToLowerInvariant() is not ("pause" or "resume" or "skip" or "mute" or "unmute")
            || !Cuebot.Core.Overlays.AlertQueueSubAction.Apply(queue, command))
            throw ApiException.NotFound($"Unknown alert command '{command}' (pause, resume, skip, mute or unmute)");
        return queue.Snapshot();
    }

    /// <summary>The latest poll and prediction from each source, and Cuebot's chat commands for voting and betting.</summary>
    public JsonObject Polls()
    {
        var tracker = services.Polls ?? throw ApiException.Unavailable("Polls are not available");
        return new JsonObject
        {
            ["polls"] = tracker.Snapshot(),
            ["voteCommand"] = services.ChatPolls?.VoteCommand ?? "",
            ["betCommand"] = services.ChatPolls?.BetCommand ?? "",
        };
    }

    /// <summary>
    /// Cuebot chat polls and predictions: <c>startPoll {title, choices, duration}</c>, <c>endPoll</c>,
    /// <c>startPrediction {title, outcomes, window, currency}</c>, <c>lock</c>, <c>resolve {winner}</c> (number from 1 or
    /// title) and <c>cancel</c>. Returns the new state; 404 when there was nothing to end, lock, resolve or cancel.
    /// </summary>
    public JsonObject ChatPollCommand(string command, JsonObject body)
    {
        var polls = services.ChatPolls ?? throw ApiException.Unavailable("Polls are not available");
        static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToJsonString() ?? "";
        static IEnumerable<string> List(JsonNode? node) => node switch
        {
            JsonArray a => a.Select(Text),
            JsonValue v when v.TryGetValue<string>(out var s) => s.Split('\n'),
            _ => [],
        };
        static int Int(JsonNode? node, int fallback) => node is JsonValue v && v.TryGetValue<double>(out var d) ? (int)d : fallback;
        try
        {
            var state = command switch
            {
                "startPoll" => polls.StartPoll(Text(body["title"]), List(body["choices"]), Int(body["duration"], 60)),
                "endPoll" => polls.EndPoll(),
                "startPrediction" => polls.StartPrediction(Text(body["title"]), List(body["outcomes"]), Int(body["window"], 120), Text(body["currency"])),
                "lock" => polls.LockPrediction(),
                "resolve" => polls.ResolvePrediction(ChatPolls.Find(polls.PredictionOutcomes, Text(body["winner"])) is var w && w >= 0
                    ? w : throw new ArgumentException("winner must be an outcome's number (from 1) or title")),
                "cancel" => polls.CancelPrediction(),
                _ => throw ApiException.NotFound($"Unknown command '{command.Replace("unknown ", "")}' (lock, resolve or cancel)"),
            };
            return state?.ToJson() ?? throw ApiException.NotFound(command == "endPoll" ? "No chat poll is running" : "No chat prediction is open");
        }
        catch (ArgumentException ex)
        {
            throw ApiException.BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw ApiException.Conflict(ex.Message);
        }
    }

    /// <summary>The current (or last) giveaway: <c>{giveaway}</c> (null when there has been none).</summary>
    public JsonObject Giveaway() =>
        new() { ["giveaway"] = (services.Giveaways ?? throw ApiException.Unavailable("Giveaways are not available")).Snapshot() };

    /// <summary>
    /// <c>open {title, command, ticketCost, maxTickets, currency, subscriberLuck, subscribersOnly, seconds, winners}</c>,
    /// <c>close</c>, <c>draw</c>, <c>redraw</c> (one more winner) or <c>cancel</c> (refunds tickets). Returns the state.
    /// </summary>
    public JsonObject GiveawayCommand(string command, JsonObject body)
    {
        var giveaways = services.Giveaways ?? throw ApiException.Unavailable("Giveaways are not available");
        static string Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToJsonString() ?? "";
        static double Num(JsonNode? node, double fallback) => node is JsonValue v && v.TryGetValue<double>(out var d) ? d : fallback;
        try
        {
            var state = command switch
            {
                "open" => giveaways.Open(new Cuebot.Core.Giveaways.GiveawayOptions
                {
                    Title = Text(body["title"]), Command = body["command"] is null ? "!join" : Text(body["command"]),
                    TicketCost = (long)Num(body["ticketCost"], 0), MaxTickets = (int)Num(body["maxTickets"], 1), Currency = Text(body["currency"]),
                    SubscriberLuck = Num(body["subscriberLuck"], 1), SubscribersOnly = body["subscribersOnly"] is JsonValue b && b.TryGetValue<bool>(out var only) && only,
                    Seconds = (int)Num(body["seconds"], 0), Winners = (int)Num(body["winners"], 1),
                }),
                "close" => giveaways.Close(),
                "draw" => giveaways.Draw(),
                "redraw" => giveaways.Draw(oneMore: true),
                "cancel" => giveaways.Cancel(),
                _ => throw ApiException.NotFound($"Unknown giveaway command '{command}' (close, draw, redraw or cancel)"),
            };
            return state ?? throw ApiException.NotFound("There is no giveaway to " + command);
        }
        catch (ArgumentException ex)
        {
            throw ApiException.BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            throw ApiException.Conflict(ex.Message);
        }
    }

    /// <summary>The points store: <c>{command, items, pending, history}</c> (the latest 20 finished redemptions).</summary>
    public JsonObject Store()
    {
        var store = services.Store ?? throw ApiException.Unavailable("The store is not available");
        return new JsonObject
        {
            ["command"] = store.Command,
            ["items"] = JsonSerializer.SerializeToNode(store.Items, JsonOptions),
            ["pending"] = JsonSerializer.SerializeToNode(store.Pending, JsonOptions),
            ["history"] = JsonSerializer.SerializeToNode(store.History.Take(20), JsonOptions),
        };
    }

    /// <summary>Approves or rejects (refunds) a pending redemption; 404 when it is not pending.</summary>
    public JsonObject ReviewRedemption(string id, string command)
    {
        var store = services.Store ?? throw ApiException.Unavailable("The store is not available");
        var done = command switch
        {
            "approve" => store.Approve(id),
            "reject" => store.Reject(id),
            _ => throw ApiException.NotFound($"Unknown command '{command}' (approve or reject)"),
        };
        return done ? Store() : throw ApiException.NotFound($"No pending redemption '{id}'");
    }

    public JsonObject Chat(int limit = 50)
    {
        var chat = services.Chat ?? throw ApiException.Unavailable("Chat is not available");
        limit = Math.Clamp(limit, 1, 500);
        var messages = chat.RecentMessages().TakeLast(limit).ToList();
        var feed = chat.RecentFeed().TakeLast(limit).ToList();
        return new JsonObject
        {
            ["messages"] = new JsonArray([.. messages.Select(m => (JsonNode)new JsonObject
            {
                ["platform"] = m.Platform,
                ["id"] = m.Id,
                ["user"] = m.User,
                ["userId"] = m.UserId,
                ["text"] = m.Text,
                ["time"] = m.Time.ToString("o"),
                ["roles"] = m.Roles.ToString(),
                ["color"] = m.Color,
                ["amount"] = m.Amount,
            })]),
            ["feed"] = new JsonArray([.. feed.Select(f => (JsonNode)new JsonObject
            {
                ["platform"] = f.Platform,
                ["kind"] = f.Kind.ToString(),
                ["user"] = f.User,
                ["title"] = f.Title,
                ["detail"] = f.Detail,
                ["amount"] = f.Amount,
                ["time"] = f.Time.ToString("o"),
            })]),
            ["platforms"] = CatalogJson.Strings(chat.Platforms),
            ["sendablePlatforms"] = CatalogJson.Strings(chat.SendablePlatforms),
        };
    }

    public async Task<JsonObject> SendChatAsync(string? platform, string? message, CancellationToken ct = default)
    {
        var chat = services.Chat ?? throw ApiException.Unavailable("Chat is not available");
        var text = message ?? "";
        if (text.Trim().Length == 0) throw ApiException.BadRequest("message is required");
        var target = string.IsNullOrWhiteSpace(platform) ? ChatHub.AllPlatforms : platform.Trim();
        try
        {
            var sent = await chat.SendAsync(target, text, ct).ConfigureAwait(false);
            services.Log.Info(LogSource, $"Chat message sent to {string.Join(", ", sent)}");
            return new JsonObject { ["sent"] = true, ["platforms"] = CatalogJson.Strings(sent) };
        }
        catch (InvalidOperationException ex)
        {
            throw ApiException.Conflict(ex.Message);
        }
    }

    // ---- Import / export -------------------------------------------------------------------------------------------

    private TransferService Transfer => services.Transfer ?? throw ApiException.Unavailable("Import/export is not available");

    public JsonObject PreviewImport(string? data) => PlanJson(Plan(data));

    private ImportPlan Plan(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) throw ApiException.BadRequest("data is required (a CUEBOT1:... string, bundle JSON or a Streamer.bot export)");
        try
        {
            return Transfer.Preview(data);
        }
        catch (TransferFormatException ex)
        {
            throw ApiException.BadRequest(ex.Message);
        }
    }

    /// <summary>
    /// Imports <paramref name="data"/>. <paramref name="mode"/> (overwrite, addAsNew, skip) applies to every conflict;
    /// <paramref name="items"/> overrides single items: <c>[{kind, name | sourceId, include?, mode?}]</c>.
    /// </summary>
    public async Task<JsonObject> ApplyImportAsync(string? data, string? mode, JsonArray? items)
    {
        var plan = Plan(data);
        if (!string.IsNullOrWhiteSpace(mode))
        {
            var all = ParseMode(mode, "mode");
            foreach (var item in plan.Items) item.Mode = all;
        }
        var problems = new JsonArray();
        for (var i = 0; i < (items?.Count ?? 0); i++)
        {
            if (items![i] is not JsonObject choice)
            {
                problems.Add(Problem($"items[{i}]", "Must be an object"));
                continue;
            }
            var kindText = choice["kind"]?.GetValue<string>() ?? "";
            if (!Enum.TryParse<TransferKind>(kindText, ignoreCase: true, out var kind))
            {
                problems.Add(Problem($"items[{i}].kind", $"Unknown kind '{kindText}' (action, queue, command, timer, global)"));
                continue;
            }
            var name = choice["name"]?.GetValue<string>();
            var sourceId = Guid.TryParse(choice["sourceId"]?.GetValue<string>(), out var g) ? g : (Guid?)null;
            var target = plan.Items.FirstOrDefault(x => x.Kind == kind && (sourceId is { } id ? x.SourceId == id : string.Equals(x.Name, name, StringComparison.OrdinalIgnoreCase)));
            if (target is null)
            {
                problems.Add(Problem($"items[{i}]", $"No {kind} '{(object?)sourceId ?? name}' in the import"));
                continue;
            }
            if (choice["include"] is JsonValue include && include.TryGetValue<bool>(out var inc)) target.Include = inc;
            if (choice["mode"]?.GetValue<string>() is { } m) target.Mode = ParseMode(m, $"items[{i}].mode");
        }
        if (problems.Count > 0) throw ApiException.BadRequest("The import choices are not valid", problems);
        var changes = await Transfer.ApplyAsync(plan).ConfigureAwait(false);
        services.Log.Info(LogSource, $"Import applied: {changes.Summary}");
        return new JsonObject
        {
            ["summary"] = changes.Summary,
            ["added"] = changes.Added,
            ["overwritten"] = changes.Overwritten,
            ["skipped"] = changes.Skipped,
            ["renamed"] = CatalogJson.Strings(changes.Renamed),
            ["actions"] = new JsonArray([.. changes.Actions.Select(a => (JsonNode)new JsonObject { ["id"] = a.Id.ToString(), ["name"] = a.Name })]),
        };
    }

    private static ImportMode ParseMode(string text, string path) =>
        Enum.TryParse<ImportMode>(text.Trim(), ignoreCase: true, out var mode)
            ? mode
            : throw ApiException.BadRequest($"Unknown {path} '{text}'", [Problem(path, "Use overwrite, addAsNew or skip")]);

    private static JsonObject PlanJson(ImportPlan plan) => new()
    {
        ["source"] = plan.Source.ToString(),
        ["header"] = JsonSerializer.SerializeToNode(plan.Bundle.Header, JsonOptions),
        ["items"] = new JsonArray([.. plan.Items.Select(i => (JsonNode)new JsonObject
        {
            ["kind"] = i.Kind.ToString(),
            ["sourceId"] = i.SourceId?.ToString(),
            ["name"] = i.Name,
            ["group"] = i.Group,
            ["conflict"] = i.Conflict.ToString(),
            ["conflictTargetName"] = i.ConflictTargetName,
            ["include"] = i.Include,
            ["mode"] = i.Mode.ToString(),
            ["containsCode"] = i.ContainsCode,
            ["placeholderCount"] = i.PlaceholderCount,
            ["details"] = CatalogJson.Strings(i.Details),
            ["warnings"] = CatalogJson.Strings(i.Warnings),
        })]),
        ["warnings"] = CatalogJson.Strings(plan.Report.Warnings),
        ["mapped"] = CatalogJson.Strings(plan.Report.Mapped),
        ["placeholders"] = plan.Report.Placeholders.Count,
        ["containsCode"] = plan.Items.Any(i => i.ContainsCode),
    };

    /// <summary>A Cuebot import string (CUEBOT1:...) for the named items (ids or names); dependencies by default.</summary>
    public JsonObject Export(IEnumerable<string>? actions, IEnumerable<string>? commands, IEnumerable<string>? timers,
        IEnumerable<string>? globals, bool includeDependencies = true, string? name = null)
    {
        var selection = new ExportSelection { IncludeDependencies = includeDependencies };
        selection.Header.Name = string.IsNullOrWhiteSpace(name) ? "Cuebot export" : name.Trim();
        selection.Header.Generator = "Cuebot API";
        foreach (var a in Clean(actions)) selection.ActionIds.Add(FindAction(a).Id);
        foreach (var c in Clean(commands)) selection.CommandIds.Add(FindCommand(c).Id);
        foreach (var t in Clean(timers)) selection.TimerIds.Add(FindTimer(t).Id);
        foreach (var g in Clean(globals)) selection.GlobalNames.Add(GetGlobal(g)["name"]!.GetValue<string>());
        if (selection.ActionIds.Count + selection.CommandIds.Count + selection.TimerIds.Count + selection.GlobalNames.Count == 0)
            throw ApiException.BadRequest("Nothing selected: name at least one action, command, timer or global");
        var bundle = Transfer.Export(selection);
        return new JsonObject
        {
            ["data"] = TransferCodec.Encode(bundle),
            ["actions"] = bundle.Actions.Count,
            ["queues"] = bundle.Queues.Count,
            ["commands"] = bundle.Commands.Count,
            ["timers"] = bundle.Timers.Count,
            ["globals"] = bundle.Globals.Count,
        };

        static IEnumerable<string> Clean(IEnumerable<string>? values) =>
            (values ?? []).SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    // ---- JSON helpers ----------------------------------------------------------------------------------------------

    public static JsonObject ToObject<T>(T value) => JsonSerializer.SerializeToNode(value, JsonOptions)!.AsObject();

    private static JsonObject Saved<T>(string key, T value, JsonArray warnings)
    {
        var json = new JsonObject { [key] = ToObject(value) };
        if (warnings.Count > 0) json["warnings"] = warnings;
        return json;
    }

    /// <summary>Deserializes a request object; malformed or mistyped JSON becomes a 400 with the JSON path.</summary>
    public static T FromNode<T>(JsonNode? node, string what) where T : class
    {
        if (node is not JsonObject) throw ApiException.BadRequest($"Expected a JSON object ({what})");
        try
        {
            return node.Deserialize<T>(JsonOptions) ?? throw ApiException.BadRequest($"Expected a JSON object ({what})");
        }
        catch (JsonException ex)
        {
            throw ApiException.BadRequest($"Invalid {what}: {ex.Message}", [Problem(ex.Path ?? "$", ex.Message)]);
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or NotSupportedException)
        {
            throw ApiException.BadRequest($"Invalid {what}: {ex.Message}");
        }
    }

    private static JsonObject Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch)
        {
            var existing = target.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Key;
            if (existing is not null) target.Remove(existing);
            target[key] = value?.DeepClone();
        }
        return target;
    }

    public static JsonObject Problem(string path, string message) => new() { ["path"] = path, ["message"] = message };

    /// <summary>A stored or argument value as JSON: primitives as themselves, anything else as text.</summary>
    public static JsonNode? Value(object? value) => value switch
    {
        null => null,
        string s => s,
        bool b => b,
        long l => l,
        int i => i,
        double d when double.IsFinite(d) => d,
        decimal m => m,
        float f when float.IsFinite(f) => f,
        DateTimeOffset t => t.ToString("o"),
        DateTime t => t.ToString("o"),
        JsonElement e => JsonNode.Parse(e.GetRawText()),
        JsonNode n => n.DeepClone(),
        _ => VariableResolver.Format(value),
    };

    public static JsonObject Args(IReadOnlyDictionary<string, object?> args)
    {
        var json = new JsonObject();
        foreach (var (key, value) in args.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)) json[key] = Value(value);
        return json;
    }
}
