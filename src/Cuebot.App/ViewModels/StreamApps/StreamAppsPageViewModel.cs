namespace Cuebot.App.ViewModels.StreamApps;

/// <summary>
/// The Stream apps page: OBS Studio and Streamlabs Desktop, Meld Studio and PolyPop, T.I.T.S., Lumia Stream, OBSBOT,
/// mtion and StreamFog, as sub-pages in the section list.
/// </summary>
public sealed class StreamAppsPageViewModel(ObsViewModel obs, StreamAppsViewModel apps) : PageViewModel("Stream apps")
{
    public ObsViewModel Obs { get; } = obs;

    public StreamAppsViewModel Apps { get; } = apps;

    public TitsViewModel? Tits { get; init; }

    public LumiaViewModel? Lumia { get; init; }
    /// <summary>OBSBOT tab (null in hosts that do not show it).</summary>
    public ObsbotViewModel? Obsbot { get; init; }

    /// <summary>mtion studio tab.</summary>
    public MtionViewModel? Mtion { get; init; }

    private int _selectedTab;

    /// <summary>The tab shown (some tabs are hidden in hosts without that app).</summary>
    public int SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!SetProperty(ref _selectedTab, value)) return;
            OnPropertyChanged(nameof(SubPageIndex));
            OnPropertyChanged(nameof(SubPageTitle));
        }
    }

    private IReadOnlyList<(string Name, int Tab)> Tabs =>
    [
        ("OBS & Streamlabs Desktop", 0), ("Meld & PolyPop", 1),
        .. Tits is null ? [] : new[] { ("T.I.T.S.", 2) },
        .. Lumia is null ? [] : new[] { ("Lumia Stream", 3) },
        .. Obsbot is null ? [] : new[] { ("OBSBOT", 4) },
        .. Mtion is null ? [] : new[] { ("mtion", 5) },
        ("StreamFog", 6),
    ];

    public override IReadOnlyList<string> SubPages => Tabs.Select(t => t.Name).ToArray();

    public override int SubPageIndex
    {
        get => SubPageOf(Tabs, SelectedTab);
        set
        {
            if (value >= 0 && value < Tabs.Count) SelectedTab = Tabs[value].Tab;
        }
    }
}
