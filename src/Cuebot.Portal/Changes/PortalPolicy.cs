using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;
using Cuebot.Core.Storage;
using Cuebot.Portal.Members;

namespace Cuebot.Portal.Changes;

/// <summary>
/// What a role may change (docs/MOD_PORTAL_PLAN.md, section 5). Every check returns the problems in words for the
/// mod; an empty list means allowed. Cuebot checks every request, whatever the portal showed.
/// </summary>
public static class PortalPolicy
{
    public static bool Has(PortalRole? role, PortalPermission permission) => role?.Permissions.Contains(permission) == true;

    /// <summary>True when the role's group scope covers <paramref name="group"/> (an empty scope covers all).</summary>
    public static bool InScope(PortalRole? role, string group) =>
        role is not null && (role.Groups.Count == 0 || role.Groups.Contains(group, StringComparer.OrdinalIgnoreCase));

    public static IReadOnlyList<string> CheckRun(PortalRole? role, ActionDefinition action)
    {
        var problems = new List<string>();
        if (!Has(role, PortalPermission.RunActions)) problems.Add("Your role cannot run actions.");
        if (!InScope(role, action.Group)) problems.Add($"Your role cannot use the group '{action.Group}'.");
        return problems;
    }

    public static IReadOnlyList<string> CheckAction(PortalRole? role, ActionDefinition? before, ActionDefinition? after)
    {
        var problems = new List<string>();
        if (role is null) return ["You have no role."];
        var needed = before is null ? PortalPermission.CreateActions : after is null ? PortalPermission.DeleteActions : PortalPermission.EditActions;
        if (!Has(role, needed)) problems.Add(needed switch
        {
            PortalPermission.CreateActions => "Your role cannot create actions.",
            PortalPermission.DeleteActions => "Your role cannot delete actions.",
            _ => "Your role cannot change actions.",
        });
        foreach (var a in new[] { before, after })
            if (a is not null && !InScope(role, a.Group)) problems.Add($"Your role cannot use the group '{a.Group}'.");

        if (before is not null && Steps(before.SubActions).Any(s => StepRiskCatalog.ForStep(s.Type) == StepRisk.Never))
        {
            problems.Add("This action has steps that run code, programs or files, so it can only be changed on the streamer's computer.");
            return problems;
        }

        var beforeSteps = before is null ? [] : Steps(before.SubActions).ToDictionary(s => s.Id);
        var afterSteps = after is null ? [] : Steps(after.SubActions).ToDictionary(s => s.Id);
        foreach (var s in afterSteps.Values)
        {
            var risk = StepRiskCatalog.ForStep(s.Type);
            if (risk > role.MaxRisk && !(beforeSteps.TryGetValue(s.Id, out var old) && SameStep(old, s)))
                problems.Add(Describe(s.Type, risk, "add or change"));
        }
        if (after is not null)
        {
            foreach (var s in beforeSteps.Values)
            {
                var risk = StepRiskCatalog.ForStep(s.Type);
                if (risk > role.MaxRisk && !afterSteps.ContainsKey(s.Id)) problems.Add(Describe(s.Type, risk, "remove"));
            }
        }

        var beforeTriggers = before?.Triggers.ToDictionary(t => t.Id) ?? [];
        foreach (var t in after?.Triggers ?? [])
        {
            var risk = StepRiskCatalog.ForTrigger(t.Type);
            if (risk > role.MaxRisk && !(beforeTriggers.TryGetValue(t.Id, out var old) && Same(old, t)))
                problems.Add(Describe(t.Type, risk, "add or change the trigger"));
        }
        if (after is not null)
        {
            var afterTriggers = after.Triggers.Select(t => t.Id).ToHashSet();
            foreach (var t in beforeTriggers.Values)
            {
                var risk = StepRiskCatalog.ForTrigger(t.Type);
                if (risk > role.MaxRisk && !afterTriggers.Contains(t.Id)) problems.Add(Describe(t.Type, risk, "remove the trigger"));
            }
        }
        return [.. problems.Distinct()];
    }

    public static IReadOnlyList<string> CheckCommand(PortalRole? role, CommandDefinition? before, CommandDefinition? after)
    {
        if (role is null) return ["You have no role."];
        var problems = new List<string>();
        var needed = before is null ? PortalPermission.CreateCommands : after is null ? PortalPermission.DeleteCommands : PortalPermission.EditCommands;
        if (!Has(role, needed)) problems.Add(needed switch
        {
            PortalPermission.CreateCommands => "Your role cannot create commands.",
            PortalPermission.DeleteCommands => "Your role cannot delete commands.",
            _ => "Your role cannot change commands.",
        });
        foreach (var c in new[] { before, after })
            if (c is not null && !InScope(role, c.Group)) problems.Add($"Your role cannot use the group '{c.Group}'.");
        return [.. problems.Distinct()];
    }

    /// <summary>Marks the steps of <paramref name="after"/> that are new or changed as edited by the member.</summary>
    public static void StampEdits(ActionDefinition? before, ActionDefinition after, Guid memberId)
    {
        var old = before is null ? [] : Steps(before.SubActions).ToDictionary(s => s.Id);
        foreach (var s in Steps(after.SubActions))
        {
            if (old.TryGetValue(s.Id, out var o) && SameStep(o, s)) s.EditedBy = o.EditedBy;
            else s.EditedBy = memberId;
        }
    }

    /// <summary>Every step of a chain, branches included.</summary>
    public static IEnumerable<SubActionDefinition> Steps(IEnumerable<SubActionDefinition> chain)
    {
        foreach (var s in chain)
        {
            yield return s;
            foreach (var branch in s.Branches.Values)
                foreach (var child in Steps(branch)) yield return child;
        }
    }

    /// <summary>The same step setting for setting, ignoring its children, position and edit mark.</summary>
    private static bool SameStep(SubActionDefinition a, SubActionDefinition b) =>
        a.Type == b.Type && a.Enabled == b.Enabled && a.Weight.Equals(b.Weight)
        && a.Parameters.Count == b.Parameters.Count
        && a.Parameters.All(p => b.Parameters.TryGetValue(p.Key, out var v) && v == p.Value);

    private static bool Same<T>(T a, T b) => JsonSerializer.Serialize(a, Json.Options) == JsonSerializer.Serialize(b, Json.Options);

    private static string Describe(string type, StepRisk risk, string verb) => risk == StepRisk.Never
        ? $"Mods can never {verb} '{type}': it runs code, programs or files."
        : $"Your role cannot {verb} '{type}' (it needs {Level(risk)}).";

    public static string Level(StepRisk risk) => risk switch
    {
        StepRisk.Basic => "level 1, Basic",
        StepRisk.StreamControl => "level 2, Stream control",
        StepRisk.ShowCritical => "level 3, Show-critical",
        StepRisk.TrustedTech => "level 4, Trusted tech",
        _ => "the streamer",
    };
}
