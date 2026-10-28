using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Cuebot.App.ViewModels;
using Cuebot.Core.Actions;

namespace Cuebot.App.Views;

/// <summary>
/// Commands page. List shortcuts (Streamer.bot's Commands view): Delete deletes the selected command, Ctrl+O turns it
/// on or off, Ctrl+C copies its id, Ctrl+R renames its group, Ctrl+* / Ctrl+- expand / collapse every group.
/// </summary>
public partial class CommandsView : UserControl
{
    public CommandsView()
    {
        InitializeComponent();
        PageKeys.Handle(CommandList, OnListKey);
    }

    private CommandsViewModel? Vm => DataContext as CommandsViewModel;

    /// <summary>Asks for the new group name; tests replace it.</summary>
    internal Func<UserPromptRequest, Task<string?>> AskGroupName { get; set; } =
        request => InputPromptWindow.AskAsync(request, CancellationToken.None);

    private bool OnListKey(KeyEventArgs e)
    {
        if (Vm is not { } vm) return false;
        var ctrl = PageKeys.Ctrl(e);
        switch (e.Key)
        {
            case Key.Delete when PageKeys.Plain(e):
                return PageKeys.Run(vm.DeleteCommandCommand);
            case Key.O when ctrl:
                return PageKeys.Run(vm.ToggleSelectedEnabledCommand);
            case Key.C when ctrl && vm.Editor is { } editor:
                _ = CopyIdAsync(editor);
                return true;
            case Key.R when ctrl && vm.SelectedGroup is { Length: > 0 }:
                _ = RenameGroupAsync(vm);
                return true;
            case Key.Multiply or Key.D8 when ctrl:
                return PageKeys.Run(vm.ExpandAllCommand);
            case Key.Subtract or Key.OemMinus when ctrl:
                return PageKeys.Run(vm.CollapseAllCommand);
        }
        return false;
    }

    private async Task RenameGroupAsync(CommandsViewModel vm)
    {
        var old = vm.SelectedGroup ?? "";
        var name = await AskGroupName(new UserPromptRequest("Rename group", $"New name for the group \"{old}\":", old));
        if (!string.IsNullOrWhiteSpace(name) && name.Trim() != old) vm.RenameSelectedGroup(name);
    }

    private async void OnCopyId(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: CommandEditorViewModel editor }) await CopyIdAsync(editor);
    }

    private async Task CopyIdAsync(CommandEditorViewModel editor)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            try
            {
                await clipboard.SetTextAsync(editor.IdText);
                editor.Status = "Id copied.";
            }
            catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException)
            {
                editor.Status = "Could not copy the id.";
            }
        }
    }
}
