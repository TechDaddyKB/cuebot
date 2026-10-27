using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Cuebot.App.ViewModels.Actions;
using Cuebot.Core.Actions;

namespace Cuebot.App.Graph;

/// <summary>
/// Node-graph editor for one action. Click a node to edit it, drag to move it, drop it on another node's
/// output port to rewire it there, click a "+" port to add a step at that point. Drag the background to pan,
/// mouse wheel to zoom, Delete to remove the selected node. Right-click selects the node under the pointer for the
/// context menu; Ctrl+C/V/D/Z/Y bubble to the Actions page, which handles them for both editors.
/// </summary>
public sealed class FlowGraphControl : Control
{
    public static readonly StyledProperty<ActionItemViewModel?> ActionProperty =
        AvaloniaProperty.Register<FlowGraphControl, ActionItemViewModel?>(nameof(Action));

    private const double PortRadius = 7;
    private const double PortHitRadius = 13;

    /// <summary>Canvas colours for one theme variant; the control picks one from its actual theme (Light, Dark, System).</summary>
    private sealed record Palette(IBrush Background, IBrush GridDot, IBrush NodeFill, IBrush TitleBrush, IBrush SubtitleBrush,
        IBrush PortFill, IBrush DropFill, IPen EdgePen, IPen NodePen, IPen SelectedPen, IPen PortPen)
    {
        public static Palette Create(string background, string gridDot, string nodeFill, string title, string subtitle,
            string portFill, string edge, string nodeBorder, string selected) => new(
            Brush(background), Brush(gridDot), Brush(nodeFill), Brush(title), Brush(subtitle), Brush(portFill), Brush("#4CAF50"),
            new Pen(Brush(edge), 2), new Pen(Brush(nodeBorder), 1), new Pen(Brush(selected), 2.5), new Pen(Brush(edge), 1.5));

        private static SolidColorBrush Brush(string color) => new(Color.Parse(color));
    }

    private static readonly Palette DarkPalette = Palette.Create(
        background: "#1B1D23", gridDot: "#2E323C", nodeFill: "#272A33", title: "#FFFFFF", subtitle: "#A8AEBB",
        portFill: "#3A3F4B", edge: "#7C8496", nodeBorder: "#3A3F4B", selected: "#6CA8FF");

    private static readonly Palette LightPalette = Palette.Create(
        background: "#F3F4F7", gridDot: "#D3D7DF", nodeFill: "#FFFFFF", title: "#1B1D23", subtitle: "#5C6372",
        portFill: "#E1E4EA", edge: "#8A92A3", nodeBorder: "#C9CED8", selected: "#2F6FE0");

    private Palette P => ActualThemeVariant == Avalonia.Styling.ThemeVariant.Light ? LightPalette : DarkPalette;
    private static readonly Typeface TitleFace = new(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold);
    private static readonly Typeface TextFace = new(FontFamily.Default);

    private FlowGraph _graph = new([], []);
    private Matrix _view = Matrix.CreateTranslation(40, 30);
    private Point _lastPointer;
    private GraphNode? _dragNode;
    private Point _dragOffset;
    private bool _dragMoved;
    private bool _panning;
    private GraphPort? _hoverPort;
    private bool _fitPending = true;

    static FlowGraphControl()
    {
        FocusableProperty.OverrideDefaultValue<FlowGraphControl>(true);
        ClipToBoundsProperty.OverrideDefaultValue<FlowGraphControl>(true);
        AffectsRender<FlowGraphControl>(ActionProperty);
    }

    public FlowGraphControl()
    {
        // The canvas paints its own colours, so a Light/Dark/System switch has to repaint it.
        ActualThemeVariantChanged += (_, _) => InvalidateVisual();
    }

    public ActionItemViewModel? Action
    {
        get => GetValue(ActionProperty);
        set => SetValue(ActionProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ActionProperty) return;
        if (change.OldValue is ActionItemViewModel old)
        {
            old.Changed -= Rebuild;
            old.PropertyChanged -= OnActionPropertyChanged;
        }
        if (change.NewValue is ActionItemViewModel action)
        {
            action.Changed += Rebuild;
            action.PropertyChanged += OnActionPropertyChanged;
        }
        _fitPending = true;
        Rebuild();
    }

    private void OnActionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e) => Rebuild();

    private void Rebuild()
    {
        _graph = Action is null ? new FlowGraph([], []) : FlowGraphLayout.Build(Action);
        InvalidateVisual();
    }

    // ---- Rendering ----

    public override void Render(DrawingContext context)
    {
        if (_fitPending && Bounds.Width > 0 && _graph.Nodes.Count > 0)
        {
            FitToView();
            _fitPending = false;
        }
        context.FillRectangle(P.Background, new Rect(Bounds.Size));
        DrawGrid(context);
        using (context.PushTransform(_view))
        {
            foreach (var frame in _graph.Frames) DrawFrame(context, frame);
            foreach (var edge in _graph.Edges) DrawEdge(context, edge);
            foreach (var node in _graph.Nodes) DrawNode(context, node);
        }
        if (Action is null)
        {
            DrawText(context, "Select an action", new Point(20, 20), TextFace, 14, P.SubtitleBrush, 400);
        }
    }

    private void DrawGrid(DrawingContext context)
    {
        var spacing = 24 * _view.M11;
        if (spacing < 8) return;
        var offsetX = _view.M31 % spacing;
        var offsetY = _view.M32 % spacing;
        for (var x = offsetX; x < Bounds.Width; x += spacing)
        for (var y = offsetY; y < Bounds.Height; y += spacing)
            context.FillRectangle(P.GridDot, new Rect(x, y, 1.5, 1.5));
    }

    private static void DrawFrame(DrawingContext context, GraphFrame frame)
    {
        var fill = new SolidColorBrush(frame.Color, 0.12);
        var pen = new Pen(new SolidColorBrush(frame.Color, 0.7), 1.5, new DashStyle([4, 3], 0));
        context.DrawRectangle(fill, pen, frame.Bounds, 10, 10);
    }

    private void DrawEdge(DrawingContext context, GraphEdge edge)
    {
        var a = edge.From.Position;
        var b = edge.To.Position;
        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(a, false);
            var bend = Math.Max(30, Math.Abs(b.Y - a.Y) / 2);
            var c1 = edge.From.Kind == GraphPortKind.Branch ? new Point(a.X + bend, a.Y) : new Point(a.X, a.Y + bend);
            var c2 = new Point(b.X, b.Y - bend);
            g.CubicBezierTo(c1, c2, b);
            g.EndFigure(false);
        }
        context.DrawGeometry(null, P.EdgePen, geometry);
    }

    private void DrawNode(DrawingContext context, GraphNode node)
    {
        var selected = node.Item is EditorItemViewModel { IsSelected: true }
                       || (node.Kind == GraphNodeKind.Entry && Action?.Selected is null);
        using (context.PushOpacity(node.Enabled ? 1 : 0.45))
        {
            var rect = node.Bounds;
            context.DrawRectangle(P.NodeFill, selected ? P.SelectedPen : P.NodePen, rect, 8, 8);
            var header = new Rect(rect.X, rect.Y, 6, rect.Height);
            context.DrawRectangle(node.Tint is { } tint ? new SolidColorBrush(tint) : CategoryBrush(node), null, header, 3, 3);

            var kindLabel = node.KindLabel ?? node.Kind switch
            {
                GraphNodeKind.Trigger => "TRIGGER · " + node.Category.ToUpperInvariant(),
                GraphNodeKind.Entry => "ACTION",
                _ => node.Category.ToUpperInvariant(),
            };
            DrawText(context, kindLabel, new Point(rect.X + 16, rect.Y + 7), TextFace, 9.5, P.SubtitleBrush, rect.Width - 24);
            DrawText(context, node.Title, new Point(rect.X + 16, rect.Y + 21), TitleFace, 13.5, P.TitleBrush, rect.Width - 24);
            if (node.Subtitle.Length > 0)
                DrawText(context, node.Subtitle, new Point(rect.X + 16, rect.Y + 40), TextFace, 11, P.SubtitleBrush, rect.Width - 24);
        }

        foreach (var port in node.Ports) DrawPort(context, port);
    }

    private void DrawPort(DrawingContext context, GraphPort port)
    {
        var isOutput = port.Kind != GraphPortKind.In;
        var isDropTarget = _dragMoved && _dragNode is not null && port == _hoverPort && CanDrop(_dragNode, port);
        var fill = isDropTarget ? P.DropFill : port == _hoverPort && isOutput ? P.SelectedPen.Brush! : P.PortFill;
        context.DrawEllipse(fill, P.PortPen, port.Position, PortRadius, PortRadius);
        if (isOutput)
        {
            // "+" glyph: output ports add a step (or trigger) at that point.
            var p = port.Position;
            var plus = new Pen(P.TitleBrush, 1.5);
            context.DrawLine(plus, new Point(p.X - 3.5, p.Y), new Point(p.X + 3.5, p.Y));
            context.DrawLine(plus, new Point(p.X, p.Y - 3.5), new Point(p.X, p.Y + 3.5));
        }
        if (port.Label is not null)
        {
            var at = port.Kind == GraphPortKind.Branch ? new Point(port.Position.X + 10, port.Position.Y - 16) : new Point(port.Position.X + 10, port.Position.Y - 7);
            DrawText(context, port.Label, at, TextFace, 10, P.SubtitleBrush, 120);
        }
    }

    private static void DrawText(DrawingContext context, string text, Point at, Typeface face, double size, IBrush brush, double maxWidth)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, face, size, brush)
        {
            MaxTextWidth = maxWidth,
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis,
        };
        context.DrawText(formatted, at);
    }

    private static IBrush CategoryBrush(GraphNode node)
    {
        if (node.Kind == GraphNodeKind.Entry) return new SolidColorBrush(Color.Parse("#6CA8FF"));
        if (node.Kind == GraphNodeKind.Trigger) return new SolidColorBrush(Color.Parse("#F0A33A"));
        var hue = (uint)node.Category.Aggregate(17, (h, c) => h * 31 + c) % 360;
        return new SolidColorBrush(HsvColor.ToRgb(hue, 0.55, 0.85));
    }

    /// <summary>Zooms (never above 100%) and centers so the whole flow is visible.</summary>
    private void FitToView()
    {
        const double margin = 40;
        if (_graph.Nodes.Count == 0 || Bounds.Width <= 0) return;
        var extent = _graph.Nodes.Select(n => n.Bounds).Aggregate((a, b) => a.Union(b)).Inflate(new Thickness(0, 0, 60, 20));
        var scale = Math.Clamp(Math.Min((Bounds.Width - 2 * margin) / extent.Width, (Bounds.Height - 2 * margin) / extent.Height), 0.65, 1);
        var offsetX = margin - extent.X * scale + Math.Max(0, (Bounds.Width - 2 * margin - extent.Width * scale) / 2);
        _view = Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offsetX, margin - extent.Y * scale);
    }

    // ---- Interaction ----

    private Point ToWorld(Point screen) => _view.Invert().Transform(screen);

    private GraphNode? NodeAt(Point world) => _graph.Nodes.LastOrDefault(n => n.Bounds.Contains(world));

    private GraphPort? OutputPortAt(Point world) =>
        _graph.Nodes.SelectMany(n => n.Ports)
            .Where(p => p.Kind != GraphPortKind.In)
            .FirstOrDefault(p => Distance(p.Position, world) <= PortHitRadius / _view.M11 + 2);

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (Action is null) return;
        var screen = e.GetPosition(this);
        var world = ToWorld(screen);
        _lastPointer = screen;
        var point = e.GetCurrentPoint(this);

        if (point.Properties.IsRightButtonPressed)
        {
            // Select what the context menu (copy, paste, duplicate, delete) will act on; the entry node means the action.
            Action.Selected = NodeAt(world)?.Item as EditorItemViewModel;
            InvalidateVisual();
            return;
        }

        if (point.Properties.IsLeftButtonPressed && OutputPortAt(world) is { } port)
        {
            ActivatePort(port);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed && NodeAt(world) is { } node)
        {
            Action.Selected = node.Item as EditorItemViewModel;
            _dragNode = node;
            _dragOffset = world - node.Bounds.TopLeft;
            _dragMoved = false;
            e.Pointer.Capture(this);
            InvalidateVisual();
            e.Handled = true;
            return;
        }

        Action.Selected = null;
        _panning = true;
        e.Pointer.Capture(this);
        InvalidateVisual();
    }

    private void ActivatePort(GraphPort port)
    {
        if (Action is null) return;
        if (port.Kind == GraphPortKind.AddTrigger) Action.RequestAddTrigger();
        else if (port.Target is not null) Action.RequestAddStep(port.Target);
        else if (port.Node.Kind == GraphNodeKind.Trigger) Action.RequestAddTrigger();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var screen = e.GetPosition(this);
        var world = ToWorld(screen);

        if (_panning)
        {
            var delta = screen - _lastPointer;
            _view *= Matrix.CreateTranslation(delta.X, delta.Y);
            _lastPointer = screen;
            InvalidateVisual();
            return;
        }

        var hover = OutputPortAt(world);
        if (_dragNode is not null && Action is not null)
        {
            if (!_dragMoved && Distance(screen, _lastPointer) < 4) return;
            _dragMoved = true;
            var topLeft = world - _dragOffset;
            SetLayout(_dragNode.Item, new NodeLayout(Math.Round(topLeft.X), Math.Round(topLeft.Y)));
            _graph = FlowGraphLayout.Build(Action);
            _dragNode = _graph.Nodes.First(n => n.Item == _dragNode.Item);
        }

        if (hover != _hoverPort || _dragNode is not null)
        {
            _hoverPort = hover;
            Cursor = hover is not null ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            InvalidateVisual();
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        e.Pointer.Capture(null);
        _panning = false;
        if (_dragNode is null || Action is null) return;

        var node = _dragNode;
        _dragNode = null;
        if (!_dragMoved) return;
        _dragMoved = false;

        var drop = OutputPortAt(ToWorld(e.GetPosition(this)));
        if (drop is not null && CanDrop(node, drop) && node.Item is SubActionItemViewModel step)
        {
            // Rewire: the step (with its branches) moves to the drop point and is auto-placed there.
            step.Definition.Layout = null;
            if (!Action.MoveStep(step, drop.Target!)) Rebuild();
        }
        else
        {
            Action.MarkChanged(structural: false);
            Rebuild();
        }
    }

    private static bool CanDrop(GraphNode dragged, GraphPort port) =>
        dragged.Item is SubActionItemViewModel step
        && port.Target is not null
        && port.Node != dragged
        && !step.OwnedLists().Contains(port.Target.List);

    private static void SetLayout(object item, NodeLayout layout)
    {
        switch (item)
        {
            case SubActionItemViewModel step: step.Definition.Layout = layout; break;
            case TriggerItemViewModel trigger: trigger.Definition.Layout = layout; break;
            case ActionItemViewModel action: action.Definition.Layout = layout; break;
        }
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var factor = e.Delta.Y > 0 ? 1.1 : 1 / 1.1;
        var zoom = _view.M11 * factor;
        if (zoom is < 0.3 or > 2.5) return;
        var p = e.GetPosition(this);
        _view = _view * Matrix.CreateTranslation(-p.X, -p.Y) * Matrix.CreateScale(factor, factor) * Matrix.CreateTranslation(p.X, p.Y);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Action is null) return;
        if (e.Key is Key.Delete or Key.Back)
        {
            Action.DeleteSelected();
            e.Handled = true;
        }
        else if (e.Key == Key.Home)
        {
            FitToView();
            InvalidateVisual();
        }
    }
}
