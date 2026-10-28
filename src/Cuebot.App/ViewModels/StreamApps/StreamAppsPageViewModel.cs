namespace Cuebot.App.ViewModels.StreamApps;

/// <summary>The OBS page: OBS Studio and Streamlabs Desktop, plus Meld Studio and PolyPop, as two tabs.</summary>
public sealed class StreamAppsPageViewModel(ObsViewModel obs, StreamAppsViewModel apps) : PageViewModel("OBS")
{
    public ObsViewModel Obs { get; } = obs;

    public StreamAppsViewModel Apps { get; } = apps;

    /// <summary>OBSBOT tab (null in hosts that do not show it).</summary>
    public ObsbotViewModel? Obsbot { get; init; }

    /// <summary>mtion studio tab.</summary>
    public MtionViewModel? Mtion { get; init; }
}
