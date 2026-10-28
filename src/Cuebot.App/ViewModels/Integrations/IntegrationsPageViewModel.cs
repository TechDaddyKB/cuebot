using Cuebot.App.ViewModels.Elgato;
using Cuebot.App.ViewModels.StreamDeck;
using Cuebot.App.ViewModels.WaveLink;

namespace Cuebot.App.ViewModels.Integrations;

/// <summary>The Integrations page: the app and service integrations, plus Elgato (lights and webcams), Wave Link and Stream Deck as tabs.</summary>
public sealed class IntegrationsPageViewModel(IntegrationsViewModel integrations, ElgatoViewModel elgato, WaveLinkViewModel waveLink,
    StreamDeckViewModel streamDeck) : PageViewModel("Integrations")
{
    public IntegrationsViewModel Integrations { get; } = integrations;

    public ElgatoViewModel Elgato { get; } = elgato;

    public WaveLinkViewModel WaveLink { get; } = waveLink;

    public StreamDeckViewModel StreamDeck { get; } = streamDeck;
}
