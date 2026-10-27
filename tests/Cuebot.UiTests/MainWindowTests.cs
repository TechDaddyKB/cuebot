using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Cuebot.App;
using Cuebot.App.ViewModels;
using Cuebot.App.ViewModels.Actions;
using Cuebot.App.Views;
using Cuebot.Core;

namespace Cuebot.UiTests;

/// <summary>
/// Renders every page headlessly with the real composition root, fails on binding errors,
/// and saves screenshots to tests/Cuebot.UiTests/screenshots for visual review.
/// </summary>
public sealed class MainWindowTests : IAsyncDisposable
{
    private static readonly string ScreenshotDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshots"));

    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-").FullName;
    private readonly AppHost _host;

    public MainWindowTests()
    {
        _host = AppHost.Create(new AppPaths(_dataDir));
        Directory.CreateDirectory(ScreenshotDir);
    }

    private (Window Window, MainWindowViewModel ViewModel) Open()
    {
        var vm = new MainWindowViewModel(_host);
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        Pump();
        return (window, vm);
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Save(Window window, string name)
    {
        Pump();
        window.CaptureRenderedFrame()?.Save(Path.Combine(ScreenshotDir, name + ".png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
    }

    [AvaloniaFact]
    public void Every_page_renders_without_binding_errors()
    {
        TestApp.BindingErrors.Clear();
        var app = Avalonia.Application.Current!;
        var original = app.RequestedThemeVariant;
        var (window, vm) = Open();
        try
        {
            Assert.Equal(
                ["Dashboard", "Chat", "Actions", "Commands", "Queues", "Timers", "Users & Groups", "Variables", "Watchers",
                 "Twitch", "Rumble", "YouTube", "OBS", "Speech", "Scripting", "Settings"],
                vm.Pages.Select(p => p.Title));
            foreach (var (theme, suffix) in new[] { (Cuebot.App.Shell.AppTheme.Dark, ""), (Cuebot.App.Shell.AppTheme.Light, "-light") })
            {
                vm.Settings.Theme = theme;
                foreach (var page in vm.Pages)
                {
                    vm.SelectedPage = page;
                    Save(window, $"page-{FileName(page.Title)}{suffix}");
                }
            }
        }
        finally
        {
            vm.Settings.Theme = Cuebot.App.Shell.AppTheme.Dark;
            app.RequestedThemeVariant = original;
        }
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
        window.Close();
    }

    /// <summary>"Users &amp; Groups" → "users-groups".</summary>
    internal static string FileName(string title)
    {
        var name = string.Concat(title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-'));
        while (name.Contains("--")) name = name.Replace("--", "-");
        return name.Trim('-');
    }

    [AvaloniaFact]
    public void Actions_page_list_and_graph_views_render_sample_flow()
    {
        TestApp.BindingErrors.Clear();
        var (window, vm) = Open();
        var actions = vm.Pages.OfType<ActionsViewModel>().Single();
        vm.SelectedPage = actions;

        var cheer = actions.Actions.Single(a => a.Name == "Cheer reaction");
        actions.SelectedAction = cheer;
        cheer.Selected = cheer.Steps.Items[0];
        Save(window, "actions-list");

        actions.IsGraphView = true;
        Save(window, "actions-graph");
        vm.Settings.Theme = Cuebot.App.Shell.AppTheme.Light;
        Save(window, "actions-graph-light");
        vm.Settings.Theme = Cuebot.App.Shell.AppTheme.Dark;

        // A code sub-action shows the syntax-highlighting editor in the properties panel.
        var python = actions.SelectedAction!.Registry.GetSubAction("code.python")!;
        cheer.AddStep(python, new StepInsertTarget(cheer.Steps, cheer.Steps.Items.Count));
        actions.IsGraphView = false;
        Save(window, "actions-code");

        actions.AddStepCommand.Execute(null);
        Save(window, "actions-picker");
        Assert.True(actions.Picker.IsOpen);
        actions.Picker.Search = "obs scene";
        Assert.Contains(actions.Picker.Options, o => o.Name.Contains("scene", StringComparison.OrdinalIgnoreCase));
        actions.Picker.CancelCommand.Execute(null);

        Assert.Empty(TestApp.BindingErrors.Errors);
        window.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
