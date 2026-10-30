using Cuebot.App.ViewModels.Controllers;
using Cuebot.App.ViewModels.Midi;

namespace Cuebot.App.ViewModels;

/// <summary>
/// The Input page: MIDI and game controllers, as sub-pages in the section list (voice control has its own page under
/// Audio, hotkeys theirs under Automation).
/// </summary>
public sealed class InputPageViewModel(MidiViewModel midi, ControllersViewModel controllers) : PageViewModel("Input")
{
    public MidiViewModel Midi { get; } = midi;

    public ControllersViewModel Controllers { get; } = controllers;

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

    public override IReadOnlyList<string> SubPages { get; } = ["MIDI", "Game controllers"];

    public override int SubPageIndex
    {
        get => SelectedTab;
        set
        {
            if (value >= 0) SelectedTab = value;
        }
    }
}
