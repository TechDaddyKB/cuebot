using Cuebot.App.ViewModels.Elgato;
using Cuebot.App.ViewModels.GoXlr;
using Cuebot.App.ViewModels.StreamDeck;
using Cuebot.App.ViewModels.WaveLink;

namespace Cuebot.App.ViewModels.Integrations;

/// <summary>
/// The Apps &amp; devices page: the app and service integrations, Elgato (lights and webcams), Wave Link, GoXLR and
/// Stream Deck, as sub-pages in the section list.
/// </summary>
public sealed class IntegrationsPageViewModel(IntegrationsViewModel integrations, ElgatoViewModel elgato, WaveLinkViewModel waveLink,
    GoXlrViewModel goXlr, StreamDeckViewModel streamDeck) : PageViewModel("Apps & devices")
{
    public IntegrationsViewModel Integrations { get; } = integrations;

    public ElgatoViewModel Elgato { get; } = elgato;

    public WaveLinkViewModel WaveLink { get; } = waveLink;

    public GoXlrViewModel GoXlr { get; } = goXlr;

    public StreamDeckViewModel StreamDeck { get; } = streamDeck;

    private int _selectedTab;

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

    public override IReadOnlyList<string> SubPages { get; } = ["Apps & services", "Lights & cameras", "Wave Link", "GoXLR", "Stream Deck"];

    public override int SubPageIndex
    {
        get => SelectedTab;
        set
        {
            if (value >= 0) SelectedTab = value;
        }
    }
}
