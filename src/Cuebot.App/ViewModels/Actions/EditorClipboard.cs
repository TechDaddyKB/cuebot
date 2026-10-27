using System.Text.Json;
using Cuebot.Core.Actions;
using Cuebot.Core.Storage;

namespace Cuebot.App.ViewModels.Actions;

public enum ClipKind { Actions, SubActions, Triggers }

/// <summary>
/// What the actions editor copies: whole actions, sub-actions (with their branches and group children) or triggers,
/// as model JSON. The same text goes on the system clipboard, so it can be pasted into another Cuebot instance.
/// </summary>
public sealed class EditorClip
{
    public const string FormatName = "cuebot.editor-clip";

    /// <summary>Marks the JSON as ours, so random clipboard text is never pasted as actions.</summary>
    public string Format { get; set; } = FormatName;

    public int Version { get; set; } = 1;
    public ClipKind Kind { get; set; }
    public List<ActionDefinition> Actions { get; set; } = [];
    public List<SubActionDefinition> SubActions { get; set; } = [];
    public List<TriggerDefinition> Triggers { get; set; } = [];

    public int Count => Kind switch
    {
        ClipKind.Actions => Actions.Count,
        ClipKind.SubActions => SubActions.Count,
        _ => Triggers.Count,
    };

    public string Describe() => (Kind, Count) switch
    {
        (ClipKind.Actions, 1) => $"action '{Actions[0].Name}'",
        (ClipKind.Actions, var n) => $"{n} actions",
        (ClipKind.SubActions, 1) => "1 sub-action",
        (ClipKind.SubActions, var n) => $"{n} sub-actions",
        (_, 1) => "1 trigger",
        (_, var n) => $"{n} triggers",
    };

    public static EditorClip Of(ActionDefinition action) => new() { Kind = ClipKind.Actions, Actions = [Json.Clone(action)] };
    public static EditorClip Of(SubActionDefinition step) => new() { Kind = ClipKind.SubActions, SubActions = [Json.Clone(step)] };
    public static EditorClip Of(TriggerDefinition trigger) => new() { Kind = ClipKind.Triggers, Triggers = [Json.Clone(trigger)] };

    public string ToJson() => JsonSerializer.Serialize(this, Json.Options);

    /// <summary>Parses clipboard text; null when it is not a Cuebot editor clip.</summary>
    public static EditorClip? TryParse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.TrimStart().StartsWith('{')) return null;
        try
        {
            var clip = JsonSerializer.Deserialize<EditorClip>(text, Json.Options);
            if (clip is null || clip.Format != FormatName) return null;
            clip.Actions ??= [];
            clip.SubActions ??= [];
            clip.Triggers ??= [];
            return clip.Count > 0 ? clip : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>A fresh copy of the clip (so one clip can be pasted many times), with new ids everywhere.</summary>
    public EditorClip CloneWithNewIds()
    {
        var copy = Json.Clone(this);
        foreach (var a in copy.Actions) NewIds(a);
        foreach (var s in copy.SubActions) NewIds(s, clearLayout: true);
        foreach (var t in copy.Triggers) NewIds(t, clearLayout: true);
        return copy;
    }

    public static void NewIds(ActionDefinition action)
    {
        action.Id = Guid.NewGuid();
        foreach (var t in action.Triggers) NewIds(t, clearLayout: false);
        foreach (var s in action.SubActions) NewIds(s, clearLayout: false);
    }

    public static void NewIds(TriggerDefinition trigger, bool clearLayout)
    {
        trigger.Id = Guid.NewGuid();
        if (clearLayout) trigger.Layout = null;
    }

    /// <summary>New ids for the step and every step in its branches. Pasted steps lose their canvas position so the
    /// graph places them at the paste point instead of on top of the original.</summary>
    public static void NewIds(SubActionDefinition step, bool clearLayout)
    {
        step.Id = Guid.NewGuid();
        if (clearLayout) step.Layout = null;
        foreach (var child in step.Branches.Values.SelectMany(b => b)) NewIds(child, clearLayout);
    }
}
