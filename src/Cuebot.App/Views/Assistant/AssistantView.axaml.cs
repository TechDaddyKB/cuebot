using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Cuebot.App.ViewModels.Assistant;

namespace Cuebot.App.Views.Assistant;

public partial class AssistantView : UserControl
{
    public AssistantView()
    {
        InitializeComponent();
        // Tunnel: a multi-line TextBox handles Enter itself before a bubbling KeyDown handler would see it.
        AssistantInput.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Follow the conversation as it streams in.
        AssistantItems.LayoutUpdated += (_, _) =>
        {
            if (_follow) AssistantScroll.ScrollToEnd();
        };
        AssistantScroll.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta.Y == 0) _follow = AssistantScroll.Offset.Y >= AssistantScroll.Extent.Height - AssistantScroll.Viewport.Height - 24;
        };
    }

    private bool _follow = true;

    /// <summary>Enter sends; Shift+Enter is a new line.</summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || DataContext is not AssistantViewModel vm) return;
        e.Handled = true;
        if (vm.SendCommand.CanExecute(null)) Dispatcher.UIThread.Post(() => vm.SendCommand.Execute(null));
    }
}
