using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Portal.Assistant;
using Cuebot.Portal.Changes;
using Cuebot.Portal.Members;
using Cuebot.Portal.Overlays;
using Cuebot.Portal.Uploads;
using Cuebot.Portal.Web;

namespace Cuebot.Portal;

/// <summary>
/// What a signed-in mod device may ask for, one JSON request at a time: <c>{"op": "...", ...}</c> in,
/// <c>{"ok": true, ...}</c> or <c>{"ok": false, "error": "..."}</c> out. The transport (the encrypted relay link,
/// docs/MOD_PORTAL_PLAN.md phase 2) hands over the member it authenticated; every request re-reads the member's role,
/// so a change of role or a suspension applies at once.
/// </summary>
public sealed class PortalApi(PortalDirectory directory, PortalChanges changes, ProfileService profile, CommandStore commands,
    ActionEngine engine, Registry registry, PortalControls? controls = null, PortalUploads? uploads = null,
    PortalOverlays? overlays = null, PortalAssistant? assistant = null)
{
    private readonly PortalFiles _files = new(uploads?.Store);

    public async Task<JsonObject> HandleAsync(Guid memberId, JsonObject request)
    {
        var member = directory.Member(memberId);
        if (member is null || member.Suspended) return Error("Your access was removed or paused by the streamer.");
        var role = directory.RoleOf(member);
        try
        {
            var name = (string?)request["op"] ?? "";
            if (controls is not null && PortalControls.Handles(name)) return await controls.HandleAsync(member, role, name, request).ConfigureAwait(false);
            if (uploads is not null && PortalUploads.Handles(name)) return await uploads.HandleAsync(member, role, name, request).ConfigureAwait(false);
            if (overlays is not null && PortalOverlays.Handles(name)) return await overlays.HandleAsync(member, role, name, request).ConfigureAwait(false);
            if (PortalAssistant.Handles(name))
                return assistant is null ? Error("This Cuebot has no assistant.") : await assistant.HandleAsync(member, role, name, request).ConfigureAwait(false);
            return name switch
            {
                "me" => Me(member, role),
                "ui" => PortalUi.Js.Length > 0 ? Ok(new JsonObject { ["js"] = PortalUi.Js, ["css"] = PortalUi.Css }) : Error("This Cuebot has no portal UI."),
                "steps.catalog" => StepCatalog(role),
                "actions.list" => Ok(new JsonObject { ["actions"] = new JsonArray([.. VisibleActions(role).Select(Summary)]) }),
                "actions.get" => FindAction(role, request) is { } a ? Ok(new JsonObject { ["action"] = PortalChanges.ToNode(_files.ToPortal(a)) }) : NotFound(),
                "actions.run" => await RunAsync(member, role, request).ConfigureAwait(false),
                "actions.save" => await SaveActionAsync(member, role, request).ConfigureAwait(false),
                "actions.delete" => await DeleteActionAsync(member, role, request).ConfigureAwait(false),
                "commands.list" => Ok(new JsonObject
                {
                    ["commands"] = new JsonArray([.. commands.All().Where(c => PortalPolicy.InScope(role, c.Group)).Select(c => PortalChanges.ToNode(c))]),
                }),
                "commands.save" => await SaveCommandAsync(member, role, request).ConfigureAwait(false),
                "commands.delete" => await DeleteCommandAsync(member, role, request).ConfigureAwait(false),
                "changes.mine" => Ok(new JsonObject
                {
                    ["changes"] = new JsonArray([.. changes.All.Where(c => c.MemberId == member.Id).TakeLast(100).Select(ForPortal)]),
                }),
                "changes.undo" => await UndoAsync(member, request).ConfigureAwait(false),
                var op => Error($"Unknown request '{op}'."),
            };
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException or System.Text.Json.JsonException or ArgumentException)
        {
            return Error("That request was not understood: " + e.Message);
        }
    }

    private JsonObject Me(PortalMember member, PortalRole? role) => Ok(new JsonObject
    {
        ["name"] = member.Name,
        ["role"] = role?.Name ?? "",
        ["maxRisk"] = (int)(role?.MaxRisk ?? StepRisk.Basic),
        ["permissions"] = new JsonArray([.. (role?.Permissions ?? []).Select(p => (JsonNode)p.ToString())]),
        ["changes"] = directory.ChangeModeOf(member).ToString(),
        ["groups"] = new JsonArray([.. (role?.Groups ?? []).Select(g => (JsonNode)g)]),
    });

    /// <summary>The step and trigger types the member's role may use, with their settings, for the portal's editor.</summary>
    private JsonObject StepCatalog(PortalRole? role)
    {
        var max = role?.MaxRisk ?? StepRisk.Basic;
        JsonArray Params(IEnumerable<ParameterDescriptor> ps, string type = "") => new([.. ps.Select(p =>
        {
            var param = new JsonObject
            {
                ["key"] = p.Key, ["label"] = p.Label, ["kind"] = p.Kind.ToString(), ["default"] = p.DefaultValue,
                ["choices"] = new JsonArray([.. p.Choices.Select(c => (JsonNode)c)]), ["help"] = p.Help,
            };
            // A file setting: the editor offers uploads of these kinds (none: the uploaded sounds folder).
            if (PortalFiles.Settings.TryGetValue(type, out var file) && file.Key == p.Key)
            {
                param["upload"] = new JsonArray([.. file.Kinds.Select(k => (JsonNode)k.ToString())]);
                param["help"] = file.Kinds.Length == 0 ? "Plays one of the uploaded sounds at random." : "Pick an upload (Uploads page).";
            }
            return (JsonNode)param;
        })]);
        return Ok(new JsonObject
        {
            ["steps"] = new JsonArray([.. registry.SubActions.Where(h => StepRiskCatalog.ForStep(h.TypeId) <= max).Select(h => (JsonNode)new JsonObject
            {
                ["type"] = h.TypeId, ["name"] = h.DisplayName, ["category"] = h.Category, ["risk"] = (int)StepRiskCatalog.ForStep(h.TypeId),
                ["branches"] = new JsonArray([.. h.Branches.Select(b => (JsonNode)b)]), ["parameters"] = Params(h.Parameters, h.TypeId),
            })]),
            ["triggers"] = new JsonArray([.. registry.Triggers.Where(t => StepRiskCatalog.ForTrigger(t.TypeId) <= max).Select(t => (JsonNode)new JsonObject
            {
                ["type"] = t.TypeId, ["name"] = t.DisplayName, ["category"] = t.Category, ["parameters"] = Params(t.Parameters),
            })]),
        });
    }

    private IEnumerable<ActionDefinition> VisibleActions(PortalRole? role) =>
        profile.Snapshot.Actions.Where(a => PortalPolicy.InScope(role, a.Group));

    private static JsonObject Summary(ActionDefinition a) => new()
    {
        ["id"] = a.Id.ToString(),
        ["name"] = a.Name,
        ["group"] = a.Group,
        ["enabled"] = a.Enabled,
        ["steps"] = PortalPolicy.Steps(a.SubActions).Count(),
        ["locked"] = PortalPolicy.Steps(a.SubActions).Any(s => StepRiskCatalog.ForStep(s.Type) == StepRisk.Never),
        // The riskiest step's level (5 for steps only the streamer may change): running needs it, and the portal asks first from 3.
        ["risk"] = PortalPolicy.Steps(a.SubActions).Select(s => (int)StepRiskCatalog.ForStep(s.Type)).DefaultIfEmpty(0).Max(),
    };

    private ActionDefinition? FindAction(PortalRole? role, JsonObject request) =>
        Guid.TryParse((string?)request["id"], out var id) ? VisibleActions(role).FirstOrDefault(a => a.Id == id) : null;

    private Task<JsonObject> RunAsync(PortalMember member, PortalRole? role, JsonObject request)
    {
        if (FindAction(role, request) is not { } action) return Task.FromResult(NotFound());
        if (PortalPolicy.CheckRun(role, action) is { Count: > 0 } problems) return Task.FromResult(Refused(problems));
        // No arguments from the mod: the action runs exactly as the streamer set it up, like a deck button.
        _ = engine.RunAsync(action, source: $"Mod portal: {member.Name}");
        return Task.FromResult(Ok(new JsonObject { ["started"] = true }));
    }

    /// <summary>
    /// True when an action this command starts runs code, programs or files: changing who may use the command (or
    /// its text) would change who reaches those steps, so only the streamer may.
    /// </summary>
    private bool FeedsNeverSteps(CommandDefinition command) => profile.Snapshot.Actions.Any(a =>
        a.Triggers.Any(t => t.Type.StartsWith("command.", StringComparison.Ordinal) && t.Settings.TryGetValue("command", out var c)
            && (c == command.Id.ToString() || string.Equals(c, command.Name, StringComparison.OrdinalIgnoreCase)))
        && PortalPolicy.Steps(a.SubActions).Any(s => StepRiskCatalog.ForStep(s.Type) == StepRisk.Never));

    private async Task<JsonObject> SaveActionAsync(PortalMember member, PortalRole? role, JsonObject request)
    {
        if (request["action"] is not JsonObject node) return Error("No action given.");
        var after = PortalChanges.FromNode<ActionDefinition>(node);
        var before = profile.Snapshot.Actions.FirstOrDefault(a => a.Id == after.Id);
        _files.FromPortal(after);
        if (PortalPolicy.CheckAction(role, before, after).Concat(_files.Check(before, after)).ToList() is { Count: > 0 } problems) return Refused(problems);
        PortalPolicy.StampEdits(before, after, member.Id);
        return Submitted(await changes.SubmitAsync(member, ActionArea.AreaName, after.Id, PortalChanges.ToNode(after)).ConfigureAwait(false));
    }

    private async Task<JsonObject> DeleteActionAsync(PortalMember member, PortalRole? role, JsonObject request)
    {
        if (FindAction(role, request) is not { } before) return NotFound();
        if (PortalPolicy.CheckAction(role, before, null) is { Count: > 0 } problems) return Refused(problems);
        return Submitted(await changes.SubmitAsync(member, ActionArea.AreaName, before.Id, null).ConfigureAwait(false));
    }

    private async Task<JsonObject> SaveCommandAsync(PortalMember member, PortalRole? role, JsonObject request)
    {
        if (request["command"] is not JsonObject node) return Error("No command given.");
        var after = PortalChanges.FromNode<CommandDefinition>(node);
        var before = commands.Get(after.Id);
        if (PortalPolicy.CheckCommand(role, before, after, before is not null && FeedsNeverSteps(before)) is { Count: > 0 } problems) return Refused(problems);
        return Submitted(await changes.SubmitAsync(member, CommandArea.AreaName, after.Id, PortalChanges.ToNode(after)).ConfigureAwait(false));
    }

    private async Task<JsonObject> DeleteCommandAsync(PortalMember member, PortalRole? role, JsonObject request)
    {
        if (!Guid.TryParse((string?)request["id"], out var id) || commands.Get(id) is not { } before) return NotFound();
        if (PortalPolicy.CheckCommand(role, before, null, FeedsNeverSteps(before)) is { Count: > 0 } problems) return Refused(problems);
        return Submitted(await changes.SubmitAsync(member, CommandArea.AreaName, id, null).ConfigureAwait(false));
    }

    private async Task<JsonObject> UndoAsync(PortalMember member, JsonObject request)
    {
        if (!Guid.TryParse((string?)request["id"], out var id)) return NotFound();
        return await changes.UndoAsync(id, member).ConfigureAwait(false) switch
        {
            ChangeResult.Applied => Ok(new JsonObject()),
            ChangeResult.NotAllowed => Error("Your role can only undo your own changes."),
            ChangeResult.Conflict => Error("It was changed again since, so it cannot be undone automatically."),
            ChangeResult.NotPending => Error("That change is not live, so there is nothing to undo."),
            _ => NotFound(),
        };
    }

    /// <summary>A change as the portal sees it: overlay links (their tokens) stay on the streamer's computer.</summary>
    private static JsonNode ForPortal(PortalChange change)
    {
        var node = PortalChanges.ToNode(change);
        if (change.Area == OverlayArea.AreaName)
            foreach (var side in new[] { "before", "after" })
                (node[side] as JsonObject)?.Remove("token");
        return node;
    }

    private static JsonObject Submitted((ChangeResult Result, PortalChange Change) r) => Ok(new JsonObject
    {
        ["change"] = r.Change.Id.ToString(),
        ["status"] = r.Result == ChangeResult.Pending ? "pending" : "live",
    });

    private static JsonObject Ok(JsonObject body)
    {
        body["ok"] = true;
        return body;
    }

    private static JsonObject Error(string message) => new() { ["ok"] = false, ["error"] = message };

    private static JsonObject NotFound() => Error("Not found, or not something your role can see.");

    private static JsonObject Refused(IReadOnlyList<string> problems) => new()
    {
        ["ok"] = false,
        ["error"] = string.Join(" ", problems),
        ["problems"] = new JsonArray([.. problems.Select(p => (JsonNode)p)]),
    };
}
