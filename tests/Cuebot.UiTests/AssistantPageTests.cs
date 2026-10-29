using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cuebot.App;
using Cuebot.App.ViewModels;
using Cuebot.App.ViewModels.Assistant;
using Cuebot.App.Views;
using Cuebot.Assistant;
using Cuebot.Core;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.UiTests;

/// <summary>The Assistant page with a scripted AI: picker, API key, a streamed answer, a tool call and an Approve.</summary>
public sealed class AssistantPageTests : IAsyncDisposable
{
    private static readonly string ScreenshotDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshots"));

    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-assistant-").FullName;
    private readonly ScriptedProvider _ai = new();
    private readonly AppHost _host;

    public AssistantPageTests() =>
        _host = AppHost.Create(new AppPaths(_dataDir), overrides: services => services.AddSingleton<IAssistantProvider>(_ai));

    /// <summary>Answers, calls delete_action on "Hello" (needs approval), then reports what happened.</summary>
    private sealed class ScriptedProvider : IAssistantProvider
    {
        public string Id => "scripted";
        public string Name => "Scripted AI";
        public AssistantProviderKind Kind => AssistantProviderKind.ApiKey;
        public string? LastResult { get; private set; }

        public Task<AssistantProviderStatus> GetStatusAsync(CancellationToken ct = default) =>
            Task.FromResult(AssistantProviderStatus.Available("ready"));

        public IAssistantSession StartSession(AssistantContext context) => new Session(this, context);

        private sealed class Session(ScriptedProvider owner, AssistantContext context) : IAssistantSession
        {
            public async IAsyncEnumerable<AssistantUpdate> SendAsync(string text, [EnumeratorCancellation] CancellationToken ct = default)
            {
                yield return new AssistantText("Deleting ");
                yield return new AssistantText("it now.");
                yield return new AssistantToolStarted("c1", "delete_action");
                var confirmation = new AssistantConfirmation("delete_action", "Delete the action 'Hello'");
                yield return confirmation;
                var approved = await confirmation.Answer;
                owner.LastResult = approved ? "approved" : "declined";
                yield return new AssistantToolFinished("c1", "delete_action", Failed: !approved);
                yield return new AssistantText(approved ? "Done." : "Left it.");
                _ = context;
            }

            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Screenshot(Avalonia.Controls.Window window, string name)
    {
        Pump();
        Directory.CreateDirectory(ScreenshotDir);
        window.CaptureRenderedFrame()?.Save(Path.Combine(ScreenshotDir, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    private static void PumpUntil(Func<bool> condition, [CallerArgumentExpression(nameof(condition))] string what = "")
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition() && DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(5);
        }
        Assert.True(condition(), what);
    }

    [AvaloniaFact]
    public void Chat_streams_the_answer_and_waits_for_Approve()
    {
        var vm = new MainWindowViewModel(_host);
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        var page = Assert.IsType<AssistantViewModel>(vm.Pages.Single(p => p.Title == "Assistant"));
        Assert.Contains(vm.NavGroups.Single(g => g.Name == "Main").Pages, p => p == page);
        vm.SelectedPage = page;
        Pump();

        // Picking the provider stores it; the API key field belongs to it.
        page.SelectedChoice = page.Choices.Single(c => c.Id == "scripted");
        PumpUntil(() => page.ProviderReady);
        Assert.True(page.UsesApiKey);
        page.ApiKey = "  key-123 ";
        Assert.Equal("scripted", page.Service.Settings.Provider);
        Assert.Equal("key-123", page.Service.Settings.For("scripted").ApiKey);

        // Enter in the message box sends.
        var input = window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "AssistantInput");
        input.Focus();
        page.Input = "Delete my Hello action";
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        PumpUntil(() => page.Items.Any(i => i.AwaitingAnswer));

        Assert.Equal("Delete my Hello action", page.Items[0].Text);
        Assert.Equal("Deleting it now.", page.Items.Single(i => i.IsAssistant).Text);
        Assert.Equal("running", page.Items.Single(i => i.IsTool).State);
        Assert.True(page.IsBusy);

        Screenshot(window, "assistant-confirm");
        var approve = window.GetVisualDescendants().OfType<Button>().Single(b => b.Content as string == "Approve" && b.IsEffectivelyVisible);
        approve.Command!.Execute(null);
        PumpUntil(() => !page.IsBusy);

        Assert.Equal("approved", _ai.LastResult);
        Assert.Equal("done", page.Items.Single(i => i.IsTool).State);
        Assert.Equal("Approved", page.Items.Single(i => i.IsConfirmation).State);
        Assert.Equal("Done.", page.Items.Last(i => i.IsAssistant).Text);
        Screenshot(window, "assistant-done");
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));

        page.NewChatCommand.Execute(null);
        PumpUntil(() => page.IsEmpty);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }
}
