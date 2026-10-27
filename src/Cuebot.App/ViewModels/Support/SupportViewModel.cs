using CommunityToolkit.Mvvm.ComponentModel;
using Cuebot.App.Diagnostics;

namespace Cuebot.App.ViewModels.Support;

/// <summary>The Support page: "Report a problem" and "Updates", plus the crash prompt for the main window.</summary>
public sealed partial class SupportViewModel : PageViewModel, IDisposable
{
    public const int ReportTab = 0;
    public const int UpdatesTab = 1;

    public SupportViewModel(SupportServices services) : base("Support")
    {
        Report = new ReportProblemViewModel(services);
        Updates = new UpdatesViewModel(services);
        CrashBanner = new CrashBannerViewModel(new CrashStore(services.Paths), Report);
        CrashBanner.ReportRequested += () => SelectedTab = ReportTab;
        _ = Report.DetectGhAsync();
        if (services.CheckUpdatesOnStart) _ = Updates.CheckOnStartAsync();
    }

    public ReportProblemViewModel Report { get; }
    public UpdatesViewModel Updates { get; }

    /// <summary>Show this as a banner in the main window (see <c>CrashBannerView</c>).</summary>
    public CrashBannerViewModel CrashBanner { get; }

    [ObservableProperty] private int _selectedTab;

    public void Dispose() => Updates.Dispose();
}
