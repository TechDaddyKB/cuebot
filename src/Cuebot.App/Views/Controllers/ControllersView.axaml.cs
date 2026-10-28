using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels.Controllers;

namespace Cuebot.App.Views.Controllers;

public partial class ControllersView : UserControl
{
    private ControllersViewModel? _live;

    public ControllersView() => InitializeComponent();

    // Controllers are only held open and refreshed while the page is on screen.
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Attach(DataContext as ControllersViewModel);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Attach(null);
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (this.IsAttachedToVisualTree()) Attach(DataContext as ControllersViewModel);
    }

    private void Attach(ControllersViewModel? vm)
    {
        if (ReferenceEquals(vm, _live)) return;
        _live?.StopLiveUpdates();
        _live = vm;
        _live?.StartLiveUpdates();
    }
}
