using Avalonia.Controls;
using Avalonia.Input;
using Cuebot.App.ViewModels.VoiceControl;

namespace Cuebot.App.Views.VoiceControl;

/// <summary>
/// Voice Control page. Keys on the command list (Streamer.bot's Voice Control > Commands view): Delete deletes the
/// selected command, Ctrl+Up / Ctrl+Down move it, Ctrl+Home / Ctrl+End move it to the top / bottom.
/// </summary>
public partial class VoiceControlView : UserControl
{
    public VoiceControlView()
    {
        InitializeComponent();
        PageKeys.Handle(VoiceCommands, e => DataContext is VoiceControlViewModel vm && e.Key switch
        {
            Key.Delete when PageKeys.Plain(e) => PageKeys.Run(vm.RemoveCommandCommand),
            Key.Up when PageKeys.Ctrl(e) => PageKeys.Run(vm.MoveUpCommand),
            Key.Down when PageKeys.Ctrl(e) => PageKeys.Run(vm.MoveDownCommand),
            Key.Home when PageKeys.Ctrl(e) => PageKeys.Run(vm.MoveToTopCommand),
            Key.End when PageKeys.Ctrl(e) => PageKeys.Run(vm.MoveToBottomCommand),
            _ => false,
        }, selected: () => (DataContext as VoiceControlViewModel)?.Selected);
    }
}
