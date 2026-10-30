using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Cuebot.App.ViewModels;

/// <summary>A page in the main navigation.</summary>
public abstract class PageViewModel(string title) : ObservableObject
{
    public string Title { get; } = title;

    /// <summary>Sub-pages the section list shows under this page while it is open (the page shows one at a time).</summary>
    public virtual IReadOnlyList<string> SubPages => [];

    public bool HasSubPages => SubPages.Count > 0;

    /// <summary>The sub-page shown (index into <see cref="SubPages"/>).</summary>
    public virtual int SubPageIndex
    {
        get => 0;
        set { }
    }

    /// <summary>Services raise events on background threads; view models update on the UI thread.</summary>
    protected static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
