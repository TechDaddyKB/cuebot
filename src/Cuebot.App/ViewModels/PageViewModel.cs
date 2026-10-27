using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cuebot.App.ViewModels;

/// <summary>A page in the main navigation.</summary>
public abstract class PageViewModel(string title) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>Services raise events on background threads; view models update on the UI thread.</summary>
    protected static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
