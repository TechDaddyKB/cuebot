using Avalonia.Controls;
using Avalonia.Input;
using Cuebot.App.ViewModels.Midi;

namespace Cuebot.App.Views.Midi;

/// <summary>
/// MIDI page. Keys (Streamer.bot's MIDI In / MIDI Out views): Delete deletes the focused device or event, Ctrl+O turns
/// the focused event on or off.
/// </summary>
public partial class MidiView : UserControl
{
    public MidiView()
    {
        InitializeComponent();
        PageKeys.Handle(this, e =>
        {
            if (e.Key == Key.Delete && PageKeys.Plain(e))
            {
                if (PageKeys.Item<MidiDeviceRowViewModel>(e) is { } device) return PageKeys.Run(device.DeleteCommand);
                if (PageKeys.Item<MidiEventRowViewModel>(e) is { } midiEvent) return PageKeys.Run(midiEvent.DeleteCommand);
            }
            if (e.Key == Key.O && PageKeys.Ctrl(e) && PageKeys.Item<MidiEventRowViewModel>(e) is { } toggled)
            {
                toggled.Enabled = !toggled.Enabled;
                return true;
            }
            return false;
        });
    }
}
