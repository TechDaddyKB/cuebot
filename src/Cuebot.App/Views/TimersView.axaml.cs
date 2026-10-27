using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels;

namespace Cuebot.App.Views;

public partial class TimersView : UserControl
{
    public TimersView() => InitializeComponent();

    // Countdowns only tick while the page is on screen.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        (DataContext as TimersViewModel)?.StartLiveUpdates();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        (DataContext as TimersViewModel)?.StopLiveUpdates();
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.IsAttachedToVisualTree()) (DataContext as TimersViewModel)?.StartLiveUpdates();
    }
}
