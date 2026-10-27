using Avalonia;
using Cuebot.App.ViewModels.Actions;
using Cuebot.Core.Actions;

namespace Cuebot.App.Graph;

public enum GraphNodeKind { Trigger, Entry, Step }

public enum GraphPortKind { In, Next, Branch, AddTrigger }

/// <summary>A connection point. Output ports double as "add here" / "drop here" targets.</summary>
public sealed record GraphPort(GraphNode Node, GraphPortKind Kind, Point Position, string? Label = null, StepInsertTarget? Target = null);

public sealed class GraphNode(GraphNodeKind kind, object item, Rect bounds, string title, string subtitle, string category, bool enabled)
{
    public GraphNodeKind Kind { get; } = kind;

    /// <summary>The view model behind the node: TriggerItemViewModel, ActionItemViewModel or SubActionItemViewModel.</summary>
    public object Item { get; } = item;

    public Rect Bounds { get; } = bounds;
    public string Title { get; } = title;
    public string Subtitle { get; } = subtitle;
    public string Category { get; } = category;
    public bool Enabled { get; } = enabled;
    public List<GraphPort> Ports { get; } = [];

    public GraphPort? InPort => Ports.FirstOrDefault(p => p.Kind == GraphPortKind.In);
}

public sealed record GraphEdge(GraphPort From, GraphPort To);

public sealed record FlowGraph(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphEdge> Edges);

/// <summary>
/// Projects an action's flow tree onto a canvas. Flow runs top to bottom; branches fan out to the right.
/// Nodes with a saved <see cref="NodeLayout"/> keep their position and their unsaved descendants follow them.
/// </summary>
public static class FlowGraphLayout
{
    public const double NodeWidth = 220;
    public const double NodeHeight = 62;
    public const double ColumnGap = 50;
    public const double RowGap = 44;
    private const double BranchPortSpacing = 20;

    public static FlowGraph Build(ActionItemViewModel action)
    {
        var nodes = new List<GraphNode>();
        var edges = new List<GraphEdge>();

        // Triggers along the top, all feeding the entry node.
        var triggerOuts = new List<GraphPort>();
        double x = 0;
        foreach (var trigger in action.Triggers)
        {
            var pos = Position(trigger.Definition.Layout, x, 0);
            var node = new GraphNode(GraphNodeKind.Trigger, trigger, new Rect(pos, Size), trigger.Title, trigger.Summary,
                trigger.Category, trigger.Enabled && action.Enabled);
            var output = new GraphPort(node, GraphPortKind.Next, BottomCenter(node.Bounds));
            node.Ports.Add(output);
            nodes.Add(node);
            triggerOuts.Add(output);
            x += NodeWidth + ColumnGap;
        }

        var entryPos = Position(action.Definition.Layout, 0, NodeHeight + RowGap);
        var entry = new GraphNode(GraphNodeKind.Entry, action, new Rect(entryPos, Size), action.Name,
            action.Concurrent ? "Runs concurrently" : "Runs queued", "Action", action.Enabled);
        var entryIn = new GraphPort(entry, GraphPortKind.In, TopCenter(entry.Bounds));
        var entryNext = new GraphPort(entry, GraphPortKind.Next, BottomCenter(entry.Bounds), Target: new StepInsertTarget(action.Steps, 0));
        entry.Ports.Add(entryIn);
        entry.Ports.Add(entryNext);
        entry.Ports.Add(new GraphPort(entry, GraphPortKind.AddTrigger,
            new Point(entry.Bounds.Right + 18, entry.Bounds.Top + 14), "trigger"));
        nodes.Add(entry);
        edges.AddRange(triggerOuts.Select(t => new GraphEdge(t, entryIn)));

        PlaceChain(action.Steps, entryPos.X, entry.Bounds.Bottom + RowGap, entryNext, action.Enabled, nodes, edges);
        return new FlowGraph(nodes, edges);
    }

    /// <returns>The lowest Y and rightmost X used by the chain and everything under it.</returns>
    private static (double Bottom, double Right) PlaceChain(StepListViewModel list, double x, double y, GraphPort incoming,
        bool enabled, List<GraphNode> nodes, List<GraphEdge> edges)
    {
        var cursorY = y;
        var previous = incoming;
        double bottom = y, right = x + NodeWidth;

        for (var i = 0; i < list.Items.Count; i++)
        {
            var step = list.Items[i];
            var stepEnabled = enabled && step.Enabled;
            var pos = Position(step.Definition.Layout, x, cursorY);
            var node = new GraphNode(GraphNodeKind.Step, step, new Rect(pos, Size), step.Title, step.Summary, step.Category, stepEnabled);
            var input = new GraphPort(node, GraphPortKind.In, TopCenter(node.Bounds));
            var next = new GraphPort(node, GraphPortKind.Next, BottomCenter(node.Bounds), Target: new StepInsertTarget(list, i + 1));
            node.Ports.Add(input);
            node.Ports.Add(next);
            nodes.Add(node);
            edges.Add(new GraphEdge(previous, input));

            var nodeBottom = node.Bounds.Bottom;
            var branchX = node.Bounds.Right + ColumnGap;
            for (var b = 0; b < step.Branches.Count; b++)
            {
                var branch = step.Branches[b];
                var portPos = new Point(node.Bounds.Right, node.Bounds.Top + 16 + b * BranchPortSpacing);
                var port = new GraphPort(node, GraphPortKind.Branch, portPos, branch.Name, new StepInsertTarget(branch.Steps, 0));
                node.Ports.Add(port);
                var (branchBottom, branchRight) = PlaceChain(branch.Steps, branchX, node.Bounds.Bottom + RowGap / 2, port,
                    stepEnabled, nodes, edges);
                nodeBottom = Math.Max(nodeBottom, branchBottom);
                branchX = Math.Max(branchX + NodeWidth, branchRight) + ColumnGap;
            }

            bottom = Math.Max(bottom, nodeBottom);
            right = Math.Max(right, Math.Max(node.Bounds.Right, branchX - ColumnGap));
            cursorY = nodeBottom + RowGap;
            previous = next;
        }
        return (bottom, right);
    }

    private static Size Size => new(NodeWidth, NodeHeight);

    private static Point Position(NodeLayout? layout, double x, double y) => layout is null ? new Point(x, y) : new Point(layout.X, layout.Y);

    private static Point TopCenter(Rect r) => new(r.Center.X, r.Top);

    private static Point BottomCenter(Rect r) => new(r.Center.X, r.Bottom);
}
