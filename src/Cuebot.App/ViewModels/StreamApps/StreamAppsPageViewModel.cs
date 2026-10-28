namespace Cuebot.App.ViewModels.StreamApps;

/// <summary>The OBS page: OBS Studio and Streamlabs Desktop, plus Meld Studio and PolyPop, as two tabs.</summary>
public sealed class StreamAppsPageViewModel(ObsViewModel obs, StreamAppsViewModel apps) : PageViewModel("OBS")
{
    public ObsViewModel Obs { get; } = obs;

    public StreamAppsViewModel Apps { get; } = apps;
}
