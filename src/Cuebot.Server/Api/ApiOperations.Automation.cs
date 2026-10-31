using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Overlays;
using Cuebot.Core.Overlays.Events;
using Cuebot.Server.StreamerBot;

namespace Cuebot.Server.Api;

public sealed partial class ApiOperations
{
    // ---- Running things now ----------------------------------------------------------------------------------------

    /// <summary>
    /// Runs sub-actions once, without saving an action: any sub-action type the catalogue has (switch an OBS scene, set
    /// the stream title, speak, ban, change a light...). They are validated like an action's and run in
    /// <paramref name="queue"/> (default queue when empty). Waits for the run like <see cref="RunActionAsync"/>.
    /// </summary>
    public async Task<JsonObject> RunSubActionsAsync(JsonNode? subActions, JsonNode? args, string? queue, double timeoutSeconds, string source,
        CancellationToken ct = default)
    {
        if (args is not null and not JsonObject) throw ApiException.BadRequest("args must be a JSON object");
        var (action, validation) = OneOffAction(subActions, queue, source);
        var steps = action.SubActions;
        var runArgs = StreamerBotRequests.ToArgs(args is null ? default : JsonSerializer.SerializeToElement(args));
        services.Log.Info(LogSource, $"{source} runs {steps.Count} sub-action(s): {string.Join(", ", steps.Select(s => s.Type))}");
        var started = DateTimeOffset.Now;
        var run = services.Engine.RunAsync(action, runArgs, CancellationToken.None, source);
        try
        {
            var success = await run.WaitAsync(TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 300)), ct).ConfigureAwait(false);
            var entry = services.Engine.History.Entries.FirstOrDefault(e => e.ActionId == action.Id && e.QueuedAt >= started.AddSeconds(-1));
            var result = new JsonObject { ["completed"] = true, ["success"] = success };
            if (entry is not null) result["run"] = RunJson(entry, includeArgs: true);
            if (validation.Warnings.Count > 0) result["warnings"] = validation.Warnings.DeepClone();
            return result;
        }
        catch (TimeoutException)
        {
            return new JsonObject { ["completed"] = false, ["message"] = "Still running (or waiting in its queue); check get_history later" };
        }
    }

    /// <summary>The sub-actions' type ids when they are valid; throws with the problems otherwise.</summary>
    public IReadOnlyList<string> ValidateSubActions(JsonNode? subActions) =>
        [.. OneOffAction(subActions, null, "check").Action.SubActions.Select(s => s.Type)];

    private (ActionDefinition Action, ActionValidator.Result Validation) OneOffAction(JsonNode? subActions, string? queue, string source)
    {
        if (subActions is not JsonArray steps || steps.Count == 0) throw ApiException.BadRequest("subActions must be a list of at least one sub-action");
        var action = FromNode<ActionDefinition>(new JsonObject
        {
            ["name"] = $"{source}: run sub-actions",
            ["queue"] = queue ?? "",
            ["concurrent"] = true,
            ["subActions"] = steps.DeepClone(),
        }, "subActions");
        return (action, ValidateOrThrow(action));
    }

    /// <summary>
    /// Runs a saved action as if one of its triggers fired (<c>isTest</c> set), with the trigger's arguments plus
    /// <paramref name="args"/>. <paramref name="trigger"/> is the trigger's type id or its number from 1; default the first.
    /// </summary>
    public async Task<JsonObject> TestTriggerAsync(string idOrName, string? trigger, JsonNode? args, CancellationToken ct = default)
    {
        var action = FindAction(idOrName);
        if (args is not null and not JsonObject) throw ApiException.BadRequest("args must be a JSON object");
        TriggerDefinition? chosen = null;
        if (!string.IsNullOrWhiteSpace(trigger))
        {
            chosen = int.TryParse(trigger, out var n) && n >= 1 && n <= action.Triggers.Count
                ? action.Triggers[n - 1]
                : action.Triggers.FirstOrDefault(t => string.Equals(t.Type, trigger.Trim(), StringComparison.OrdinalIgnoreCase))
                  ?? throw ApiException.NotFound($"Action '{action.Name}' has no trigger '{trigger}'");
        }
        else chosen = action.Triggers.FirstOrDefault();
        var runArgs = StreamerBotRequests.ToArgs(args is null ? default : JsonSerializer.SerializeToElement(args));
        services.Log.Info(LogSource, $"API tests action '{action.Name}' ({chosen?.Type ?? "no trigger"})");
        var success = await services.Engine.TestTriggerAsync(action, chosen, runArgs, ct).WaitAsync(TimeSpan.FromSeconds(60), ct).ConfigureAwait(false);
        return new JsonObject { ["action"] = action.Name, ["trigger"] = chosen?.Type, ["success"] = success };
    }

    /// <summary>The sample events <see cref="Emulate"/> can send, by platform, with each one's arguments (what overrides can change).</summary>
    public static JsonObject EmulatableEvents() => new()
    {
        ["platforms"] = new JsonObject([.. OverlayEmulator.Kinds.Select(k => KeyValuePair.Create(k.Key, (JsonNode?)new JsonObject([.. k.Value.Select(kind =>
        {
            var sample = OverlayEmulator.Create(k.Key, kind)!;
            var args = Args(sample.Args);
            foreach (var key in new[] { "user", "userId", "userName", "isTest", "messageId", "platform" }) args.Remove(key);
            return KeyValuePair.Create(kind, (JsonNode?)new JsonObject { ["event"] = sample.Type, ["args"] = args });
        })])))]),
    };

    /// <summary>
    /// Sends a sample platform event (a follow, sub, cheer, raid, tip, chat message...) with a made-up viewer.
    /// <paramref name="target"/> "overlays" (default) shows it on every overlay only, like the widget editor's Emulate;
    /// "everything" publishes it as a real event, so actions with matching triggers run too (they may post to chat).
    /// <paramref name="overrides"/> replace sample arguments (amount, user, message, tier...).
    /// </summary>
    public JsonObject Emulate(string platform, string kind, string? target, JsonObject? overrides = null)
    {
        platform = platform.Trim().ToLowerInvariant();
        kind = kind.Trim().ToLowerInvariant();
        var e = OverlayEmulator.Create(platform, kind)
                ?? throw ApiException.BadRequest($"No sample '{kind}' for '{platform}'. Use list_emulated_events for the choices.");
        if (overrides is { Count: > 0 })
        {
            // e.g. {"amount": 75} to try a variation for big raids, or {"user": "Alice", "message": "hi"}.
            var args = new Dictionary<string, object?>(e.Args, StringComparer.OrdinalIgnoreCase);
            foreach (var (key, value) in StreamerBotRequests.ToArgs(JsonSerializer.SerializeToElement(overrides))) args[key] = value;
            e = new Cuebot.Core.Events.BotEvent(e.Type, args);
        }
        var everything = string.Equals(target, "everything", StringComparison.OrdinalIgnoreCase);
        if (everything)
        {
            services.Bus.Publish(e);
        }
        else
        {
            var bridge = services.OverlayBridge ?? throw ApiException.Unavailable("Overlays are not available");
            var feed = services.OverlayFeed ?? throw ApiException.Unavailable("Overlays are not available");
            foreach (var (listener, data) in bridge.Convert(e)) feed.Publish(OverlayMessage.Event(listener, data));
        }
        services.Log.Info(LogSource, $"API emulated {platform} {kind} ({(everything ? "everything" : "overlays only")})");
        return new JsonObject { ["sent"] = true, ["event"] = e.Type, ["target"] = everything ? "everything" : "overlays", ["args"] = Args(e.Args) };
    }

    // ---- Queues ----------------------------------------------------------------------------------------------------

    /// <summary>Every action queue with its settings and live state (paused, waiting, running).</summary>
    public JsonObject Queues()
    {
        var states = services.Engine.Queues.Queues;
        var defined = Profile.Snapshot.Queues;
        return new JsonObject
        {
            ["queues"] = new JsonArray([.. states.Select(q => (JsonNode)new JsonObject
            {
                ["id"] = defined.FirstOrDefault(d => string.Equals(d.Name, q.Name, StringComparison.OrdinalIgnoreCase))?.Id.ToString(),
                ["name"] = q.Name,
                ["blocking"] = q.Blocking,
                ["paused"] = q.Paused,
                ["waiting"] = q.Pending,
                ["running"] = q.Running,
                ["isDefault"] = q.IsDefault,
            })]),
        };
    }

    /// <summary>Adds a queue (actions name it in their <c>queue</c> field); blocking runs its actions one at a time.</summary>
    public async Task<JsonObject> CreateQueueAsync(string name, bool blocking)
    {
        name = name.Trim();
        if (name.Length == 0) throw ApiException.BadRequest("The queue needs a name");
        if (string.Equals(name, QueueService.DefaultQueue, StringComparison.OrdinalIgnoreCase)
            || Profile.Snapshot.Queues.Any(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw ApiException.Conflict($"A queue named '{name}' already exists");
        await Profile.EditAsync(p => p.Queues.Add(new QueueDefinition { Name = name, Blocking = blocking })).ConfigureAwait(false);
        services.Log.Info(LogSource, $"Queue '{name}' created");
        return Queues();
    }

    /// <summary>Renames a queue (actions using it move with it) and/or changes whether it is blocking.</summary>
    public async Task<JsonObject> UpdateQueueAsync(string queue, string? newName, bool? blocking)
    {
        var existing = FindQueue(queue);
        var name = newName?.Trim();
        if (name is { Length: > 0 } && !string.Equals(name, existing.Name, StringComparison.OrdinalIgnoreCase)
            && (string.Equals(name, QueueService.DefaultQueue, StringComparison.OrdinalIgnoreCase)
                || Profile.Snapshot.Queues.Any(q => string.Equals(q.Name, name, StringComparison.OrdinalIgnoreCase))))
            throw ApiException.Conflict($"A queue named '{name}' already exists");
        await Profile.EditAsync(p =>
        {
            var q = p.Queues.First(x => x.Id == existing.Id);
            if (name is { Length: > 0 })
            {
                foreach (var action in p.Actions.Where(a => string.Equals(a.Queue, q.Name, StringComparison.OrdinalIgnoreCase))) action.Queue = name;
                q.Name = name;
            }
            if (blocking is { } b) q.Blocking = b;
        }).ConfigureAwait(false);
        return Queues();
    }

    /// <summary>Deletes a queue; its actions move to the default queue.</summary>
    public async Task<JsonObject> DeleteQueueAsync(string queue)
    {
        var existing = FindQueue(queue);
        await Profile.EditAsync(p =>
        {
            foreach (var action in p.Actions.Where(a => string.Equals(a.Queue, existing.Name, StringComparison.OrdinalIgnoreCase))) action.Queue = "";
            p.Queues.RemoveAll(q => q.Id == existing.Id);
        }).ConfigureAwait(false);
        services.Log.Info(LogSource, $"Queue '{existing.Name}' deleted");
        return Queues();
    }

    /// <summary><c>pause</c>, <c>resume</c> or <c>clear</c> (cancels the waiting runs) one queue, or every queue with "all".</summary>
    public JsonObject QueueControl(string queue, string command)
    {
        var queues = services.Engine.Queues;
        var all = string.Equals(queue.Trim(), "all", StringComparison.OrdinalIgnoreCase);
        var names = all ? queues.Queues.Select(q => q.Name).ToList() : [QueueName(queue)];
        var cleared = 0;
        switch (command)
        {
            case "pause": if (all) queues.PauseAll(); else queues.Pause(names[0]); break;
            case "resume": if (all) queues.ResumeAll(); else queues.Resume(names[0]); break;
            case "clear": cleared = names.Sum(queues.Clear); break;
            default: throw ApiException.BadRequest($"Unknown queue command '{command}' (pause, resume or clear)");
        }
        var result = Queues();
        if (command == "clear") result["cleared"] = cleared;
        return result;
    }

    private QueueDefinition FindQueue(string queue)
    {
        var text = queue.Trim();
        var snapshot = Profile.Snapshot.Queues;
        return (Guid.TryParse(text, out var id) ? snapshot.FirstOrDefault(q => q.Id == id) : null)
               ?? snapshot.FirstOrDefault(q => string.Equals(q.Name, text, StringComparison.OrdinalIgnoreCase))
               ?? throw ApiException.NotFound(string.Equals(text, QueueService.DefaultQueue, StringComparison.OrdinalIgnoreCase)
                   ? "The default queue cannot be changed or deleted" : $"Queue '{text}' not found");
    }

    private string QueueName(string queue)
    {
        var text = queue.Trim();
        if (text.Length == 0 || string.Equals(text, QueueService.DefaultQueue, StringComparison.OrdinalIgnoreCase)) return QueueService.DefaultQueue;
        return FindQueue(text).Name;
    }
}
