using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cuebot.App;
using Cuebot.App.Graph;
using Cuebot.App.Shell;
using Cuebot.App.ViewModels;
using Cuebot.App.ViewModels.Actions;
using Cuebot.App.ViewModels.History;
using Cuebot.App.Views;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Events;

namespace Cuebot.UiTests;

/// <summary>
/// Actions editor copy/paste/duplicate and undo/redo, the History page (action history + trigger viewer) and the
/// grouped sidebar, against the real composition root. Screenshots go to tests/Cuebot.UiTests/screenshots.
/// </summary>
public sealed class ActionsUxTests : IAsyncDisposable
{
    private static readonly string ScreenshotDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshots"));

    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-ux-").FullName;
    private readonly AppHost _host;

    public ActionsUxTests()
    {
        _host = AppHost.Create(new AppPaths(_dataDir));
        Directory.CreateDirectory(ScreenshotDir);
    }

    private ProfileService Profile => _host.Get<ProfileService>();
    private ActionEngine Engine => _host.Get<ActionEngine>();

    private (Window Window, MainWindowViewModel ViewModel) Open()
    {
        var vm = new MainWindowViewModel(_host);
        var window = new MainWindow { DataContext = vm, Width = 1360, Height = 860 };
        window.Show();
        Pump();
        return (window, vm);
    }

    private static T Page<T>(MainWindowViewModel vm) where T : PageViewModel => vm.Pages.OfType<T>().Single();

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

    private static void AssertNoBindingErrors() =>
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));

    private static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new TimeoutException("Condition not met");
            await Task.Delay(10);
            Pump();
        }
    }

    private static SubActionDefinition Step(string type, params (string Key, string Value)[] parameters)
    {
        var step = new SubActionDefinition { Type = type };
        foreach (var (k, v) in parameters) step.Parameters[k] = v;
        return step;
    }

    /// <summary>An action with a trigger, a plain step and a Group holding two children.</summary>
    private ActionDefinition AddGroupedAction(string name = "Grouped")
    {
        var group = Step(GroupSubAction.Id, ("name", "Greeting"));
        group.Branch(GroupSubAction.Steps).AddRange([Step("core.setArgument", ("name", "a"), ("value", "1")),
            Step("core.setArgument", ("name", "b"), ("value", "2"))]);
        var trigger = new TriggerDefinition { Type = Profile.Snapshot.Actions.SelectMany(a => a.Triggers).First().Type };
        var action = new ActionDefinition
        {
            Name = name,
            Triggers = [trigger],
            SubActions = [Step("core.setArgument", ("name", "first"), ("value", "x")), group],
        };
        Profile.Profile.Actions.Add(action);
        Profile.NotifyChanged();
        return action;
    }

    private static IEnumerable<Guid> AllIds(ActionDefinition a) =>
        a.Triggers.Select(t => t.Id).Concat(Steps(a.SubActions).Select(s => s.Id)).Prepend(a.Id);

    private static IEnumerable<SubActionDefinition> Steps(IEnumerable<SubActionDefinition> list) =>
        list.SelectMany(s => Steps(s.Branches.Values.SelectMany(b => b)).Prepend(s));

    // ---- Copy / paste ----

    [AvaloniaFact]
    public async Task Copy_paste_gives_new_ids_and_copies_group_children()
    {
        AddGroupedAction();
        TestApp.BindingErrors.Clear();
        var (window, vm) = Open();
        var actions = Page<ActionsViewModel>(vm);
        vm.SelectedPage = actions;
        string? systemClipboard = null;
        actions.WriteSystemClipboard = text => { systemClipboard = text; return Task.CompletedTask; };
        actions.ReadSystemClipboard = () => Task.FromResult(systemClipboard);

        var item = actions.Actions.Single(a => a.Name == "Grouped");
        actions.SelectedAction = item;
        var group = item.Steps.Items.Single(s => s.IsGroup);
        item.Selected = group;

        // Sub-action (a group with children) → system clipboard JSON → paste after the selection.
        await actions.CopyItemCommand.ExecuteAsync(null);
        Assert.NotNull(systemClipboard);
        Assert.Equal(ClipKind.SubActions, EditorClip.TryParse(systemClipboard)!.Kind);
        await actions.PasteCommand.ExecuteAsync(null);
        Assert.Equal(3, item.Steps.Items.Count);
        var pasted = item.Steps.Items[2];
        Assert.True(pasted.IsGroup);
        Assert.Same(pasted, item.Selected);
        Assert.Equal(2, pasted.Branches[0].Steps.Items.Count);
        Assert.Equal(["a", "b"], pasted.Branches[0].Steps.Items.Select(s => s.Definition.Parameters["name"]));
        Assert.NotEqual(group.Definition.Id, pasted.Definition.Id);
        var ids = AllIds(item.Definition).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        // Saved to the profile the engine runs.
        Assert.Equal(3, Profile.Snapshot.FindAction(item.Definition.Id)!.SubActions.Count);

        // Pasting into a branch: select the pasted group's branch, paste the in-app clip there (no system clipboard).
        actions.ReadSystemClipboard = null;
        item.Selected = pasted.Branches[0];
        await actions.PasteCommand.ExecuteAsync(null);
        Assert.Equal(3, pasted.Branches[0].Steps.Items.Count);
        Assert.True(pasted.Branches[0].Steps.Items[2].IsGroup);

        // Trigger copy/paste and duplicate.
        var trigger = item.Triggers[0];
        item.Selected = trigger;
        await actions.CopyItemCommand.ExecuteAsync(null);
        await actions.PasteCommand.ExecuteAsync(null);
        actions.DuplicateItemCommand.Execute(null);
        Assert.Equal(3, item.Triggers.Count);
        Assert.Equal(3, item.Triggers.Select(t => t.Definition.Id).Distinct().Count());
        Assert.All(item.Triggers, t => Assert.Equal(trigger.Definition.Type, t.Definition.Type));

        // Whole action, as if pasted into another Cuebot from the system clipboard text.
        await actions.CopySelectionAsync(wholeAction: true);
        var json = systemClipboard!;
        var clip = EditorClip.TryParse(json)!;
        Assert.Equal(ClipKind.Actions, clip.Kind);
        actions.ReadSystemClipboard = () => Task.FromResult<string?>(json);
        await actions.PasteCommand.ExecuteAsync(null);
        var copy = actions.SelectedAction!;
        Assert.Equal("Grouped (copy)", copy.Name);
        Assert.NotEqual(item.Definition.Id, copy.Definition.Id);
        Assert.Empty(AllIds(copy.Definition).Intersect(AllIds(item.Definition)));
        Assert.Equal(Steps(item.Definition.SubActions).Count(), Steps(copy.Definition.SubActions).Count());
        Assert.NotNull(Profile.Snapshot.FindAction(copy.Definition.Id));

        // Random clipboard text is not pasted as actions.
        Assert.Null(EditorClip.TryParse("hello"));
        Assert.Null(EditorClip.TryParse("{\"name\":\"x\"}"));

        Save(window, "actions-paste");
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public void Keyboard_shortcuts_copy_paste_duplicate_and_undo_in_graph_and_list()
    {
        AddGroupedAction();
        var (window, vm) = Open();
        var actions = Page<ActionsViewModel>(vm);
        vm.SelectedPage = actions;
        var item = actions.SelectedAction = actions.Actions.Single(a => a.Name == "Grouped");
        actions.IsGraphView = true;
        Pump();

        var graph = window.GetVisualDescendants().OfType<FlowGraphControl>().Single();
        graph.Focus();
        item.Selected = item.Steps.Items[0];
        window.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.D, "d");
        Pump();
        Assert.Equal(3, item.Steps.Items.Count);
        Assert.Equal("first", item.Steps.Items[1].Definition.Parameters["name"]);
        Assert.NotEqual(item.Steps.Items[0].Definition.Id, item.Steps.Items[1].Definition.Id);

        window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
        Pump();
        item = actions.SelectedAction!;
        Assert.Equal(2, item.Steps.Items.Count);
        window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
        Pump();
        item = actions.SelectedAction!;
        Assert.Equal(3, item.Steps.Items.Count);
        window.KeyPress(Key.Z, RawInputModifiers.Control | RawInputModifiers.Shift, PhysicalKey.Z, "Z");
        Pump();
        Assert.Equal(3, actions.SelectedAction!.Steps.Items.Count); // nothing left to redo

        // List view: Ctrl+C on a step then Ctrl+V pastes it after the selection.
        actions.IsGraphView = false;
        Pump();
        item = actions.SelectedAction!;
        var rowButton = window.GetVisualDescendants().OfType<Button>().First(b => b.DataContext == item.Steps.Items[0]);
        rowButton.Focus();
        item.Selected = item.Steps.Items[0];
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        Pump();
        Assert.Equal(ClipKind.SubActions, actions.Clip!.Kind);
        window.KeyPress(Key.V, RawInputModifiers.Control, PhysicalKey.V, "v");
        Pump();
        Assert.Equal(4, actions.SelectedAction!.Steps.Items.Count);

        // In a text field the shortcuts belong to the text box.
        var name = window.GetVisualDescendants().OfType<TextBox>().First(t => t.Text == item.Name);
        name.Focus();
        window.KeyPress(Key.D, RawInputModifiers.Control, PhysicalKey.D, "d");
        Pump();
        Assert.Equal(4, actions.SelectedAction!.Steps.Items.Count);

        // Ctrl+C with the action list focused copies the whole action.
        var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "ActionList");
        list.ContainerFromItem(actions.SelectedAction!)!.Focus();
        window.KeyPress(Key.C, RawInputModifiers.Control, PhysicalKey.C, "c");
        Pump();
        Assert.Equal(ClipKind.Actions, actions.Clip!.Kind);
        window.Close();
    }

    // ---- Undo / redo ----

    [AvaloniaFact]
    public void Undo_redo_sequences_coalesce_typing_and_keep_history_per_action()
    {
        AddGroupedAction();
        AddGroupedAction("Other");
        TestApp.BindingErrors.Clear();
        var (window, vm) = Open();
        var actions = Page<ActionsViewModel>(vm);
        vm.SelectedPage = actions;
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        actions.Clock = () => now;

        var item = actions.SelectedAction = actions.Actions.Single(a => a.Name == "Grouped");
        Assert.False(actions.CanUndo);
        Assert.False(actions.UndoCommand.CanExecute(null));

        // 1. Typing the name: rapid edits are one step.
        foreach (var name in new[] { "G", "Gr", "Gre", "Greet" })
        {
            item.Name = name;
            now = now.AddMilliseconds(200);
        }
        // 2. A toggle is its own step.
        now = now.AddSeconds(5);
        item.AlwaysRun = true;
        // 3. Typing in a parameter, then pausing, then typing again: two steps.
        now = now.AddSeconds(5);
        var param = (TextParameterViewModel)item.Steps.Items[0].Parameters.First(p => p.Descriptor.Key == "value");
        param.Value = "y";
        now = now.AddMilliseconds(300);
        param.Value = "yz";
        now = now.AddSeconds(3);
        param.Value = "yz!";
        // 4. A structural edit: delete the group.
        item.Selected = item.Steps.Items[1];
        item.DeleteSelected();
        Assert.True(actions.CanUndo);

        actions.UndoCommand.Execute(null); // group back
        item = actions.SelectedAction!;
        Assert.Equal(2, item.Steps.Items.Count);
        Assert.True(item.Steps.Items[1].IsGroup);
        Assert.Equal(2, item.Steps.Items[1].Branches[0].Steps.Items.Count);
        actions.UndoCommand.Execute(null); // "yz!" → "yz"
        Assert.Equal("yz", actions.SelectedAction!.Steps.Items[0].Definition.Parameters["value"]);
        actions.UndoCommand.Execute(null); // → "x"
        Assert.Equal("x", actions.SelectedAction!.Steps.Items[0].Definition.Parameters["value"]);
        actions.UndoCommand.Execute(null); // AlwaysRun off
        Assert.False(actions.SelectedAction!.AlwaysRun);
        Assert.Equal("Greet", actions.SelectedAction.Name);
        actions.UndoCommand.Execute(null); // name back
        Assert.Equal("Grouped", actions.SelectedAction!.Name);
        Assert.False(actions.CanUndo);
        // The profile the engine reads follows undo.
        Assert.Equal("Grouped", Profile.Snapshot.FindAction(item.Definition.Id)!.Name);

        actions.RedoCommand.Execute(null);
        actions.RedoCommand.Execute(null);
        Assert.Equal("Greet", actions.SelectedAction!.Name);
        Assert.True(actions.SelectedAction.AlwaysRun);
        Assert.True(actions.CanRedo);

        // History is kept per action: edit another action, come back, redo still works.
        var other = actions.SelectedAction = actions.Actions.Single(a => a.Name == "Other");
        Assert.False(actions.CanUndo);
        other.Enabled = false;
        Assert.True(actions.CanUndo);
        actions.SelectedAction = actions.Actions.Single(a => a.Definition.Id == item.Definition.Id);
        Assert.True(actions.CanRedo);
        actions.RedoCommand.Execute(null);
        Assert.Equal("yz", actions.SelectedAction!.Steps.Items[0].Definition.Parameters["value"]);

        // A new edit clears redo.
        actions.SelectedAction.Group = "Changed";
        Assert.False(actions.CanRedo);

        // An outside change (the "Set action state" sub-action) is adopted, not an undo step: one undo still goes
        // back to before the group edit, and neither undo nor redo re-enables the action.
        Assert.True(Profile.SetActionEnabledAsync("Greet", false).IsCompleted);
        Pump();
        Assert.False(actions.SelectedAction!.Enabled);
        actions.UndoCommand.Execute(null);
        Assert.Equal("", actions.SelectedAction!.Group);
        Assert.Equal("yz", actions.SelectedAction.Steps.Items[0].Definition.Parameters["value"]);
        Assert.False(actions.SelectedAction.Enabled);
        actions.RedoCommand.Execute(null);
        Assert.Equal("Changed", actions.SelectedAction!.Group);
        Assert.False(actions.SelectedAction.Enabled);
        Assert.False(Profile.Snapshot.FindAction(item.Definition.Id)!.Enabled);

        Save(window, "actions-undo");
        AssertNoBindingErrors();
        window.Close();
    }

    [Fact]
    public void Edit_history_is_bounded_and_coalesces_by_key()
    {
        var history = new EditHistory("0");
        var t = DateTime.UtcNow;
        for (var i = 1; i <= EditHistory.MaxDepth + 20; i++) history.Record(i.ToString(), null, t);
        Assert.Equal(EditHistory.MaxDepth, history.UndoDepth);
        var undone = 0;
        while (history.Undo() is not null) undone++;
        Assert.Equal(EditHistory.MaxDepth, undone);
        Assert.Equal("20", history.Current);

        var h = new EditHistory("a");
        Assert.False(h.Record("a", "k", t));
        h.Record("ab", "k", t);
        h.Record("abc", "k", t.AddMilliseconds(500));
        h.Record("abcd", "other", t.AddMilliseconds(600));
        h.Record("abcde", "other", t.AddSeconds(5)); // window passed
        Assert.Equal(3, h.UndoDepth);
        Assert.Equal("abcd", h.Undo());
        Assert.Equal("abc", h.Undo());
        Assert.Equal("a", h.Undo());
        Assert.Null(h.Undo());
        Assert.Equal("abc", h.Redo());
    }

    // ---- History page ----

    [AvaloniaFact]
    public async Task History_page_shows_real_runs_inspects_args_and_runs_again()
    {
        var action = new ActionDefinition
        {
            Name = "Echo",
            SubActions = [Step("core.setArgument", ("name", "echo"), ("value", "%user%"))],
        };
        var failing = new ActionDefinition { Name = "Broken", SubActions = [Step("core.doesNotExist")] };
        Profile.Profile.Actions.AddRange([action, failing]);
        Profile.NotifyChanged();
        TestApp.BindingErrors.Clear();
        var (window, vm) = Open();
        var history = Page<HistoryViewModel>(vm);
        vm.SelectedPage = history;
        Assert.Equal(vm.NavGroups.Single(g => g.Name == "Chat & Events"), vm.GroupOf(history));

        Assert.True(await Engine.RunAsync(Profile.Snapshot.FindAction("Echo")!,
            new Dictionary<string, object?> { ["user"] = "Dana" }, source: "Twitch › Chat message"));
        await Engine.RunAsync(Profile.Snapshot.FindAction("Broken")!);
        await Until(() => history.Runs.Count == 2 && history.Runs.All(r => !r.IsActive));

        var echo = history.Runs.Single(r => r.ActionName == "Echo");
        history.SelectedRun = echo;
        Assert.Contains("Twitch › Chat message", history.SelectedDetails);
        Assert.Contains(history.ArgsWhenQueued, r => r is { Name: "user", Value: "Dana", Changed: false });
        Assert.True(history.HasArgsAfterRun);
        Assert.Contains(history.ArgsAfterRun, r => r is { Name: "echo", Value: "Dana", Changed: true });
        Assert.Contains(history.ArgsAfterRun, r => r is { Name: "user", Changed: false });
        Save(window, "history-page");

        // Search and state filter.
        history.Search = "dana";
        Assert.Same(echo, Assert.Single(history.Runs));
        history.Search = "";
        history.StateFilter = "Completed";
        Assert.All(history.Runs, r => Assert.True(r.IsDone));
        history.StateFilter = HistoryViewModel.StateFilters[0];
        Assert.Equal(2, history.Runs.Count);

        // Run again with the same args: a new "Requeue" run of Echo with user=Dana.
        history.SelectedRun = echo;
        await history.RunAgainCommand.ExecuteAsync(null);
        await Until(() => history.Runs.Count == 3 && history.Runs.All(r => !r.IsActive));
        var again = history.Runs.First(r => r.Source == "Requeue");
        Assert.Equal("Echo", again.ActionName);
        Assert.Equal("Dana", again.Entry.ArgsWhenQueued["user"]);

        // Pending work: a paused queue shows in the summary; Open Queues links to that page; cancel works here too.
        Profile.Profile.Queues.Add(new QueueDefinition { Name = "Hold", Blocking = true });
        Profile.Profile.Actions.Add(new ActionDefinition { Name = "Held", Queue = "Hold", SubActions = [Step(CommentSubAction.Id)] });
        Profile.NotifyChanged();
        Engine.Queues.Pause("Hold");
        var held = Engine.RunAsync(Profile.Snapshot.FindAction("Held")!);
        await Until(() => history.ActiveCount == 1);
        Assert.Contains("Hold: 0 running, 1 pending (paused)", history.PendingSummary);
        history.SelectedRun = history.Runs.First(r => r.IsActive);
        Assert.True(history.CanCancelSelected);
        history.CancelRunCommand.Execute(null);
        try { Assert.False(await held); } catch (OperationCanceledException) { }
        await Until(() => history.ActiveCount == 0);
        history.OpenQueuesCommand.Execute(null);
        Assert.IsType<QueuesViewModel>(vm.SelectedPage);
        Engine.Queues.Resume("Hold");

        // Open action jumps to the Actions page with it selected.
        vm.SelectedPage = history;
        history.SelectedRun = history.Runs.First(r => r.ActionName == "Echo");
        history.OpenActionCommand.Execute(null);
        var actions = Assert.IsType<ActionsViewModel>(vm.SelectedPage);
        Assert.Equal("Echo", actions.SelectedAction!.Name);

        vm.SelectedPage = history;
        vm.Settings.Theme = AppTheme.Light;
        history.SelectedRun = history.Runs.First(r => r.ActionName == "Broken");
        Assert.True(history.HasSelectedError);
        Save(window, "history-page-light");
        vm.Settings.Theme = AppTheme.Dark;
        AssertNoBindingErrors();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Trigger_viewer_records_events_pauses_and_creates_an_action()
    {
        TestApp.BindingErrors.Clear();
        var (window, vm) = Open();
        var history = Page<HistoryViewModel>(vm);
        vm.SelectedPage = history;
        history.SelectedTab = 1;
        var viewer = history.Triggers;
        viewer.ClearCommand.Execute(null);
        var registry = _host.Get<Registry>();
        var chat = registry.Triggers.First(t => t.EventTypes.Contains("twitch.chatMessage"));
        var bus = _host.Get<EventBus>();

        bus.Publish(new BotEvent("twitch.chatMessage", new Dictionary<string, object?> { ["user"] = "Dana", ["message"] = "hello there" }));
        // Events from other threads are marshalled to the UI thread.
        await Task.Run(() => bus.Publish(new BotEvent("custom.somethingElse", new Dictionary<string, object?> { ["n"] = 5 })));
        int Ours() => viewer.Events.Count(e => e.Type is "twitch.chatMessage" or "custom.somethingElse");
        await Until(() => Ours() == 2);
        Assert.True(viewer.Events.ToList().FindIndex(e => e.Type == "custom.somethingElse") < viewer.Events.ToList().FindIndex(e => e.Type == "twitch.chatMessage")); // newest first

        viewer.SelectedEvent = viewer.Events.Single(e => e.Type == "twitch.chatMessage");
        Assert.Contains(viewer.Args, a => a.Name == "message" && a.Value == "hello there");
        Assert.True(viewer.HasTriggerOptions);
        Assert.Equal(chat, viewer.SelectedTrigger!.Type);
        Save(window, "history-trigger-viewer");
        vm.Settings.Theme = AppTheme.Light;
        Save(window, "history-trigger-viewer-light");
        vm.Settings.Theme = AppTheme.Dark;

        viewer.Search = "hello";
        Assert.Single(viewer.Events, e => e.Type == "twitch.chatMessage");
        viewer.Search = "";
        Assert.Equal(2, Ours());

        viewer.TogglePauseCommand.Execute(null);
        bus.Publish(new BotEvent("twitch.chatMessage"));
        Pump();
        Assert.Equal(2, Ours());
        Assert.True(viewer.IgnoredWhilePaused >= 1);
        viewer.TogglePauseCommand.Execute(null);
        bus.Publish(new BotEvent("twitch.chatMessage"));
        Pump();
        Assert.Equal(3, Ours());

        // "Create action from this trigger" opens the new action on the Actions page.
        viewer.SelectedEvent = viewer.Events.Last(e => e.Type == "twitch.chatMessage");
        viewer.CreateActionCommand.Execute(null);
        var actions = Assert.IsType<ActionsViewModel>(vm.SelectedPage);
        var created = actions.SelectedAction!;
        Assert.Equal(chat.DisplayName, created.Name);
        Assert.Equal(chat.TypeId, Assert.Single(created.Definition.Triggers).Type);
        Assert.NotNull(Profile.Snapshot.FindAction(created.Definition.Id));

        viewer.ClearCommand.Execute(null);
        Assert.Empty(viewer.Events);
        Assert.False(viewer.HasSelection);
        AssertNoBindingErrors();
        window.Close();
    }

    // ---- Navigation ----

    [AvaloniaFact]
    public void Rail_and_section_list_navigate_and_fit_at_860()
    {
        TestApp.BindingErrors.Clear();
        var app = Avalonia.Application.Current!;
        var original = app.RequestedThemeVariant;
        var (window, vm) = Open();
        try
        {
            Assert.Equal(["Home", "Automation", "Chat & Events", "Audio", "Decks & Input", "Platforms", "Overlays & Widgets", "Integrations", "Settings"],
                vm.NavGroups.Select(g => g.Name));
            Assert.Equal(["Settings"], vm.RailBottom.Select(g => g.Name));
            Assert.Equal(["Dashboard", "Assistant"], vm.NavGroups[0].Pages.Select(p => p.Title));
            // Every page is in exactly one section.
            var grouped = vm.NavGroups.SelectMany(g => g.Pages).ToList();
            Assert.Equal(vm.Pages.Count, grouped.Count);
            Assert.Equal(vm.Pages.ToHashSet(), grouped.ToHashSet());
            Assert.All(vm.Pages, p => Assert.True(MainWindowViewModel.PageDescriptions.ContainsKey(p.Title), p.Title));

            // The rail fits at 860 px without scrolling.
            var rail = window.GetVisualDescendants().OfType<ItemsControl>().Single(i => i.Name == "Rail");
            var scroll = rail.FindAncestorOfType<ScrollViewer>()!;
            Assert.True(scroll.Extent.Height <= scroll.Viewport.Height + 0.5,
                $"Rail needs {scroll.Extent.Height} px but has {scroll.Viewport.Height}");
            foreach (var (theme, suffix) in new[] { (AppTheme.Dark, ""), (AppTheme.Light, "-light") })
            {
                vm.Settings.Theme = theme;
                Save(window, $"shell{suffix}");
            }

            // A rail click opens the section's first page, then the page last visited there.
            var automation = vm.NavGroups.Single(g => g.Name == "Automation");
            vm.SelectGroupCommand.Execute(automation);
            Assert.Same(automation.Pages[0], vm.SelectedPage);
            Assert.Same(automation, vm.CurrentGroup);
            Assert.True(automation.IsCurrent);
            Assert.All(vm.NavGroups.Where(g => g != automation), g => Assert.False(g.IsCurrent));
            automation.Selected = automation.Pages[2];
            Assert.Same(automation.Pages[2], vm.SelectedPage);
            vm.SelectGroupCommand.Execute(vm.NavGroups[0]);
            vm.SelectGroupCommand.Execute(automation);
            Assert.Same(automation.Pages[2], vm.SelectedPage);
            Pump();
            Assert.Equal(automation.Pages[2].Title, window.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "PageTitle").Text);
            Assert.Equal(MainWindowViewModel.PageDescriptions[automation.Pages[2].Title], vm.PageDescription);

            // The section list shows only the current section; it can be closed (remembered) and opened again.
            var list = window.GetVisualDescendants().OfType<ListBox>().Single(l => l.Name == "SectionPages");
            Assert.Equal(automation.Pages.Count, list.ItemCount);
            vm.ToggleSectionListCommand.Execute(null);
            Pump();
            Assert.False(list.IsEffectivelyVisible);
            Assert.True(_host.Settings.Settings.General.CollapseSectionList);
            Save(window, "shell-rail-only");
            vm.ToggleSectionListCommand.Execute(null);
            Assert.False(_host.Settings.Settings.General.CollapseSectionList);

            // Jump to a page by name (Ctrl+K box).
            Assert.True(vm.JumpTo("twitch"));
            Assert.Equal("Twitch", vm.SelectedPage.Title);
            Assert.Equal("Platforms", vm.CurrentGroup!.Name);
            Assert.False(vm.JumpTo("no such page"));
            Assert.Contains(AppShortcuts.Window, s => s.Id == "jump" && s.Gesture == "Ctrl+K");

            // The status bar counts actions and triggers.
            Assert.StartsWith("Actions ", vm.Status.Counts);
            Assert.Equal("Nothing connected", vm.Status.Connections);
        }
        finally
        {
            vm.Settings.Theme = AppTheme.Dark;
            app.RequestedThemeVariant = original;
        }
        AssertNoBindingErrors();
        window.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }
}
