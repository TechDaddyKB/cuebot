using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Cuebot.Core.Actions;

namespace Cuebot.App.Views;

/// <summary>
/// The "Get input" sub-action's dialog: a title, a prompt and a text box. OK returns the text; Cancel, Escape or
/// closing the window returns null. It stays on top so it is seen even while Cuebot is in the tray.
/// </summary>
public sealed class InputPromptWindow : Window
{
    private readonly TextBox _input;
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public InputPromptWindow(UserPromptRequest request)
    {
        Title = string.IsNullOrWhiteSpace(request.Title) ? "Cuebot" : request.Title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;

        _input = new TextBox { Text = request.OriginalText, Name = "PromptInput" };
        var ok = new Button { Content = "OK", IsDefault = true, MinWidth = 80, Name = "PromptOk" };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 80, Name = "PromptCancel" };
        ok.Click += (_, _) => Finish(_input.Text ?? "");
        cancel.Click += (_, _) => Finish(null);
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = request.Prompt, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Name = "PromptText" },
                _input,
                new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Children = { ok, cancel } },
            },
        };
        Opened += (_, _) =>
        {
            _input.Focus();
            _input.SelectAll();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Finish(null);
        };
        Closed += (_, _) => _result.TrySetResult(null);
    }

    /// <summary>The prompt shown most recently (tests answer it).</summary>
    public static InputPromptWindow? Current { get; private set; }

    /// <summary>What the user answered (null = cancelled).</summary>
    public Task<string?> Result => _result.Task;

    /// <summary>Accepts or cancels as if the buttons were pressed (also used by tests).</summary>
    public void Finish(string? text)
    {
        _result.TrySetResult(text);
        Close();
    }

    /// <summary>Shows the dialog on the UI thread and waits for the answer; cancelling the token closes it.</summary>
    public static async Task<string?> AskAsync(UserPromptRequest request, CancellationToken ct)
    {
        var window = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var w = new InputPromptWindow(request);
            Current = w;
            w.Show();
            w.Activate();
            return w;
        });
        await using var registration = ct.Register(() => Dispatcher.UIThread.Post(() => window.Finish(null)));
        return await window.Result.ConfigureAwait(false);
    }
}
