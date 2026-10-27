using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Cuebot.App.ViewModels;

namespace Cuebot.App.Views;

public partial class CommandsView : UserControl
{
    public CommandsView() => InitializeComponent();

    private async void OnCopyId(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: CommandEditorViewModel editor }) return;
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
