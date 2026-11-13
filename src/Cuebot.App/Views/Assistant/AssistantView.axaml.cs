using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Cuebot.App.ViewModels.Assistant;

namespace Cuebot.App.Views.Assistant;

public partial class AssistantView : UserControl
{
    public AssistantView()
    {
        InitializeComponent();
        // Tunnel: a multi-line TextBox handles Enter itself before a bubbling KeyDown handler would see it.
        AssistantInput.AddHandler(KeyDownEvent, OnInputKeyDown, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Files dropped on the message box are attached, not pasted as text.
        AssistantInput.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AssistantInput.AddHandler(DragDrop.DropEvent, OnDrop);
        // Follow the conversation as it streams in.
        AssistantItems.LayoutUpdated += (_, _) =>
        {
            if (_follow) AssistantScroll.ScrollToEnd();
        };
        AssistantScroll.ScrollChanged += (_, e) =>
        {
            if (e.ExtentDelta.Y == 0) _follow = AssistantScroll.Offset.Y >= AssistantScroll.Extent.Height - AssistantScroll.Viewport.Height - 24;
        };
    }

    private bool _follow = true;

    private async void OnAttach(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not AssistantViewModel vm || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Attach files",
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType("Pictures, videos and sounds")
                {
                    Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp", "*.ico", "*.svg", "*.mp4", "*.mov", "*.m4v", "*.webm",
                        "*.mkv", "*.mp3", "*.wav", "*.ogg", "*.opus", "*.flac", "*.m4a"],
                },
                FilePickerFileTypes.All,
            ],
        });
        vm.Attach(files.Select(f => f.TryGetLocalPath()).OfType<string>());
        AssistantInput.Focus();
    }

    private static void OnDragOver(object? sender, DragEventArgs e)
    {
        if (e.DataTransfer.TryGetFiles() is { Length: > 0 })
        {
            e.DragEffects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        if (DataContext is not AssistantViewModel vm || e.DataTransfer.TryGetFiles() is not { Length: > 0 } files) return;
        vm.Attach(files.Select(f => f.TryGetLocalPath()).OfType<string>());
        e.Handled = true;
    }

    /// <summary>Enter sends; Shift+Enter is a new line.</summary>
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift) || DataContext is not AssistantViewModel vm) return;
        e.Handled = true;
        if (vm.SendCommand.CanExecute(null)) Dispatcher.UIThread.Post(() => vm.SendCommand.Execute(null));
    }
}
