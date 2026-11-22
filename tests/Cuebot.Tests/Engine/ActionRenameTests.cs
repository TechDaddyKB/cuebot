using Cuebot.Core.Actions;
using Cuebot.Core.Decks;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Tests.Engine;

/// <summary>Renaming an action keeps everything that names it working.</summary>
public sealed class ActionRenameTests : IDisposable
{
    private readonly AppLog _log = new();
    private readonly ProfileService _profile;

    public ActionRenameTests() => _profile = new ProfileService(null, _log);

    public void Dispose()
    {
        _profile.Dispose();
        _log.Dispose();
    }

    private ActionDefinition Add(string name, params SubActionDefinition[] steps)
    {
        var action = new ActionDefinition { Name = name };
        action.SubActions.AddRange(steps);
        _profile.Profile.Actions.Add(action);
        _profile.NotifyChanged();
        return action;
    }

    private static SubActionDefinition Step(string type, string target) =>
        new() { Type = type, Parameters = new(StringComparer.OrdinalIgnoreCase) { ["action"] = target } };

    private void Rename(ActionDefinition action, string name)
    {
        action.Name = name;
        _profile.NotifyChanged();
    }

    [Theory]
    [InlineData("core.runAction")]
    [InlineData("core.setActionState")]
    [InlineData("core.getActionState")]
    [InlineData("core.setTriggerState")]
    [InlineData("core.setSubActionState")]
    [InlineData("core.setSubActionWeight")]
    public void Steps_naming_the_action_follow_a_rename(string type)
    {
        var target = Add("Target");
        var step = Step(type, "target");
        Add("Caller", step);

        Rename(target, "Renamed");

        Assert.Equal("Renamed", step.Parameters["action"]);
        Assert.Equal("Renamed", _profile.Snapshot.Actions.Single(a => a.Name == "Caller").SubActions[0].Parameters["action"]);
    }

    [Fact]
    public void Nested_steps_follow_and_variables_and_ids_are_left_alone()
    {
        var target = Add("Target");
        var nested = Step("core.runAction", "Target");
        var group = new SubActionDefinition { Type = "core.group" };
        group.Branches["Steps"] = [nested];
        var dynamicName = Step("core.runAction", "%Target%");
        var byId = Step("core.runAction", target.Id.ToString());
        Add("Caller", group, dynamicName, byId);

        Rename(target, "Renamed");

        Assert.Equal("Renamed", nested.Parameters["action"]);
        Assert.Equal("%Target%", dynamicName.Parameters["action"]);
        Assert.Equal(target.Id.ToString(), byId.Parameters["action"]);
    }

    [Fact]
    public void Typing_past_another_actions_name_does_not_capture_its_references()
    {
        var target = Add("Foo");
        Add("Ba");
        var toTarget = Step("core.runAction", "Foo");
        var toOther = Step("core.runAction", "Ba");
        Add("Caller", toTarget, toOther);

        foreach (var typed in new[] { "", "B", "Ba", "Bar" }) Rename(target, typed);

        Assert.Equal("Bar", toTarget.Parameters["action"]);
        Assert.Equal("Ba", toOther.Parameters["action"]);
    }

    [Fact]
    public void Deck_buttons_follow_a_rename()
    {
        var bus = new EventBus(_log);
        var decks = new DeckService(null, bus, _log);
        _profile.ActionRenamed += decks.RenameAction;
        var deck = new DeckDefinition();
        deck.Pages[0].Items.Add(new DeckItem { Action = "Target" });
        decks.Save(deck);
        var target = Add("Target");

        Rename(target, "Renamed");

        Assert.Equal("Renamed", decks.Decks[0].Pages[0].Items[0].Action);
    }

    [Fact]
    public void Rename_event_is_not_raised_for_other_edits()
    {
        var target = Add("Target");
        var renames = 0;
        _profile.ActionRenamed += (_, _) => renames++;

        target.Enabled = false;
        _profile.NotifyChanged();

        Assert.Equal(0, renames);
    }
}
