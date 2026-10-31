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

    /// <summary>The name of the sub-page shown ("" for pages without sub-pages).</summary>
    public string SubPageTitle => HasSubPages ? SubPages[Math.Clamp(SubPageIndex, 0, SubPages.Count - 1)] : "";

    /// <summary>
    /// For pages whose sub-pages are the tabs of a hidden tab strip: <paramref name="tabs"/> lists the sub-pages shown
    /// with their tab index (tabs hidden in this host are left out).
    /// </summary>
    protected static int SubPageOf(IReadOnlyList<(string Name, int Tab)> tabs, int selectedTab) =>
        Math.Max(0, tabs.ToList().FindIndex(t => t.Tab == selectedTab));

    /// <summary>Called when the window moves to another page; pages close their dialogs here.</summary>
    public virtual void OnLeft()
    {
    }

    /// <summary>Called when the window moves to this page.</summary>
    public virtual void OnEntered()
    {
    }

    /// <summary>Services raise events on background threads; view models update on the UI thread.</summary>
    protected static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
