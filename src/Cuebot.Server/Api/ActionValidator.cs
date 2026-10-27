using System.Text.Json.Nodes;
using Cuebot.Core.Actions;

namespace Cuebot.Server.Api;

/// <summary>
/// Checks an action against the registry before it is saved: unknown trigger or sub-action types and parameter values
/// that do not fit their descriptor (not a number, not true/false, not one of the choices) are errors; unknown
/// parameter keys and branches the step does not offer are warnings (imports and older files may carry them).
/// Also repairs the structure: empty or duplicate ids get new ones, null lists become empty, type ids get their
/// registered spelling.
/// </summary>
public static class ActionValidator
{
    public sealed record Result(JsonArray Errors, JsonArray Warnings)
    {
        public bool Ok => Errors.Count == 0;
    }

    public static Result Validate(ActionDefinition action, Registry registry)
    {
        var errors = new JsonArray();
        var warnings = new JsonArray();
        var ids = new HashSet<Guid>();

        if (string.IsNullOrWhiteSpace(action.Name)) Add(errors, "name", "The action needs a name");
        action.Name = (action.Name ?? "").Trim();
        action.Group ??= "";
        action.Queue ??= "";
        action.Triggers ??= [];
        action.SubActions ??= [];

        for (var i = 0; i < action.Triggers.Count; i++)
        {
            var path = $"triggers[{i}]";
            var trigger = action.Triggers[i];
            if (trigger is null)
            {
                Add(errors, path, "Trigger is null");
                continue;
            }
            trigger.Settings ??= [];
            if (trigger.Id == Guid.Empty || !ids.Add(trigger.Id)) ids.Add(trigger.Id = Guid.NewGuid());
            var type = registry.GetTrigger(trigger.Type ?? "");
            if (type is null)
            {
                Add(errors, path + ".type", $"Unknown trigger type '{trigger.Type}' (list them with GET /api/v1/triggers or list_trigger_types)");
                continue;
            }
            trigger.Type = type.TypeId;
            CheckParameters(type.Parameters, trigger.Settings, path + ".settings", errors, warnings);
        }

        ValidateSteps(action.SubActions, "subActions", registry, ids, errors, warnings);
        return new Result(errors, warnings);
    }

    private static void ValidateSteps(List<SubActionDefinition> steps, string basePath, Registry registry, HashSet<Guid> ids,
        JsonArray errors, JsonArray warnings)
    {
        for (var i = 0; i < steps.Count; i++)
        {
            var path = $"{basePath}[{i}]";
            var step = steps[i];
            if (step is null)
            {
                Add(errors, path, "Sub-action is null");
                continue;
            }
            step.Parameters ??= [];
            step.Branches ??= [];
            if (step.Id == Guid.Empty || !ids.Add(step.Id)) ids.Add(step.Id = Guid.NewGuid());
            var handler = registry.GetSubAction(step.Type ?? "");
            if (handler is null)
            {
                Add(errors, path + ".type", $"Unknown sub-action type '{step.Type}' (list them with GET /api/v1/subactions or list_subaction_types)");
            }
            else
            {
                step.Type = handler.TypeId;
                CheckParameters(handler.Parameters, step.Parameters, path + ".parameters", errors, warnings);
                var offered = SubActionBranches.For(handler, step);
                foreach (var branch in step.Branches.Keys.Where(b => !offered.Contains(b, StringComparer.Ordinal)))
                {
                    Add(warnings, $"{path}.branches.{branch}",
                        offered.Count == 0
                            ? $"'{handler.DisplayName}' has no branches; these steps never run"
                            : $"'{handler.DisplayName}' offers the branches {string.Join(", ", offered)}; '{branch}' never runs");
                }
            }
            foreach (var (name, children) in step.Branches.ToArray())
            {
                if (children is null)
                {
                    step.Branches[name] = [];
                    continue;
                }
                ValidateSteps(children, $"{path}.branches.{name}", registry, ids, errors, warnings);
            }
        }
    }

    private static void CheckParameters(IReadOnlyList<ParameterDescriptor> descriptors, Dictionary<string, string> values, string path,
        JsonArray errors, JsonArray warnings)
    {
        foreach (var (key, value) in values.ToArray())
        {
            if (value is null)
            {
                values[key] = "";
                continue;
            }
            var descriptor = descriptors.FirstOrDefault(d => string.Equals(d.Key, key, StringComparison.OrdinalIgnoreCase));
            if (descriptor is null)
            {
                Add(warnings, $"{path}.{key}",
                    descriptors.Count == 0
                        ? $"Unknown parameter '{key}' (this type has no parameters)"
                        : $"Unknown parameter '{key}'; known: {string.Join(", ", descriptors.Select(d => d.Key))}");
                continue;
            }
            if (CatalogJson.CheckValue(descriptor, value) is { } problem) Add(errors, $"{path}.{key}", problem);
        }
    }

    private static void Add(JsonArray list, string path, string message) =>
        list.Add(new JsonObject { ["path"] = path, ["message"] = message });
}
