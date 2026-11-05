using System.Text.Json.Nodes;
using Cuebot.Core.Actions;
using Cuebot.Core.Commands;

namespace Cuebot.Portal.Changes;

/// <summary>Actions in actions.json, edited on the profile's owning thread.</summary>
public sealed class ActionArea(ProfileService profile) : IPortalArea
{
    public const string AreaName = "action";

    public string Name => AreaName;

    public JsonNode? Current(Guid id) =>
        profile.Snapshot.Actions.FirstOrDefault(a => a.Id == id) is { } a ? PortalChanges.ToNode(a) : null;

    public string NameOf(JsonNode value) => (string?)value["name"] ?? "";

    public Task ApplyAsync(Guid id, JsonNode? value)
    {
        var action = value is null ? null : PortalChanges.FromNode<ActionDefinition>(value);
        if (action is not null) action.Id = id;
        return profile.EditAsync(p =>
        {
            var i = p.Actions.FindIndex(a => a.Id == id);
            if (action is null) { if (i >= 0) p.Actions.RemoveAt(i); }
            else if (i >= 0) p.Actions[i] = action;
            else p.Actions.Add(action);
        });
    }
}

/// <summary>Chat commands in commands.json.</summary>
public sealed class CommandArea(CommandStore commands) : IPortalArea
{
    public const string AreaName = "command";

    public string Name => AreaName;

    public JsonNode? Current(Guid id) => commands.Get(id) is { } c ? PortalChanges.ToNode(c) : null;

    public string NameOf(JsonNode value) => (string?)value["name"] ?? "";

    public Task ApplyAsync(Guid id, JsonNode? value)
    {
        if (value is null) commands.Remove(id);
        else
        {
            var command = PortalChanges.FromNode<CommandDefinition>(value);
            command.Id = id;
            commands.Save(command);
        }
        return Task.CompletedTask;
    }
}
