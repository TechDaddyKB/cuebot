using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels.Actions;
using Cuebot.App.ViewModels.Extras;
using Cuebot.App.Views;
using Cuebot.App.Views.Extras;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.Decks;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.UiTests.Extras;

/// <summary>
/// The Decks page writes through to the deck service and renders without binding errors (screenshots
/// "extras-decks*.png"); the Get Input dialog answers and cancels; range triggers show Always Run.
/// </summary>
public sealed class DecksPageTests : IAsyncDisposable
{
    private static readonly string ScreenshotDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshots"));

    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-decks-").FullName;
    private readonly ServiceProvider _services;

    public DecksPageTests()
    {
        _services = new ServiceCollection().AddCuebotCore(new AppPaths(_dataDir)).BuildServiceProvider();
        Directory.CreateDirectory(ScreenshotDir);
        TestApp.BindingErrors.Clear();
    }

    private DeckService Decks => _services.GetRequiredService<DeckService>();

    private DecksViewModel CreatePage(bool httpEnabled = true) =>
        new(Decks, () => "http://127.0.0.1:7474", () => httpEnabled, () => ["Go live", "Mute mic"]);

    private static Window Open(Control view, object viewModel)
    {
        view.DataContext = viewModel;
        var window = new Window { Content = view, Width = 1200, Height = 860 };
        window.Show();
        Pump();
        return window;
    }

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void SaveBothThemes(Window window, string name)
    {
        var app = Avalonia.Application.Current!;
        var original = app.RequestedThemeVariant;
        try
        {
            foreach (var (theme, suffix) in new[] { (ThemeVariant.Dark, ""), (ThemeVariant.Light, "-light") })
            {
                app.RequestedThemeVariant = theme;
                Pump();
                window.CaptureRenderedFrame()?.Save(Path.Combine(ScreenshotDir, $"extras-{name}{suffix}.png"),
                    Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
            }
        }
        finally
        {
            app.RequestedThemeVariant = original;
        }
    }

    private static void AssertNoBindingErrors() =>
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));

    [AvaloniaFact]
    public void Empty_page_offers_to_add_a_deck_and_says_when_http_is_off()
    {
        var vm = CreatePage(httpEnabled: false);
        var window = Open(new DecksView(), vm);
        Assert.False(vm.HasSelection);
        Assert.Contains("HTTP server, which is off", vm.HttpHint);
        Assert.Contains(window.GetVisualDescendants().OfType<Button>(), b => b.Name == "AddDeckButton");
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public void Editing_a_deck_writes_through_to_the_service()
    {
        var vm = CreatePage();
        var window = Open(new DecksView(), vm);
        vm.AddDeckCommand.Execute(null);
        Pump();
        var deck = Assert.IsType<DeckEditorViewModel>(vm.SelectedDeck);
        deck.Name = "Stream deck";
        deck.Rows = 2;
        deck.Columns = 4;
        deck.Background = "#101010";
        Assert.Equal($"http://127.0.0.1:7474/deck/{deck.Id}", deck.Url);

        var first = Assert.Single(deck.Items);
        first.Title = "Live";
        first.Action = "Go live";
        first.ActiveTitle = "Stop";
        first.Icon = "mdi:play";
        deck.AddItemCommand.Execute(null);
        deck.Items[1].Name = "Mute";
        deck.Items[1].ToggleOnPress = true;
        deck.AddPageCommand.Execute(null);
        deck.PageName = "Sounds";
        Pump();

        var saved = Decks.Find("Stream deck")!;
        Assert.Equal((2, 4, "#101010", 2), (saved.Rows, saved.Columns, saved.Background, saved.Pages.Count));
        var live = saved.Pages[0].Items[0];
        Assert.Equal(("Live", "Stop", "Go live", "mdi:play"), (live.States[0].Title, live.States[1].Title, live.Action, live.States[0].Icon));
        Assert.Equal((0, 1, true), (saved.Pages[0].Items[1].Row, saved.Pages[0].Items[1].Column, saved.Pages[0].Items[1].ToggleOnPress));
        Assert.Equal("Sounds", saved.Pages[1].Name);
        Assert.Empty(deck.Items); // the new page is selected

        deck.SelectedPageIndex = 0;
        Pump();
        Assert.Equal(2, deck.Items.Count);
        deck.Items[1].RemoveCommand.Execute(null);
        Assert.Single(Decks.Find("Stream deck")!.Pages[0].Items);
        SaveBothThemes(window, "decks");
        AssertNoBindingErrors();

        vm.RemoveDeckCommand.Execute(null);
        Assert.Empty(Decks.Decks);
        Assert.Null(vm.SelectedDeck);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Get_input_dialog_returns_the_text_or_null()
    {
        var request = new UserPromptRequest("Question", "Your name?", "Ann");
        var ask = InputPromptWindow.AskAsync(request, CancellationToken.None);
        Pump();
        var window = FindPrompt();
        Assert.Equal("Question", window.Title);
        Assert.Equal("Ann", window.GetVisualDescendants().OfType<TextBox>().Single(t => t.Name == "PromptInput").Text);
        window.Finish("Bob");
        Pump();
        Assert.Equal("Bob", await ask);

        using var cts = new CancellationTokenSource();
        var cancelled = InputPromptWindow.AskAsync(request, cts.Token);
        Pump();
        FindPrompt();
        cts.Cancel();
        Pump();
        Assert.Null(await cancelled);
    }

    private static InputPromptWindow FindPrompt()
    {
        for (var i = 0; i < 20; i++)
        {
            Pump();
            if (InputPromptWindow.Current is { IsVisible: true } w) return w;
        }
        throw new InvalidOperationException("The prompt window did not open");
    }

    [AvaloniaFact]
    public void Range_triggers_offer_always_run()
    {
        var range = new EventTriggerType("t.bits", "T", "Bits", "t.bits",
            [new ParameterDescriptor("minBits", "Min", ParameterKind.Number), new ParameterDescriptor("maxBits", "Max", ParameterKind.Number)]);
        var changes = 0;
        var trigger = new TriggerItemViewModel(new TriggerDefinition { Type = "t.bits" }, range, _ => changes++);
        Assert.True(trigger.IsRangeTrigger);
        trigger.AlwaysRun = true;
        Assert.True(trigger.Definition.AlwaysRun);
        Assert.Equal(1, changes);
        Assert.False(new TriggerItemViewModel(new TriggerDefinition(), new EventTriggerType("x", "X", "X", "x"), _ => { }).IsRangeTrigger);
    }

    public ValueTask DisposeAsync()
    {
        _services.Dispose();
        try { Directory.Delete(_dataDir, recursive: true); }
        catch (IOException) { }
        return ValueTask.CompletedTask;
    }
}
