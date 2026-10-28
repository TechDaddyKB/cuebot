using Cuebot.Controllers.Device;
using Cuebot.Controllers.Tests.Support;
using Cuebot.Core.Actions;
using Cuebot.Core.Events;

namespace Cuebot.Controllers.Tests;

public sealed class TriggerTests
{
    private static Dictionary<string, object?> Args(params (string Key, object? Value)[] pairs) =>
        pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Controller_filters_match_slot_name_type_and_guid()
    {
        var args = Args(("controllerName", "DualSense Wireless Controller"), ("controllerGuid", "0500abc"), ("controllerIndex", 2),
            ("controllerType", "PS5"));
        Assert.True(ControllerFilter.Matches("", args));
        Assert.True(ControllerFilter.Matches("Any", args));
        Assert.True(ControllerFilter.Matches("#2", args));
        Assert.True(ControllerFilter.Matches("1, 2", args));
        Assert.True(ControllerFilter.Matches("dualsense", args));
        Assert.True(ControllerFilter.Matches("ps5", args));
        Assert.True(ControllerFilter.Matches("0500ABC", args));
        Assert.False(ControllerFilter.Matches("#1", args));
        Assert.False(ControllerFilter.Matches("Xbox", args));
    }

    [Fact]
    public void Button_trigger_filters_names_labels_and_state()
    {
        var press = Args(("button", "South"), ("buttonLabel", "A"), ("pressed", true));
        var release = Args(("button", "South"), ("buttonLabel", "A"), ("pressed", false));
        Assert.True(ControllerTriggers.ButtonMatches("Any", "Pressed", press));
        Assert.False(ControllerTriggers.ButtonMatches("Any", "Pressed", release));
        Assert.True(ControllerTriggers.ButtonMatches("Any", "Released", release));
        Assert.False(ControllerTriggers.ButtonMatches("Any", "Released", press));
        Assert.True(ControllerTriggers.ButtonMatches("Any", "Both", release));
        Assert.True(ControllerTriggers.ButtonMatches("B, A", "Pressed", press));
        Assert.True(ControllerTriggers.ButtonMatches("cross", "Pressed", press));
        Assert.False(ControllerTriggers.ButtonMatches("North", "Pressed", press));
    }

    [Fact]
    public void Combo_fires_once_when_the_last_button_goes_down()
    {
        var args = Args(("button", "RightShoulder"), ("buttonLabel", "RB"), ("pressed", true), ("controllerType", "Xbox One"),
            ("heldButtons", "LeftShoulder,RightShoulder"));
        Assert.True(ControllerTriggers.ComboMatches("LeftShoulder+RightShoulder", false, args));
        Assert.True(ControllerTriggers.ComboMatches("LB + RB", true, args));
        Assert.False(ControllerTriggers.ComboMatches("LB+RB+A", false, args));
        var extra = new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase) { ["heldButtons"] = "South,LeftShoulder,RightShoulder" };
        Assert.True(ControllerTriggers.ComboMatches("LB+RB", false, extra));
        Assert.False(ControllerTriggers.ComboMatches("LB+RB", true, extra));
        var unrelated = new Dictionary<string, object?>(extra, StringComparer.OrdinalIgnoreCase) { ["button"] = "South", ["buttonLabel"] = "A" };
        Assert.False(ControllerTriggers.ComboMatches("LB+RB", false, unrelated));   // the combo was already complete
        var released = new Dictionary<string, object?>(args, StringComparer.OrdinalIgnoreCase) { ["pressed"] = false };
        Assert.False(ControllerTriggers.ComboMatches("LB+RB", false, released));
        Assert.False(ControllerTriggers.ComboMatches("", false, args));
    }

    [Fact]
    public void Axis_modes_detect_crossings()
    {
        Dictionary<string, object?> Move(double from, double to, string axis = "LeftX") =>
            Args(("axis", axis), ("axisValue", to), ("axisPrevious", from));
        Assert.True(ControllerTriggers.AxisMatches("Any", "Positive", 0.5, "Enter", Move(0.2, 0.6)));
        Assert.False(ControllerTriggers.AxisMatches("Any", "Positive", 0.5, "Enter", Move(0.6, 0.8)));
        Assert.False(ControllerTriggers.AxisMatches("Any", "Negative", 0.5, "Enter", Move(0.2, 0.6)));
        Assert.True(ControllerTriggers.AxisMatches("Any", "Either", 0.5, "Enter", Move(0, -0.7)));
        Assert.True(ControllerTriggers.AxisMatches("LeftX", "Positive", 0.5, "Exit", Move(0.6, 0.1)));
        Assert.False(ControllerTriggers.AxisMatches("LeftX", "Positive", 0.5, "Exit", Move(0.1, 0)));
        Assert.True(ControllerTriggers.AxisMatches("LeftX", "Positive", 0.5, "While beyond", Move(0.6, 0.8)));
        Assert.False(ControllerTriggers.AxisMatches("LeftX", "Positive", 0.5, "While beyond", Move(0.6, 0.3)));
        Assert.True(ControllerTriggers.AxisMatches("LeftX", "Positive", 0.5, "Any movement", Move(0.1, 0.2)));
        Assert.False(ControllerTriggers.AxisMatches("RightX, LT", "Either", 0.5, "Any movement", Move(0.1, 0.2)));
        Assert.True(ControllerTriggers.AxisMatches("RT", "Positive", 0.9, "Enter", Move(0.5, 1, "RightTrigger")));
    }

    [Fact]
    public void Hat_directions_fire_on_change()
    {
        Dictionary<string, object?> Hat(int from, int to, string hat = "Hat1") => Args(("hat", hat), ("hatValue", to), ("hatPrevious", from));
        Assert.True(ControllerTriggers.HatMatches("Any", "Any", Hat(0, 1)));
        Assert.False(ControllerTriggers.HatMatches("Any", "Any", Hat(1, 0)));
        Assert.True(ControllerTriggers.HatMatches("Hat1", "Up", Hat(0, 9)));      // UpLeft includes Up
        Assert.False(ControllerTriggers.HatMatches("Hat1", "Up", Hat(1, 9)));     // Up was already held
        Assert.True(ControllerTriggers.HatMatches("Hat1", "UpLeft", Hat(1, 9)));
        Assert.False(ControllerTriggers.HatMatches("Hat1", "UpLeft", Hat(0, 1)));
        Assert.True(ControllerTriggers.HatMatches("Any", "Centered", Hat(4, 0)));
        Assert.False(ControllerTriggers.HatMatches("Hat2", "Up", Hat(0, 1)));
        Assert.False(ControllerTriggers.HatMatches("Any", "Sideways", Hat(0, 1)));
    }

    [Fact]
    public async Task Triggers_run_actions_end_to_end_through_the_service()
    {
        await using var env = new Env();
        env.Backend.Present.AddRange([FakeBackend.Xbox(1), FakeBackend.DualSense(2)]);
        await env.StartAsync();
        var provider = new ControllerTriggers(env.Service);
        var triggers = provider.GetTriggers().ToDictionary(t => t.TypeId);
        Assert.Equal(ControllerTriggers.TriggerIds.Order(), triggers.Keys.Order());
        Assert.All(triggers.Values, t => Assert.Equal("Game Controller", t.Category));

        bool Fires(string type, Dictionary<string, string> settings)
        {
            var trigger = triggers[type];
            return env.Events.Any(e => trigger.EventTypes.Contains(e.Type)
                && trigger.Matches(e, new TriggerDefinition { Type = type, Settings = settings },
                    ParameterValues.From(trigger.Parameters, settings), new Dictionary<string, object?>()));
        }

        Assert.True(Fires(ControllerEvents.Connected, new() { ["controller"] = "PS5" }));
        Assert.False(Fires(ControllerEvents.Disconnected, []));
        env.Events.Clear();

        env.Backend.Raise(new ButtonEvent(2, 0, true));   // Cross on #2
        Assert.True(Fires(ControllerEvents.Button, new() { ["controller"] = "#2", ["button"] = "Cross" }));
        Assert.False(Fires(ControllerEvents.Button, new() { ["controller"] = "#1" }));
        Assert.True(Fires(ControllerEvents.Button, []));   // defaults: any controller, any button, pressed
        env.Backend.Raise(new ButtonEvent(2, 9, true));
        env.Backend.Raise(new ButtonEvent(2, 10, true));
        Assert.True(Fires(ControllerEvents.ButtonCombo, new() { ["buttons"] = "L1+R1" }));
        Assert.False(Fires(ControllerEvents.ButtonCombo, new() { ["buttons"] = "L1+R1", ["exact"] = "true" }));   // Cross still held

        env.Backend.Raise(new AxisEvent(1, ControllerNames.LeftTrigger, 30000));
        Assert.True(Fires(ControllerEvents.Axis, new() { ["axis"] = "LT", ["direction"] = "Positive" }));
        Assert.False(Fires(ControllerEvents.Axis, new() { ["axis"] = "LT", ["threshold"] = "95" }));

        env.Backend.Raise(new ButtonEvent(1, ControllerNames.DPadDown, true));
        Assert.True(Fires(ControllerEvents.Hat, new() { ["direction"] = "Down" }));

        env.Backend.Raise(new DeviceRemovedEvent(1));
        Assert.True(Fires(ControllerEvents.Disconnected, new() { ["controller"] = "xbox" }));

        // Suggestions offer what was pressed most recently ("learn" in the generic trigger editor).
        var suggestions = triggers[ControllerEvents.Button].Parameters.Single(p => p.Key == "button").Suggestions!().ToArray();
        Assert.Equal(["Any", "DPadDown", "RightShoulder", "LeftShoulder", "South"], suggestions[..5]);
    }

    [Fact]
    public void Profiles_with_controller_triggers_want_input()
    {
        var action = new ActionDefinition { Name = "a", Triggers = [new TriggerDefinition { Type = ControllerEvents.Hat }] };
        var profile = new ProfileSnapshot([action], []);
        Assert.True(ControllersServiceCollectionExtensions.UsesControllerTriggers(profile));
        action.Triggers[0].Enabled = false;
        Assert.False(ControllersServiceCollectionExtensions.UsesControllerTriggers(profile));
        action.Triggers[0].Enabled = true;
        action.Enabled = false;
        Assert.False(ControllersServiceCollectionExtensions.UsesControllerTriggers(profile));
        Assert.False(ControllersServiceCollectionExtensions.UsesControllerTriggers(new ProfileSnapshot(
            [new ActionDefinition { Triggers = [new TriggerDefinition { Type = "input.mouseClick" }] }], [])));
    }
}
