using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cuebot.App;
using Cuebot.App.Graph;
using Cuebot.App.ViewModels;
using Cuebot.App.ViewModels.Actions;
using Cuebot.App.Views;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;

namespace Cuebot.UiTests;

/// <summary>
/// Issue #41: mouse controls of the action flowchart, driven with real pointer input against the composition root:
/// grid snapping, "Add step here…" on the empty canvas, "Disconnect" on a wire, and dragging a wire from a port.
/// </summary>
public sealed class FlowGraphMouseTests : IAsyncDisposable
{
    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-graph-").FullName;
    private readonly AppHost _host;

    public FlowGraphMouseTests() => _host = AppHost.Create(new AppPaths(_dataDir));

    private ProfileService Profile => _host.Get<ProfileService>();

    private sealed record Editor(Window Window, ActionsViewModel Actions, FlowGraphControl Graph)
    {
        public ActionItemViewModel Action => Actions.SelectedAction!;
        public GraphNode Node(SubActionDefinition step) => Graph.Graph.Nodes.Single(n => n.Item is SubActionItemViewModel s && s.Definition == step);
        public GraphNode Entry => Graph.Graph.Nodes.Single(n => n.Kind == GraphNodeKind.Entry);
        public MenuItem MenuItem(string name) => Graph.ContextMenu!.Items.OfType<MenuItem>().Single(m => m.Name == name);

        /// <summary>A canvas (world) point as a point in the window.</summary>
        public Point At(Point world) => Graph.TranslatePoint(Graph.View.Transform(world), Window)!.Value;

        public void Down(Point world, MouseButton button = MouseButton.Left) => Window.MouseDown(At(world), button);
        public void Move(Point world) => Window.MouseMove(At(world));
        public void Up(Point world, MouseButton button = MouseButton.Left) => Window.MouseUp(At(world), button);

        public void RightClick(Point world)
        {
            Down(world, MouseButton.Right);
            Up(world, MouseButton.Right);
            Pump();
            Graph.ContextMenu?.Close();
        }

        public void Drag(Point from, Point to)
        {
            Down(from);
            Move(from + (to - from) / 2);
            Move(to);
            Up(to);
            Pump();
        }
    }

    private (ActionDefinition Definition, SubActionDefinition A, SubActionDefinition B, SubActionDefinition C) AddAction()
    {
        SubActionDefinition Log(string message) => new() { Type = "core.log", Parameters = { ["message"] = message, ["level"] = "Info" } };
        var (a, b, c) = (Log("a"), Log("b"), Log("c"));
        var action = new ActionDefinition { Name = "Mouse flow", SubActions = [a, b, c] };
        Profile.Profile.Actions.Add(action);
        Profile.NotifyChanged();
        return (action, a, b, c);
    }

    private Editor Open(ActionDefinition definition)
    {
        var vm = new MainWindowViewModel(_host);
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        var actions = vm.Pages.OfType<ActionsViewModel>().Single();
        vm.SelectedPage = actions;
        actions.SelectedAction = actions.Actions.Single(a => a.Definition == definition);
        actions.IsGraphView = true;
        Pump();
        var graph = window.GetVisualDescendants().OfType<FlowGraphControl>().Single();
        Pump();
        return new Editor(window, actions, graph);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void Dragged_nodes_snap_to_the_grid_unless_snapping_is_off_and_the_choice_is_remembered()
    {
        var (definition, _, b, _) = AddAction();
        var editor = Open(definition);
        Assert.True(editor.Actions.SnapToGrid);
        Assert.True(editor.Graph.SnapToGrid);

        var start = editor.Node(b).Bounds;
        editor.Drag(start.Center, start.Center + new Point(37, 53));
        var snapped = Assert.IsType<NodeLayout>(b.Layout);
        Assert.Equal(0, snapped.X % FlowGraphControl.GridSize);
        Assert.Equal(0, snapped.Y % FlowGraphControl.GridSize);
        Assert.True(Math.Abs(snapped.X - (start.X + 37)) <= FlowGraphControl.GridSize / 2 + 0.5);
        Assert.True(Math.Abs(snapped.Y - (start.Y + 53)) <= FlowGraphControl.GridSize / 2 + 0.5);

        editor.Actions.SnapToGrid = false;
        Pump();
        Assert.False(editor.Graph.SnapToGrid);
        Assert.False(_host.Settings.Settings.General.FlowchartSnapToGrid);
        Assert.Matches("(?i)\"flowchartSnapToGrid\"\\s*:\\s*false", File.ReadAllText(_host.Settings.Path));

        var from = editor.Node(b).Bounds;
        editor.Drag(from.Center, from.Center + new Point(37, 53));
        var free = Assert.IsType<NodeLayout>(b.Layout);
        Assert.Equal(new NodeLayout(Math.Round(from.X + 37), Math.Round(from.Y + 53)), free);

        // A new page (next start) reads the setting back.
        var again = new ActionsViewModel(Profile, _host.Get<Registry>(), _host.Get<ActionEngine>(), _host.Settings);
        Assert.False(again.SnapToGrid);
        editor.Window.Close();
    }

    [AvaloniaFact]
    public void Right_click_on_the_empty_canvas_adds_a_step_there_at_the_end_of_the_action()
    {
        var (definition, _, _, _) = AddAction();
        var editor = Open(definition);
        var nodes = editor.Graph.Graph.Nodes.Select(n => n.Bounds).Aggregate((x, y) => x.Union(y));
        var empty = new Point(nodes.Right + 61, nodes.Top + 17);

        editor.RightClick(empty);
        Assert.Equal(FlowGraphMenuTarget.Canvas, editor.Graph.MenuTarget);
        Assert.True(editor.MenuItem("GraphAddStepHere").IsVisible);
        Assert.False(editor.MenuItem("GraphDisconnect").IsVisible);
        Assert.False(editor.MenuItem("GraphDelete").IsVisible);

        editor.MenuItem("GraphAddStepHere").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump();
        var picker = editor.Actions.Picker;
        Assert.True(picker.IsOpen);
        picker.Selected = picker.Options.First(o => o.Payload is ISubActionHandler { TypeId: "core.delay" });
        picker.ConfirmCommand.Execute(null);
        Pump();

        var added = definition.SubActions[^1];
        Assert.Equal(4, definition.SubActions.Count);
        Assert.Equal("core.delay", added.Type);
        var grid = FlowGraphControl.GridSize;
        Assert.Equal(new NodeLayout(Math.Round(empty.X / grid) * grid, Math.Round(empty.Y / grid) * grid), added.Layout);
        Assert.Equal(added.Layout!.X, editor.Node(added).Bounds.X);

        // Snapping off: the step goes exactly where the canvas was clicked.
        editor.Actions.SnapToGrid = false;
        Pump();
        editor.RightClick(empty);
        editor.Graph.AddStepHere();
        picker.Selected = picker.Options.First(o => o.Payload is ISubActionHandler { TypeId: "core.delay" });
        picker.ConfirmCommand.Execute(null);
        Assert.Equal(new NodeLayout(Math.Round(empty.X), Math.Round(empty.Y)), definition.SubActions[^1].Layout);

        // A right-click on a box keeps the box menu.
        editor.RightClick(editor.Entry.Bounds.Center);
        Assert.Equal(FlowGraphMenuTarget.Node, editor.Graph.MenuTarget);
        Assert.False(editor.MenuItem("GraphAddStepHere").IsVisible);
        Assert.True(editor.MenuItem("GraphDelete").IsVisible);
        editor.Window.Close();
    }

    [AvaloniaFact]
    public void Disconnect_on_a_wire_moves_the_rest_of_the_list_into_a_switched_off_group_and_undo_restores_it()
    {
        var (definition, a, b, c) = AddAction();
        var editor = Open(definition);
        var wire = editor.Graph.Graph.Edges.Single(e => e.To.Node == editor.Node(b));
        var bWasAt = editor.Node(b).Bounds.TopLeft;

        editor.RightClick(FlowGraphControl.PointOnEdge(wire, 0.5));
        Assert.Equal(FlowGraphMenuTarget.Link, editor.Graph.MenuTarget);
        Assert.True(editor.MenuItem("GraphDisconnect").IsVisible);
        Assert.False(editor.MenuItem("GraphAddStepHere").IsVisible);
        Assert.False(editor.MenuItem("GraphCopy").IsVisible);

        editor.MenuItem("GraphDisconnect").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Pump();

        var current = editor.Action.Definition;
        Assert.Equal(2, current.SubActions.Count);
        Assert.Same(a, current.SubActions[0]);
        var group = current.SubActions[1];
        Assert.Equal(GroupSubAction.Id, group.Type);
        Assert.False(group.Enabled);
        Assert.Equal(ActionItemViewModel.DisconnectedGroupName, group.Parameters["name"]);
        Assert.Equal([b.Id, c.Id], group.Branches[GroupSubAction.Steps].Select(s => s.Id));
        Assert.Equal(new NodeLayout(Math.Round(bWasAt.X), Math.Round(bWasAt.Y)), group.Layout);
        Assert.Contains("Disconnected 2 steps", editor.Actions.EditStatus);
        // Both ends stay on the canvas, faded (switched off) on the far side.
        Assert.False(editor.Node(b).Enabled);
        Assert.True(editor.Node(a).Enabled);

        editor.Actions.UndoCommand.Execute(null);
        Pump();
        Assert.Equal([a.Id, b.Id, c.Id], editor.Action.Definition.SubActions.Select(s => s.Id));
        Assert.All(editor.Action.Definition.SubActions, s => Assert.True(s.Enabled));
        editor.Window.Close();
    }

    [AvaloniaFact]
    public void Dragging_from_a_port_onto_a_step_connects_it_there_and_a_click_still_adds_a_step()
    {
        var (definition, a, b, c) = AddAction();
        var editor = Open(definition);
        var entryOut = editor.Entry.Ports.Single(p => p.Kind == GraphPortKind.Next);

        // Released over empty canvas: nothing changes.
        editor.Drag(entryOut.Position, entryOut.Position + new Point(-150, 20));
        Assert.Equal([a.Id, b.Id, c.Id], editor.Action.Definition.SubActions.Select(s => s.Id));

        // From the action's "+" onto the last step: it now runs first.
        editor.Drag(entryOut.Position, editor.Node(c).Bounds.Center);
        Assert.Equal([c.Id, a.Id, b.Id], editor.Action.Definition.SubActions.Select(s => s.Id));

        // A step cannot be wired to its own port.
        var cOut = editor.Node(c).Ports.Single(p => p.Kind == GraphPortKind.Next);
        editor.Drag(cOut.Position, editor.Node(c).Bounds.Center);
        Assert.Equal([c.Id, a.Id, b.Id], editor.Action.Definition.SubActions.Select(s => s.Id));

        // A plain click on the port opens the step picker, as before.
        var port = editor.Node(b).Ports.Single(p => p.Kind == GraphPortKind.Next);
        editor.Down(port.Position);
        editor.Up(port.Position);
        Pump();
        Assert.True(editor.Actions.Picker.IsOpen);
        editor.Actions.Picker.CancelCommand.Execute(null);
        editor.Window.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
