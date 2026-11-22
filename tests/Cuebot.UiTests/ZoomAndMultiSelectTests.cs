using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cuebot.App;
using Cuebot.App.Shell;
using Cuebot.App.ViewModels;
using Cuebot.App.ViewModels.Actions;
using Cuebot.App.ViewModels.Chat;
using Cuebot.App.Views;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;

namespace Cuebot.UiTests;

/// <summary>
/// Interface zoom, Compact density and chat text size, and multi-select with bulk operations in the action editor,
/// driven through the real window with real pointer and key input. Screenshots go to tests/Cuebot.UiTests/screenshots.
/// </summary>
public sealed class ZoomAndMultiSelectTests : IAsyncDisposable
{
    private static readonly string ScreenshotDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshots"));

    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-zoom-").FullName;
    private readonly AppHost _host;

    public ZoomAndMultiSelectTests()
    {
        _host = AppHost.Create(new AppPaths(_dataDir));
        Directory.CreateDirectory(ScreenshotDir);
        UiZoom.Current = UiZoom.Default;
        Profile.Profile.Actions.Clear();   // a new profile starts with example actions
        Profile.NotifyChanged();
    }

    public async ValueTask DisposeAsync()
    {
        UiZoom.Current = UiZoom.Default;
        await _host.DisposeAsync();
        try { Directory.Delete(_dataDir, recursive: true); } catch (IOException) { }
    }

    private ProfileService Profile => _host.Get<ProfileService>();

    private (Window Window, MainWindowViewModel ViewModel) Open(double width = 1360, double height = 860)
    {
        var vm = new MainWindowViewModel(_host);
        var window = new MainWindow { DataContext = vm, Width = width, Height = height };
        window.Show();
        Pump();
        return (window, vm);
    }

    private static void Pump()
    {
        for (var i = 0; i < 3; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
        Dispatcher.UIThread.RunJobs();
    }

    private static T Named<T>(Visual root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static T Page<T>(MainWindowViewModel vm) where T : PageViewModel => vm.Pages.OfType<T>().Single();

    private static void Click(Window window, Control control, RawInputModifiers modifiers = RawInputModifiers.None, MouseButton button = MouseButton.Left)
    {
        var at = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
                 ?? throw new InvalidOperationException("The control is not in the window.");
        window.MouseMove(at, modifiers);
        window.MouseDown(at, button, modifiers);
        window.MouseUp(at, button, modifiers);
        Pump();
    }

    private static void Key_(Window window, Key key, RawInputModifiers modifiers = RawInputModifiers.None, PhysicalKey physical = PhysicalKey.None)
    {
        window.KeyPress(key, modifiers, physical, null);
        window.KeyRelease(key, modifiers, physical, null);
        Pump();
    }

    private void AddActions(params string[] names)
    {
        foreach (var name in names) Profile.Profile.Actions.Add(new ActionDefinition { Name = name, Group = "Start" });
        Profile.NotifyChanged();
    }

    private (Window Window, MainWindowViewModel Vm, ActionsViewModel Actions, ListBox List) OpenActions(params string[] names)
    {
        AddActions(names);
        var (window, vm) = Open();
        var actions = Page<ActionsViewModel>(vm);
        vm.SelectedPage = actions;
        Pump();
        return (window, vm, actions, Named<ListBox>(window, "ActionList"));
    }

    private static Control Row(ListBox list, ActionsViewModel actions, string name) =>
        list.ContainerFromItem(actions.Actions.Single(a => a.Name == name))!;

    /// <summary>An action with five Comment steps named s0..s4, opened in the editor.</summary>
    private (Window Window, ActionsViewModel Actions, ActionItemViewModel Item) OpenSteps()
    {
        var definition = new ActionDefinition { Name = "Steps" };
        for (var i = 0; i < 5; i++)
            definition.SubActions.Add(new SubActionDefinition { Type = "core.comment", Parameters = { ["text"] = $"s{i}" } });
        Profile.Profile.Actions.Add(definition);
        Profile.NotifyChanged();
        var (window, vm) = Open();
        var actions = Page<ActionsViewModel>(vm);
        vm.SelectedPage = actions;
        var item = actions.SelectedAction = actions.Actions.Single(a => a.Name == "Steps");
        Pump();
        return (window, actions, item);
    }

    private static Button StepRow(Window window, SubActionItemViewModel step) =>
        window.GetVisualDescendants().OfType<Button>().First(b => b.DataContext == step && b.Classes.Contains("row"));

    private static string[] Texts(StepListViewModel list) => list.Items.Select(s => s.Definition.Parameters["text"]).ToArray();

    // ---- Zoom ----

    [Theory]
    [InlineData(double.NaN, 1.0)]
    [InlineData(double.PositiveInfinity, 1.0)]
    [InlineData(0.1, 0.8)]
    [InlineData(9.0, 2.0)]
    [InlineData(1.26, 1.25)]
    public void Stored_zoom_values_are_made_usable(double stored, double expected) =>
        Assert.Equal(expected, UiZoom.Normalize(stored), 3);

    [Fact]
    public void Zoom_steps_stop_at_the_limits_and_land_on_tens()
    {
        Assert.Equal(1.3, UiZoom.In(1.25), 3);
        Assert.Equal(1.2, UiZoom.Out(1.25), 3);
        Assert.Equal(1.1, UiZoom.In(1.0), 3);
        Assert.Equal(0.9, UiZoom.Out(1.0), 3);
        Assert.Equal(UiZoom.Max, UiZoom.In(UiZoom.Max), 3);
        Assert.Equal(UiZoom.Min, UiZoom.Out(UiZoom.Min), 3);
    }

    [AvaloniaFact]
    public void Zoom_keys_scale_the_window_and_the_choice_survives_a_restart()
    {
        var (window, vm) = Open();
        var layer = Named<LayoutTransformControl>(window, "ZoomLayer");

        Key_(window, Key.OemPlus, RawInputModifiers.Control, PhysicalKey.Equal);
        Assert.Equal(1.1, vm.UiScale, 3);
        Assert.Equal(1.1, ((ScaleTransform)layer.LayoutTransform!).ScaleX, 3);
        Key_(window, Key.OemMinus, RawInputModifiers.Control, PhysicalKey.Minus);
        Key_(window, Key.OemMinus, RawInputModifiers.Control, PhysicalKey.Minus);
        Assert.Equal(0.9, vm.UiScale, 3);
        Key_(window, Key.D0, RawInputModifiers.Control, PhysicalKey.Digit0);
        Assert.Equal(1.0, vm.UiScale, 3);

        vm.SetUiScale(1.5);
        Pump();
        Assert.Equal(1.5, new AppSettingsStore(new AppPaths(_dataDir)).Settings.General.UiScale, 3);
        var settings = Page<SettingsViewModel>(vm);
        Assert.Equal("150%", settings.ZoomLabel);   // the Settings page follows the keys
        window.Close();
    }

    [AvaloniaFact]
    public void A_broken_zoom_in_app_json_is_repaired_on_load()
    {
        var store = _host.Settings;
        File.WriteAllText(store.Path, """{"General":{"UiScale":47}}""");
        Assert.Equal(UiZoom.Max, new AppSettingsStore(new AppPaths(_dataDir)).Settings.General.UiScale, 3);
    }

    [AvaloniaFact]
    public void Every_page_stays_inside_the_shell_at_the_zoom_extremes()
    {
        var (window, vm) = Open(960, 600);
        var viewport = Named<ScrollViewer>(window, "ZoomViewport");
        var shell = Named<Grid>(window, "ShellRoot");

        foreach (var scale in new[] { UiZoom.Min, UiZoom.Max })
        {
            vm.SetUiScale(scale);
            Pump();
            Assert.True(shell.Bounds.Width >= UiZoom.MinContentWidth - 0.5);
            Assert.True(shell.Bounds.Height >= UiZoom.MinContentHeight - 0.5);
            foreach (var page in vm.Pages)
            {
                vm.SelectedPage = page;
                Pump();
                var host = Named<ContentControl>(window, "PageHost");
                Assert.True(host.Bounds.Width > 0 && host.Bounds.Height > 0, $"{page.Title} has no room at {scale}");
                Assert.True(host.Bounds.Right <= shell.Bounds.Width + 1, $"{page.Title} leaves the shell at {scale}");
            }
        }

        // 200% in the smallest window: the shell keeps its minimum and the window scrolls to reach it.
        vm.SetUiScale(UiZoom.Max);
        vm.SelectedPage = Page<ActionsViewModel>(vm);
        Pump();
        Assert.Equal(ScrollBarVisibility.Auto, viewport.HorizontalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Auto, viewport.VerticalScrollBarVisibility);
        TestApp.SaveFrame(window, Path.Combine(ScreenshotDir, "zoom-200-small-window.png"));

        // At 100% nothing scrolls.
        vm.SetUiScale(UiZoom.Default);
        Pump();
        Assert.Equal(ScrollBarVisibility.Disabled, viewport.HorizontalScrollBarVisibility);
        Assert.Equal(ScrollBarVisibility.Disabled, viewport.VerticalScrollBarVisibility);
        window.Close();
    }

    [AvaloniaFact]
    public void A_dropdown_inherits_the_window_zoom()
    {
        var (window, vm) = Open();
        vm.SetUiScale(1.5);
        vm.SelectedPage = Page<SettingsViewModel>(vm);
        Pump();
        var picker = Named<ComboBox>(window, "ThemePicker");
        picker.IsDropDownOpen = true;
        Pump();
        var popup = picker.GetVisualDescendants().OfType<Popup>().First();
        Assert.True(popup.InheritsTransform);
        Assert.True(popup.IsOpen);
        picker.IsDropDownOpen = false;
        window.Close();
    }

    [AvaloniaFact]
    public void Compact_density_tightens_rows_without_changing_text_size()
    {
        var (window, actions, item) = OpenSteps();
        var row = StepRow(window, item.Steps.Items[0]);
        var normal = row.Bounds.Height;
        var font = row.FontSize;

        var settings = Page<SettingsViewModel>((MainWindowViewModel)window.DataContext!);
        settings.CompactDensity = true;
        Pump();
        Assert.Contains("compact", window.Classes);
        Assert.True(row.Bounds.Height < normal, $"{row.Bounds.Height} is not below {normal}");
        Assert.Equal(font, row.FontSize);
        Assert.True(new AppSettingsStore(new AppPaths(_dataDir)).Settings.General.CompactDensity);
        TestApp.SaveFrame(window, Path.Combine(ScreenshotDir, "density-compact.png"));
        window.Close();
    }

    [AvaloniaFact]
    public void Chat_text_size_changes_the_chat_list_and_is_clamped()
    {
        var (window, vm) = Open();
        var chat = Page<ChatViewModel>(vm);
        vm.SelectedPage = chat;
        Pump();
        var list = Named<ListBox>(window, "ChatList");
        Assert.Equal(ChatPageSettings.DefaultTextSize, list.FontSize);

        chat.Options.ChatTextSize = 20;
        Pump();
        Assert.Equal(20, list.FontSize);
        chat.Options.ChatTextSize = 500;
        Pump();
        Assert.Equal(ChatPageSettings.MaxTextSize, list.FontSize);

        // The size is saved (after the file's short delay, which must also finish before the host goes away).
        var saved = false;
        for (var i = 0; i < 100 && !saved; i++)
        {
            Thread.Sleep(50);
            saved = Directory.GetFiles(_dataDir, "chat-page.json", SearchOption.AllDirectories)
                .Any(f => File.ReadAllText(f).Contains("\"textSize\": 28", StringComparison.OrdinalIgnoreCase));
        }
        Assert.True(saved);

        chat.Options.ChatTextSize = null;   // the number box while it is being retyped
        Pump();
        Assert.Equal(ChatPageSettings.DefaultTextSize, list.FontSize);
        window.Close();
    }

    // ---- Several actions ----

    [AvaloniaFact]
    public void Ctrl_and_shift_click_select_actions_and_open_the_bulk_panel()
    {
        var (window, _, actions, list) = OpenActions("A1", "A2", "A3", "A4");
        var panel = Named<Border>(window, "BulkPanel");

        Click(window, Row(list, actions, "A1"));
        Assert.Single(actions.SelectedActions);
        Assert.False(panel.IsVisible);

        Click(window, Row(list, actions, "A3"), RawInputModifiers.Control);
        Assert.Equal(["A1", "A3"], actions.SelectedActions.Select(a => a.Name));
        Assert.True(panel.IsVisible);
        Assert.False(actions.ShowsEditor);
        Assert.Equal("2 actions selected", actions.BulkTitle);

        Click(window, Row(list, actions, "A4"), RawInputModifiers.Shift);
        Assert.True(actions.SelectedActions.Count >= 2);
        Assert.Contains(actions.SelectedActions, a => a.Name == "A4");

        // Right-clicking inside the selection keeps it for the menu; a plain click goes back to one action.
        var before = actions.SelectedActions.Count;
        Click(window, Row(list, actions, "A4"), button: MouseButton.Right);
        Assert.Equal(before, actions.SelectedActions.Count);
        Key_(window, Key.Escape);
        Click(window, Row(list, actions, "A2"));
        Assert.Equal("A2", Assert.Single(actions.SelectedActions).Name);
        Assert.True(actions.ShowsEditor);
        Assert.False(panel.IsVisible);
        window.Close();
    }

    [AvaloniaFact]
    public void Bulk_on_off_and_move_to_group_are_one_undo_step()
    {
        var (window, _, actions, list) = OpenActions("A1", "A2", "A3");
        Click(window, Row(list, actions, "A1"));
        Click(window, Row(list, actions, "A2"), RawInputModifiers.Control);
        Click(window, Row(list, actions, "A3"), RawInputModifiers.Control);
        TestApp.SaveFrame(window, Path.Combine(ScreenshotDir, "bulk-actions-panel.png"));

        actions.BulkTurnOffCommand.Execute(null);
        Pump();
        Assert.All(actions.SelectedActions, a => Assert.False(a.Enabled));
        Assert.Equal(3, actions.SelectedActions.Count);

        actions.BulkGroup = "Moved";
        actions.BulkMoveToGroupCommand.Execute(null);
        Pump();
        Assert.All(Profile.Profile.Actions, a => Assert.Equal("Moved", a.Group));
        Assert.Equal(3, actions.SelectedActions.Count);

        actions.UndoCommand.Execute(null);
        Pump();
        Assert.All(Profile.Profile.Actions, a => Assert.Equal("Start", a.Group));
        Assert.All(Profile.Profile.Actions, a => Assert.False(a.Enabled));
        Assert.Equal(3, actions.SelectedActions.Count);

        actions.UndoCommand.Execute(null);
        Pump();
        Assert.All(Profile.Profile.Actions, a => Assert.True(a.Enabled));

        actions.RedoCommand.Execute(null);
        Pump();
        Assert.All(Profile.Profile.Actions, a => Assert.False(a.Enabled));
        window.Close();
    }

    [AvaloniaFact]
    public void Bulk_undo_leaves_an_action_edited_since_alone()
    {
        var (window, _, actions, _) = OpenActions("A1", "A2");
        var a1 = actions.Actions.Single(a => a.Name == "A1");
        var a2 = actions.Actions.Single(a => a.Name == "A2");
        actions.SelectedAction = a1;
        actions.SetSelectedActions([a1, a2]);
        actions.BulkTurnOffCommand.Execute(null);
        Pump();

        actions.SetSelectedActions([]);
        actions.SelectedAction = actions.Actions.Single(a => a.Name == "A2");
        actions.SelectedAction!.Name = "A2 renamed";
        Pump();

        var again = actions.Actions.Where(a => a.Name is "A1" or "A2 renamed").ToArray();
        actions.SelectedAction = again[0];
        actions.SetSelectedActions(again);
        actions.UndoCommand.Execute(null);
        Pump();
        Assert.True(Profile.Profile.Actions.Single(a => a.Name == "A1").Enabled);
        Assert.False(Profile.Profile.Actions.Single(a => a.Name == "A2 renamed").Enabled);
        Assert.Contains("changed since", actions.EditStatus);
        window.Close();
    }

    [AvaloniaFact]
    public void Bulk_delete_asks_twice_and_undo_delete_brings_all_back_in_place()
    {
        var (window, _, actions, list) = OpenActions("A1", "A2", "A3", "A4");
        var order = Profile.Profile.Actions.Select(a => a.Name).ToArray();
        Click(window, Row(list, actions, "A2"));
        Click(window, Row(list, actions, "A3"), RawInputModifiers.Control);

        Row(list, actions, "A3").Focus();
        Key_(window, Key.Delete);
        Assert.Equal(4, Profile.Profile.Actions.Count);
        Assert.Contains("delete 2 actions", actions.EditStatus);
        Key_(window, Key.Delete);
        Assert.Equal(["A1", "A4"], Profile.Profile.Actions.Select(a => a.Name));
        Assert.Equal("Deleted 2 actions.", actions.DeletedNotice);

        actions.RestoreDeletedCommand.Execute(null);
        Pump();
        Assert.Equal(order, Profile.Profile.Actions.Select(a => a.Name));
        Assert.Equal(["A2", "A3"], actions.SelectedActions.Select(a => a.Name));
        Assert.Null(actions.DeletedNotice);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Bulk_duplicate_and_copy_paste_make_new_actions_and_select_them()
    {
        var (window, _, actions, list) = OpenActions("A1", "A2", "A3");
        Click(window, Row(list, actions, "A1"));
        Click(window, Row(list, actions, "A2"), RawInputModifiers.Control);

        actions.DuplicateActionCommand.Execute(null);
        Pump();
        Assert.Equal(5, Profile.Profile.Actions.Count);
        Assert.Equal(["A1 (copy)", "A2 (copy)"], actions.SelectedActions.Select(a => a.Name));
        Assert.Equal(5, Profile.Profile.Actions.Select(a => a.Id).Distinct().Count());

        var clip = await actions.CopySelectionAsync(wholeAction: true);
        Assert.Equal(2, clip!.Count);
        Assert.True(actions.PasteClip(clip));
        Pump();
        Assert.Equal(7, Profile.Profile.Actions.Count);
        Assert.Equal(2, actions.SelectedActions.Count);

        // Sub-actions need one target action.
        var steps = new EditorClip { Kind = ClipKind.SubActions, SubActions = [new SubActionDefinition { Type = "core.comment" }] };
        Assert.False(actions.PasteClip(steps));
        window.Close();
    }

    // ---- Several sub-actions ----

    [AvaloniaFact]
    public void Ctrl_and_shift_click_select_steps_of_one_list()
    {
        var (window, actions, item) = OpenSteps();
        var steps = item.Steps.Items;

        Click(window, StepRow(window, steps[1]));
        Click(window, StepRow(window, steps[3]), RawInputModifiers.Shift);
        Assert.Equal(["s1", "s2", "s3"], item.SelectedSteps.Select(s => s.Definition.Parameters["text"]));
        Assert.True(item.HasStepBlock);
        Assert.Null(item.SelectedStep);   // the details pane gives way to the block tools
        Assert.True(Named<Border>(window, "StepBlockBar").IsVisible);
        TestApp.SaveFrame(window, Path.Combine(ScreenshotDir, "bulk-steps.png"));

        Click(window, StepRow(window, steps[2]), RawInputModifiers.Control);
        Assert.Equal(["s1", "s3"], item.SelectedSteps.Select(s => s.Definition.Parameters["text"]));
        Assert.False(steps[2].IsSelected);

        Key_(window, Key.Escape);
        Assert.Single(item.SelectedSteps);

        StepRow(window, steps[0]).Focus();
        Key_(window, Key.A, RawInputModifiers.Control, PhysicalKey.A);
        Assert.Equal(5, item.SelectedSteps.Count);

        Click(window, StepRow(window, steps[4]));
        Assert.Single(item.SelectedSteps);
        window.Close();
    }

    [AvaloniaFact]
    public void A_step_from_another_list_starts_a_new_selection()
    {
        var (window, _, item) = OpenSteps();
        item.Selected = item.Steps.Items[0];
        item.SelectAllSteps();
        var group = item.WrapSelectionInGroup()!;
        var inside = group.Branches.Single().Steps.Items;
        var outside = item.Steps.Insert(1, new SubActionDefinition { Type = "core.comment", Parameters = { ["text"] = "out" } });
        Pump();

        item.Selected = inside[0];
        item.ToggleStepSelection(inside[1]);
        Assert.Equal(2, item.SelectedSteps.Count);
        item.ToggleStepSelection(outside);
        Assert.Same(outside, Assert.Single(item.SelectedSteps));
        window.Close();
    }

    [AvaloniaFact]
    public void Block_delete_move_toggle_and_duplicate_are_each_one_undo_step()
    {
        var (window, actions, item) = OpenSteps();
        StepSelect(item, 1, 3);

        // Delete.
        actions.DeleteItemCommand.Execute(null);
        Pump();
        item = actions.SelectedAction!;
        Assert.Equal(["s0", "s2", "s4"], Texts(item.Steps));
        actions.UndoCommand.Execute(null);
        Pump();
        item = actions.SelectedAction!;
        Assert.Equal(["s0", "s1", "s2", "s3", "s4"], Texts(item.Steps));

        // Move down as a block, keeping the gap; the bottom edge holds it.
        StepSelect(item, 1, 3);
        actions.MoveDownCommand.Execute(null);
        Assert.Equal(["s0", "s2", "s1", "s4", "s3"], Texts(item.Steps));
        actions.MoveDownCommand.Execute(null);
        Assert.Equal(["s0", "s2", "s4", "s1", "s3"], Texts(item.Steps));
        actions.MoveDownCommand.Execute(null);
        Assert.Equal(["s0", "s2", "s4", "s1", "s3"], Texts(item.Steps));
        actions.MoveToTopCommand.Execute(null);
        Assert.Equal(["s1", "s3", "s0", "s2", "s4"], Texts(item.Steps));
        actions.UndoCommand.Execute(null);
        Pump();
        item = actions.SelectedAction!;
        Assert.Equal(["s0", "s2", "s4", "s1", "s3"], Texts(item.Steps));
        Assert.Equal(2, item.SelectedSteps.Count);   // undo keeps the block selected

        // On/off: a mixed block turns on first.
        item.SelectedSteps[0].Enabled = false;
        actions.ToggleEnabled(wholeAction: false);
        Assert.All(item.SelectedSteps, s => Assert.True(s.Enabled));
        actions.ToggleEnabled(wholeAction: false);
        Assert.All(item.SelectedSteps, s => Assert.False(s.Enabled));
        actions.UndoCommand.Execute(null);
        Pump();
        item = actions.SelectedAction!;
        Assert.All(item.SelectedSteps, s => Assert.True(s.Enabled));

        // Duplicate: the copies follow the last selected step and become the selection.
        var count = item.Steps.Items.Count;
        actions.DuplicateItemCommand.Execute(null);
        Pump();
        Assert.Equal(count + 2, item.Steps.Items.Count);
        Assert.Equal(2, item.SelectedSteps.Count);
        Assert.DoesNotContain(item.SelectedSteps, s => s.Definition.Id == item.Steps.Items[0].Definition.Id);
        Assert.Equal(item.Steps.Items.Count, item.Definition.SubActions.Select(s => s.Id).Distinct().Count());
        actions.UndoCommand.Execute(null);
        Pump();
        Assert.Equal(count, actions.SelectedAction!.Steps.Items.Count);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Copied_steps_paste_in_order_after_the_selection_into_another_action()
    {
        var (window, actions, item) = OpenSteps();
        StepSelect(item, 0, 2);
        var clip = await actions.CopySelectionAsync(wholeAction: false);
        Assert.Equal(ClipKind.SubActions, clip!.Kind);
        Assert.Equal(["s0", "s2"], clip.SubActions.Select(s => s.Parameters["text"]));

        Profile.Profile.Actions.Add(new ActionDefinition { Name = "Other" });
        Profile.NotifyChanged();
        Pump();
        var other = actions.SelectedAction = actions.Actions.Single(a => a.Name == "Other");
        Assert.True(actions.PasteClip(clip));
        Pump();
        Assert.Equal(["s0", "s2"], Texts(other.Steps));
        Assert.DoesNotContain(other.Definition.SubActions, s => item.Definition.SubActions.Any(o => o.Id == s.Id));

        // Pasting onto a block goes after its last step.
        actions.SelectedAction = item;
        Pump();
        StepSelect(item, 1, 3);
        actions.PasteClip(clip);
        Assert.Equal(["s0", "s1", "s2", "s3", "s0", "s2", "s4"], Texts(item.Steps));
        window.Close();
    }

    [AvaloniaFact]
    public void Ctrl_G_wraps_the_block_in_a_group_in_place_and_undo_unwraps_it()
    {
        var (window, actions, item) = OpenSteps();
        Click(window, StepRow(window, item.Steps.Items[1]));
        Click(window, StepRow(window, item.Steps.Items[3]), RawInputModifiers.Shift);

        Key_(window, Key.G, RawInputModifiers.Control, PhysicalKey.G);
        Assert.Equal(3, item.Steps.Items.Count);
        var group = item.Steps.Items[1];
        Assert.Equal(GroupSubAction.Id, group.Definition.Type);
        Assert.Equal(ActionItemViewModel.NewGroupName, group.GroupName);
        Assert.Equal(["s1", "s2", "s3"], Texts(group.Branches.Single().Steps));
        Assert.Same(group, item.Selected);

        actions.UndoCommand.Execute(null);
        Pump();
        Assert.Equal(["s0", "s1", "s2", "s3", "s4"], Texts(actions.SelectedAction!.Steps));
        window.Close();
    }

    [AvaloniaFact]
    public void The_flowchart_never_acts_on_a_block_it_does_not_show()
    {
        var (window, actions, item) = OpenSteps();
        StepSelect(item, 1, 3);
        actions.IsGraphView = true;
        Pump();
        Assert.Single(item.SelectedSteps);
        actions.DeleteItemCommand.Execute(null);
        Assert.Equal(4, item.Steps.Items.Count);
        window.Close();
    }

    [AvaloniaFact]
    public void With_several_actions_selected_the_hidden_editor_does_not_take_step_commands()
    {
        var (window, _, actions, list) = OpenActions("A1", "A2");
        var a1 = actions.Actions.Single(a => a.Name == "A1");
        a1.Steps.Insert(0, new SubActionDefinition { Type = "core.comment" });
        a1.Selected = a1.Steps.Items[0];
        Click(window, Row(list, actions, "A1"));
        Click(window, Row(list, actions, "A2"), RawInputModifiers.Control);
        Assert.True(actions.IsBulkSelection);

        actions.DeleteItemCommand.Execute(null);
        Assert.Single(a1.Definition.SubActions);
        window.Close();
    }

    private static void StepSelect(ActionItemViewModel item, params int[] indexes)
    {
        item.Selected = item.Steps.Items[indexes[0]];
        foreach (var i in indexes.Skip(1)) item.ToggleStepSelection(item.Steps.Items[i]);
    }
}
