using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels;

namespace Cuebot.App.Views;

/// <summary>Timers page. Keys on a timer (Streamer.bot's Timers view): Delete removes it, Ctrl+O turns it on or off.</summary>
public partial class TimersView : UserControl
{
    public TimersView()
    {
        InitializeComponent();
        PageKeys.Handle(this, e => PageKeys.Item<TimerItemViewModel>(e) is { } timer && e.Key switch
        {
            Key.Delete when PageKeys.Plain(e) => PageKeys.Run(timer.RemoveCommand),
            Key.O when PageKeys.Ctrl(e) => Toggle(timer),
            _ => false,
        });
    }

    private static bool Toggle(TimerItemViewModel timer)
    {
        timer.Enabled = !timer.Enabled;
        return true;
    }

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
