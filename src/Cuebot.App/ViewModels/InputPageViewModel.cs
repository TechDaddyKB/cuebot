using Cuebot.App.ViewModels.Controllers;
using Cuebot.App.ViewModels.Midi;
using Cuebot.App.ViewModels.VoiceControl;

namespace Cuebot.App.ViewModels;

/// <summary>The Input page: voice control, MIDI and game controllers as tabs (hotkeys keep their own page).</summary>
public sealed class InputPageViewModel(VoiceControlViewModel voiceControl, MidiViewModel midi, ControllersViewModel controllers)
    : PageViewModel("Input")
{
    public VoiceControlViewModel VoiceControl { get; } = voiceControl;

    public MidiViewModel Midi { get; } = midi;

    public ControllersViewModel Controllers { get; } = controllers;
}
