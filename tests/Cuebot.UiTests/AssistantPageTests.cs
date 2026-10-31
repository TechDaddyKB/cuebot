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

    private const string Done = "Done. **Hello** is gone. Next steps:\n\n1. Open *Actions*\n2. Check the `Raid alert` action\n\n```\n!lurk\n```";

    /// <summary>Answers, calls delete_action on "Hello" (needs approval), then reports what happened.</summary>
    private sealed class ScriptedProvider : IAssistantProvider
    {
        public string Id => "scripted";
        public string Name => "Scripted AI";
        public AssistantProviderKind Kind => AssistantProviderKind.ApiKey;
        public string? LastResult { get; private set; }

        public Task<AssistantProviderStatus> GetStatusAsync(CancellationToken ct = default) =>
            Task.FromResult(AssistantProviderStatus.Available("ready"));

        public Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["scripted-large", "scripted-small"]);

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
                yield return new AssistantText(approved ? Done : "Left it.");
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
        TestApp.SaveFrame(window, Path.Combine(ScreenshotDir, name + ".png"));
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
    public void Local_model_shows_its_server_and_options_instead_of_a_key()
    {
        var vm = new MainWindowViewModel(_host);
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        var page = Assert.IsType<AssistantViewModel>(vm.Pages.Single(p => p.Title == "Assistant"));
        vm.SelectedPage = page;
        page.SelectedChoice = page.Choices.Single(c => c.Id == "local");
        PumpUntil(() => page.IsLocal);

        Assert.False(page.UsesApiKey);
        Assert.False(page.IsCli);
        Assert.Equal(32768m, page.ContextLength);
        Assert.False(page.Thinking);
        Assert.False(page.AllTools);
        page.ServerUrl = " http://127.0.0.1:1234 ";
        page.ContextLength = 1000;   // below what Cuebot's tools need
        page.Thinking = true;
        var saved = page.Service.Settings.For("local");
        Assert.Equal("http://127.0.0.1:1234", saved.ServerUrl);
        Assert.Equal(4096, saved.ContextLength);
        Assert.True(saved.Thinking);
        // The fields follow the provider once the page has laid out (slower on CI).
        bool Shown(string name) => window.GetVisualDescendants().OfType<TextBox>().Any(t => t.Name == name && t.IsEffectivelyVisible);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!Shown("AssistantServerUrl") && DateTime.UtcNow < deadline)
        {
            Pump();
            Thread.Sleep(5);
        }
        var boxes = window.GetVisualDescendants().OfType<TextBox>().Where(t => t.Name == "AssistantServerUrl")
            .Select(t => $"visible={t.IsVisible} effective={t.IsEffectivelyVisible} parentVisible={(t.Parent as Avalonia.Visual)?.IsVisible} dc={t.DataContext?.GetType().Name}");
        Assert.True(Shown("AssistantServerUrl"),
            $"IsLocal={page.IsLocal} Effective={page.Effective?.Id} Choice={page.SelectedChoice.Id} Status={page.ProviderStatus} "
            + $"Page={vm.SelectedPage?.Title} Views={window.GetVisualDescendants().OfType<Cuebot.App.Views.Assistant.AssistantView>().Count()} "
            + $"Boxes=[{string.Join("; ", boxes)}]");
        Assert.False(Shown("AssistantApiKey"));
        Screenshot(window, "assistant-local");
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
        window.Close();
    }

    [AvaloniaFact]
    public void Chat_streams_the_answer_and_waits_for_Approve()
    {
        var vm = new MainWindowViewModel(_host);
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        var page = Assert.IsType<AssistantViewModel>(vm.Pages.Single(p => p.Title == "Assistant"));
        Assert.Contains(vm.NavGroups.Single(g => g.Name == "Home").Pages, p => p == page);
        vm.SelectedPage = page;
        Pump();

        // Picking the provider stores it; the API key field belongs to it, and its models are offered straight away.
        page.SelectedChoice = page.Choices.Single(c => c.Id == "scripted");
        PumpUntil(() => page.ProviderReady);
        Assert.True(page.UsesApiKey);
        PumpUntil(() => page.HasModels);
        Assert.Equal(["scripted-large", "scripted-small"], page.Models);
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
        Assert.Equal(Done, page.Items.Last(i => i.IsAssistant).Text);
        Screenshot(window, "assistant-done");
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));

        // The conversation is kept: a new page (as after a restart) shows it, with a note that the AI starts fresh.
        var reopened = new AssistantViewModel(page.Service);
        Assert.Equal(page.Items.Select(i => i.Text), reopened.Items.SkipLast(1).Select(i => i.Text));
        Assert.True(reopened.Items[^1].IsNotice);
        Assert.Equal("Approved", reopened.Items.Single(i => i.IsConfirmation).State);
        Assert.False(reopened.Items.Single(i => i.IsConfirmation).AwaitingAnswer);
        Assert.Equal(page.Items.Count, new AssistantViewModel(page.Service).Items.Count - 1);   // the note is not saved

        // Answers render as Markdown.
        Assert.Contains(window.GetVisualDescendants().OfType<Cuebot.App.Controls.MarkdownText>(), m => m.Markdown == Done && m.Children.Count == 4);

        page.NewChatCommand.Execute(null);
        PumpUntil(() => page.IsEmpty);
        Assert.Empty(page.Service.History);
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, true); } catch (IOException) { }
    }
}
