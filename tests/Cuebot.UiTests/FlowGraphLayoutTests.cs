using Cuebot.App.Graph;
using Cuebot.App.ViewModels.Actions;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Logging;

namespace Cuebot.UiTests;

public class FlowGraphLayoutTests
{
    private static (ActionItemViewModel Action, ProfileService Profile) Create()
    {
        var registry = new Registry(
            [new IfSubAction(), new LogSubAction(), new DelaySubAction()],
            [new EventTriggerType("t.one", "Test", "One", "t.one"), new EventTriggerType("t.two", "Test", "Two", "t.two")]);
        var profile = new ProfileService(null, new AppLog());
        var definition = new ActionDefinition { Name = "Flow" };
        profile.Profile.Actions.Add(definition);
        var action = new ActionItemViewModel(definition, registry, profile);
        action.AddTrigger(registry.GetTrigger("t.one")!);
        action.AddTrigger(registry.GetTrigger("t.two")!);
        var ifStep = action.AddStep(registry.GetSubAction("core.if")!, new StepInsertTarget(action.Steps, 0));
        action.AddStep(registry.GetSubAction("core.log")!, new StepInsertTarget(ifStep.Branches[0].Steps, 0));
        action.AddStep(registry.GetSubAction("core.delay")!, new StepInsertTarget(ifStep.Branches[1].Steps, 0));
        action.AddStep(registry.GetSubAction("core.log")!, new StepInsertTarget(action.Steps, 1));
        return (action, profile);
    }

    [Fact]
    public void Builds_a_node_per_trigger_entry_and_step_with_tree_edges()
    {
        var (action, _) = Create();
        var graph = FlowGraphLayout.Build(action);

        Assert.Equal(2, graph.Nodes.Count(n => n.Kind == GraphNodeKind.Trigger));
        Assert.Single(graph.Nodes, n => n.Kind == GraphNodeKind.Entry);
        Assert.Equal(4, graph.Nodes.Count(n => n.Kind == GraphNodeKind.Step));
        // 2 trigger→entry, entry→if, if.True→log, if.False→delay, if→log(after)
        Assert.Equal(6, graph.Edges.Count);
        var ifNode = graph.Nodes.Single(n => n.Title == "If");
        Assert.Equal(["True", "False"], ifNode.Ports.Where(p => p.Kind == GraphPortKind.Branch).Select(p => p.Label));
    }

    [Fact]
    public void Auto_layout_never_overlaps_nodes()
    {
        var (action, _) = Create();
        var nodes = FlowGraphLayout.Build(action).Nodes;
        for (var i = 0; i < nodes.Count; i++)
        for (var j = i + 1; j < nodes.Count; j++)
            Assert.False(nodes[i].Bounds.Intersects(nodes[j].Bounds), $"{nodes[i].Title} overlaps {nodes[j].Title}");
    }

    [Fact]
    public void Saved_positions_are_used_and_children_follow()
    {
        var (action, _) = Create();
        var ifStep = action.Steps.Items[0];
        ifStep.Definition.Layout = new NodeLayout(1000, 2000);
        var graph = FlowGraphLayout.Build(action);

        var ifNode = graph.Nodes.Single(n => n.Item == ifStep);
        Assert.Equal(1000, ifNode.Bounds.X);
        var trueChild = graph.Nodes.Single(n => n.Item == ifStep.Branches[0].Steps.Items[0]);
        Assert.True(trueChild.Bounds.X > 1000 && trueChild.Bounds.Y > 2000);
    }

    [Fact]
    public void Output_ports_carry_insert_targets_matching_the_model()
    {
        var (action, profile) = Create();
        var graph = FlowGraphLayout.Build(action);
        var falsePort = graph.Nodes.Single(n => n.Title == "If").Ports.Single(p => p.Label == "False");

        action.AddStep(action.Registry.GetSubAction("core.log")!, falsePort.Target!);

        var falseBranch = profile.Profile.Actions.Single().SubActions[0].Branches["False"];
        Assert.Equal(["core.log", "core.delay"], falseBranch.Select(s => s.Type));
    }

    [Fact]
    public void Moving_a_step_rewires_it_and_refuses_cycles()
    {
        var (action, profile) = Create();
        var ifStep = action.Steps.Items[0];
        var trailingLog = action.Steps.Items[1];

        Assert.True(action.MoveStep(trailingLog, new StepInsertTarget(ifStep.Branches[1].Steps, 0)));
        Assert.Single(profile.Profile.Actions.Single().SubActions);
        Assert.Equal(2, profile.Profile.Actions.Single().SubActions[0].Branches["False"].Count);

        Assert.False(action.MoveStep(ifStep, new StepInsertTarget(ifStep.Branches[0].Steps, 0)));
    }
}
