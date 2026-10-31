using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;

namespace Cuebot.Assistant;

/// <summary>
/// What one assistant turn changed in actions, commands and timers, so the streamer can undo it in one click. The
/// Actions page's own undo does not cover these (outside changes become its new starting point), and small local
/// models in particular change the wrong thing now and then. Undo restores each item only if nobody changed it since
/// the turn; anything changed afterwards is kept and named.
/// </summary>
public sealed class AssistantUndo
{
    private readonly ProfileService _profile;
    private readonly CommandStore? _commands;
    private readonly Snapshot _before;
    private readonly Snapshot _after;

    private AssistantUndo(ProfileService profile, CommandStore? commands, Snapshot before, Snapshot after)
    {
        _profile = profile;
        _commands = commands;
        _before = before;
        _after = after;
        Summary = Describe(before, after);
    }

    /// <summary>What the turn changed, e.g. "Created the action 'Lurk' and the command 'Lurk'."</summary>
    public string Summary { get; }

    /// <summary>The state before a turn, to compare with afterwards.</summary>
    public static Snapshot Capture(ProfileService profile, CommandStore? commands) => new(
        profile.Snapshot.Actions.ToDictionary(a => a.Id, a => (a.Name, Json(a))),
        profile.Snapshot.Timers.ToDictionary(t => t.Id, t => (t.Name, Json(t))),
        (commands?.Snapshot ?? []).ToDictionary(c => c.Id, c => (c.Name, Json(c))));

    /// <summary>The changes since <paramref name="before"/>, or null when the turn changed none of them.</summary>
    public static AssistantUndo? Since(Snapshot before, ProfileService profile, CommandStore? commands)
    {
        var after = Capture(profile, commands);
        return before.SameAs(after) ? null : new AssistantUndo(profile, commands, before, after);
    }

    /// <summary>Puts back what the turn changed and says what happened.</summary>
    public async Task<string> UndoAsync()
    {
        var now = Capture(_profile, _commands);
        var done = new List<string>();
        var kept = new List<string>();

        var actions = Plan(_before.Actions, _after.Actions, now.Actions, "action", done, kept);
        var timers = Plan(_before.Timers, _after.Timers, now.Timers, "timer", done, kept);
        if (actions.Count > 0 || timers.Count > 0)
        {
            await _profile.EditAsync(p =>
            {
                Apply(p.Actions, actions, a => a.Id);
                Apply(p.Timers, timers, t => t.Id);
            }).ConfigureAwait(false);
        }
        if (_commands is not null)
        {
            foreach (var (id, json) in Plan(_before.Commands, _after.Commands, now.Commands, "command", done, kept))
            {
                if (json is null) _commands.Remove(id);
                else _commands.Save(JsonSerializer.Deserialize<CommandDefinition>(json, Cuebot.Core.Storage.Json.Options)!);
            }
        }

        var result = done.Count == 0 ? "Nothing to undo." : "Undone: " + string.Join("; ", done) + ".";
        if (kept.Count > 0) result += " Kept " + string.Join(", ", kept) + " because it was changed after the assistant.";
        return result;
    }

    /// <summary>For each item the turn changed and nobody changed since: its JSON from before the turn (null = remove it).</summary>
    private static Dictionary<Guid, string?> Plan(Dictionary<Guid, (string Name, string Json)> before, Dictionary<Guid, (string Name, string Json)> after,
        Dictionary<Guid, (string Name, string Json)> now, string kind, List<string> done, List<string> kept)
    {
        var plan = new Dictionary<Guid, string?>();
        foreach (var id in before.Keys.Union(after.Keys))
        {
            var had = before.TryGetValue(id, out var b);
            var made = after.TryGetValue(id, out var a);
            if (had && made && b.Json == a.Json) continue;   // the turn did not touch it
            var exists = now.TryGetValue(id, out var current);
            var untouchedSince = made ? exists && current.Json == a.Json : !exists;
            var name = (made ? a.Name : b.Name) ?? "";
            if (!untouchedSince)
            {
                kept.Add($"the {kind} '{name}'");
                continue;
            }
            plan[id] = had ? b.Json : null;
            done.Add(!had ? $"removed the {kind} '{name}'" : !made ? $"brought back the {kind} '{b.Name}'" : $"put back the {kind} '{b.Name}'");
        }
        return plan;
    }

    private static void Apply<T>(List<T> list, Dictionary<Guid, string?> plan, Func<T, Guid> id)
    {
        foreach (var (key, json) in plan)
        {
            var index = list.FindIndex(x => id(x) == key);
            if (json is null)
            {
                if (index >= 0) list.RemoveAt(index);
                continue;
            }
            var restored = JsonSerializer.Deserialize<T>(json, Cuebot.Core.Storage.Json.Options)!;
            if (index >= 0) list[index] = restored;
            else list.Add(restored);
        }
    }

    private static string Describe(Snapshot before, Snapshot after)
    {
        var parts = new List<string>();
        void Count(Dictionary<Guid, (string Name, string Json)> b, Dictionary<Guid, (string Name, string Json)> a, string kind)
        {
            var created = a.Keys.Except(b.Keys).Select(id => a[id].Name).ToList();
            var deleted = b.Keys.Except(a.Keys).Select(id => b[id].Name).ToList();
            var changed = a.Keys.Intersect(b.Keys).Where(id => a[id].Json != b[id].Json).Select(id => a[id].Name).ToList();
            if (created.Count > 0) parts.Add($"created {Names(kind, created)}");
            if (changed.Count > 0) parts.Add($"changed {Names(kind, changed)}");
            if (deleted.Count > 0) parts.Add($"deleted {Names(kind, deleted)}");
        }
        Count(before.Actions, after.Actions, "action");
        Count(before.Commands, after.Commands, "command");
        Count(before.Timers, after.Timers, "timer");
        return "The assistant " + string.Join(", ", parts) + ".";
    }

    private static string Names(string kind, List<string> names) =>
        names.Count == 1 ? $"the {kind} '{names[0]}'" : $"{names.Count} {kind}s ({string.Join(", ", names.Select(n => $"'{n}'"))})";

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, Cuebot.Core.Storage.Json.Options);

    /// <summary>Actions, timers and commands by id, as JSON.</summary>
    public sealed record Snapshot(
        Dictionary<Guid, (string Name, string Json)> Actions,
        Dictionary<Guid, (string Name, string Json)> Timers,
        Dictionary<Guid, (string Name, string Json)> Commands)
    {
        internal bool SameAs(Snapshot other) => Same(Actions, other.Actions) && Same(Timers, other.Timers) && Same(Commands, other.Commands);

        private static bool Same(Dictionary<Guid, (string Name, string Json)> a, Dictionary<Guid, (string Name, string Json)> b) =>
            a.Count == b.Count && a.All(kv => b.TryGetValue(kv.Key, out var other) && other.Json == kv.Value.Json);
    }
}

/// <summary>The turn changed actions, commands or timers; <see cref="Undo"/> puts them back.</summary>
public sealed record AssistantChanges(AssistantUndo Undo) : AssistantUpdate;
