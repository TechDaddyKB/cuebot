using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Cuebot.App;
using Cuebot.App.Graph;
using Cuebot.App.Shell;
using Cuebot.App.ViewModels;
using Cuebot.App.ViewModels.Actions;
using Cuebot.App.Views;
using Cuebot.Core;
using Cuebot.Core.Actions;
using Cuebot.Core.BuiltIn;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.SystemTools;
using Cuebot.Core.Users;
using Cuebot.Core.Variables;

namespace Cuebot.UiTests;

/// <summary>
/// Wave-2 pages (Commands, Queues, Timers, Users &amp; Groups, Variables, Watchers, Actions options, Settings audio and
/// text): editors write through to the backend stores, and populated pages render without binding errors in both
/// themes (screenshots in tests/Cuebot.UiTests/screenshots).
/// </summary>
public sealed class Wave2PagesTests : IAsyncDisposable
{
    private static readonly string ScreenshotDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshots"));

    private readonly string _dataDir = Directory.CreateTempSubdirectory("cuebot-ui-wave2-").FullName;
    private readonly AppHost _host;

    public Wave2PagesTests()
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

    /// <summary>Waits (pumping the UI thread) until the condition holds; engine runs finish on the thread pool.</summary>
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

    // ---- Actions ----

    [AvaloniaFact]
    public void Action_options_write_through_and_queue_list_follows_the_profile()
    {
        var (window, vm) = Open();
        var actions = Page<ActionsViewModel>(vm);
        var action = actions.Actions.First();

        action.RandomAction = true;
        action.ConcurrentSubActions = true;
        action.AlwaysRun = true;
        action.ExcludeFromHistory = true;
        Assert.True(action.Definition.RandomAction && action.Definition.ConcurrentSubActions && action.Definition.AlwaysRun
                    && action.Definition.ExcludeFromHistory);
        var snapshot = Profile.Snapshot.FindAction(action.Definition.Id)!;
        Assert.True(snapshot.RandomAction && snapshot.AlwaysRun);

        Assert.Equal(["Default"], action.QueueOptions);
        Assert.False(action.HasQueue);
        Profile.Profile.Queues.Add(new QueueDefinition { Name = "Alerts", Blocking = true });
        Profile.NotifyChanged();
        Pump();
        Assert.Equal(["Default", "Alerts"], action.QueueOptions);

        action.Queue = "Alerts";
        Assert.Equal("Alerts", action.Definition.Queue);
        Assert.True(action.HasQueue);
        Assert.Contains("Queue: Alerts", action.RunSummary);
        action.Queue = null;   // a dropdown clearing its selection changes nothing
        Assert.Equal("Alerts", action.Definition.Queue);
        action.Queue = "Default";
        Assert.Equal("", action.Definition.Queue);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Set_action_state_at_runtime_refreshes_the_action_list()
    {
        var (window, vm) = Open();
        var action = Page<ActionsViewModel>(vm).Actions.First();
        var raised = new List<string?>();
        action.PropertyChanged += (_, e) => raised.Add(e.PropertyName);

        Assert.True(await Profile.SetActionEnabledAsync(action.Name, false));
        Pump();
        Assert.False(action.Enabled);
        Assert.Contains(nameof(ActionItemViewModel.Enabled), raised);
        window.Close();
    }

    [AvaloniaFact]
    public void Weights_show_for_random_actions_and_random_groups()
    {
        var (window, vm) = Open();
        var actions = Page<ActionsViewModel>(vm);
        vm.SelectedPage = actions;
        var action = actions.SelectedAction = actions.Actions.Single(a => a.Name == "Thank new followers");
        var first = action.Steps.Items[0];
        Assert.False(first.ShowsWeight);

        action.RandomAction = true;
        Assert.True(first.ShowsWeight);
        first.Weight = 3;
        Assert.Equal(3, first.Definition.Weight);
        Assert.Equal("Weight 3 · 75%", first.WeightLabel);   // 3 of 3 + 1
        Assert.Equal(3, Profile.Snapshot.FindAction(action.Definition.Id)!.SubActions[0].Weight);
        action.RandomAction = false;
        Assert.False(first.ShowsWeight);

        // A random Group: its children show weights, and it renders as a tinted, named container.
        var group = action.AddStep(action.Registry.GetSubAction(GroupSubAction.Id)!, new StepInsertTarget(action.Steps, action.Steps.Items.Count));
        group.Parameters.OfType<TextParameterViewModel>().Single(p => p.Descriptor.Key == "name").Value = "Shout-outs";
        group.Parameters.OfType<TextParameterViewModel>().Single(p => p.Descriptor.Key == "color").Value = "#E5534B";
        group.Parameters.OfType<BoolParameterViewModel>().Single(p => p.Descriptor.Key == "random").IsChecked = true;
        Assert.True(group.IsGroup && group.IsRandomGroup);
        Assert.Equal("Shout-outs", group.Title);
        Assert.Equal(Avalonia.Media.Color.Parse("#E5534B"), group.GroupColor);
        var inner = action.AddStep(action.Registry.GetSubAction("core.comment")!, new StepInsertTarget(group.Branches[0].Steps, 0));
        action.AddStep(action.Registry.GetSubAction("core.comment")!, new StepInsertTarget(group.Branches[0].Steps, 1));
        Assert.True(inner.ShowsWeight);
        Assert.Equal("Weight 1 · 50%", inner.WeightLabel);

        var graph = FlowGraphLayout.Build(action);
        var frame = Assert.Single(graph.Frames);
        Assert.Equal("Shout-outs", frame.Label);
        var groupNode = graph.Nodes.Single(n => n.Item == group);
        Assert.Equal(group.GroupColor, groupNode.Tint);
        Assert.True(frame.Bounds.Contains(groupNode.Bounds));
        Assert.All(graph.Nodes.Where(n => n.Item is SubActionItemViewModel s && s.Parent == group.Branches[0].Steps),
            n => Assert.True(frame.Bounds.Contains(n.Bounds)));

        TestApp.BindingErrors.Clear();
        action.Selected = inner;
        Save(window, "actions-group-weights");
        actions.IsGraphView = true;
        Save(window, "actions-group-graph");
        actions.IsGraphView = false;
        AssertNoBindingErrors();
        window.Close();
    }

    [Fact]
    public void Test_trigger_arguments_parse_and_auto_type()
    {
        var args = TestTriggerViewModel.ParseArguments("user=Bob\n# comment\n\nbits = 500\nmessage=a=b", out var error);
        Assert.Null(error);
        Assert.Equal("Bob", args["user"]);
        Assert.Equal(500L, args["bits"]);
        Assert.Equal("a=b", args["message"]);
        TestTriggerViewModel.ParseArguments("oops", out error);
        Assert.NotNull(error);
    }

    [AvaloniaFact]
    public async Task Test_trigger_runs_the_action_with_the_arguments()
    {
        var definition = new ActionDefinition
        {
            Name = "Echo",
            Triggers = [new TriggerDefinition { Type = "core.timer" }],
            SubActions = [new SubActionDefinition { Type = "core.setArgument", Parameters = { ["name"] = "echo", ["value"] = "%user%" } }],
        };
        Profile.Profile.Actions.Add(definition);
        Profile.NotifyChanged();
        var (window, vm) = Open();
        var actions = Page<ActionsViewModel>(vm);
        vm.SelectedPage = actions;
        actions.SelectedAction = actions.Actions.Single(a => a.Name == "Echo");

        actions.TestTriggerCommand.Execute(null);
        Assert.True(actions.Test.IsOpen);
        Assert.Equal(2, actions.Test.Options.Count);   // the trigger + custom arguments
        actions.Test.Arguments = "user=Alice";
        TestApp.BindingErrors.Clear();
        Save(window, "actions-test-trigger");
        AssertNoBindingErrors();

        await actions.Test.RunCommand.ExecuteAsync(null);
        Assert.False(actions.Test.IsOpen);
        await Until(() => Engine.History.Entries.Any(e => e.ActionName == "Echo" && !e.IsActive));
        var entry = Engine.History.Entries.First(e => e.ActionName == "Echo");
        Assert.Equal(ActionRunState.Completed, entry.State);
        Assert.Equal("Alice", entry.ArgsAfterRun!["echo"]);
        Assert.Equal(true, entry.ArgsWhenQueued["isTest"]);
        Assert.StartsWith("Test:", entry.Source);
        window.Close();
    }

    // ---- Commands ----

    [AvaloniaFact]
    public void Editing_a_command_updates_the_command_store()
    {
        var store = _host.Get<CommandStore>();
        var groups = _host.Get<GroupStore>();
        groups.Add("Regulars");
        var (window, vm) = Open();
        var page = Page<CommandsViewModel>(vm);
        vm.SelectedPage = page;

        page.NewCommandCommand.Execute(null);
        var editor = page.Editor!;
        editor.Name = "Lurk";
        editor.Group = "Fun";
        editor.AliasesText = "!lurk\n  !afk \n";
        editor.SourcesText = "Twitch\nTwitch:Whisper";
        editor.GlobalCooldownSeconds = 30;
        editor.UserCooldownSeconds = 5.5m;
        editor.MinimumRole = ChatPermission.Vip;
        editor.AllowedUsersText = "alice\nbob";
        editor.DeniedUsersText = "mallory";
        editor.CaseSensitive = true;
        editor.IgnoreInternal = false;
        editor.PersistCounter = true;
        editor.PersistUserCounter = true;
        editor.Include = false;
        editor.Location = CommandLocation.Anywhere;
        editor.AllowedGroups.Single(g => g.Name == "Regulars").IsChecked = true;

        var stored = store.Get(editor.Id)!;
        Assert.Equal("Lurk", stored.Name);
        Assert.Equal("Fun", stored.Group);
        Assert.Equal(["!lurk", "!afk"], stored.Commands);
        Assert.Equal(["Twitch", "Twitch:Whisper"], stored.Sources);
        Assert.Equal(30, stored.GlobalCooldownSeconds);
        Assert.Equal(5.5, stored.UserCooldownSeconds);
        Assert.Equal(ChatPermission.Vip, stored.MinimumRole);
        Assert.Equal(["alice", "bob"], stored.AllowedUsers);
        Assert.Equal(["mallory"], stored.DeniedUsers);
        Assert.Equal(["Regulars"], stored.AllowedGroups);
        Assert.True(stored.CaseSensitive && stored.PersistCounter && stored.PersistUserCounter);
        Assert.False(stored.IgnoreInternal || stored.Include);
        Assert.Equal(CommandLocation.Anywhere, stored.Location);

        // Grouped list with a group toggle.
        var fun = page.Groups.Single(g => g.Name == "Fun");
        Assert.Equal("Lurk", Assert.Single(fun.Commands).Name);
        fun.Enabled = false;
        Assert.False(store.Get(editor.Id)!.Enabled);
        Assert.False(editor.Enabled);   // reloaded from the store

        // Regex validation feedback.
        editor.Mode = CommandMode.Regex;
        Assert.False(editor.HasRegexErrors);
        editor.AliasesText = "^!roll (?<sides>\\d+)$\n(unclosed";
        Assert.True(editor.HasRegexErrors);
        Assert.Contains("(unclosed", editor.RegexErrors);

        // Counters.
        store.Increment(editor.Id, "twitch:1");
        Pump();
        Assert.Equal(1, editor.Counter);
        editor.ResetCounterCommand.Execute(null);
        Pump();
        Assert.Equal(0, editor.Counter);

        // Duplicate and delete.
        page.DuplicateCommandCommand.Execute(null);
        Assert.Equal(2, store.Snapshot.Count);
        Assert.Equal("Lurk (copy)", page.Editor!.Name);
        page.DeleteCommandCommand.Execute(null);
        Assert.Single(store.Snapshot);
        Assert.Null(page.Editor);

        page.SelectCommand.Execute(page.Groups.SelectMany(g => g.Commands).Single());
        TestApp.BindingErrors.Clear();
        Save(window, "commands-editor");
        AssertNoBindingErrors();
        window.Close();
    }

    // ---- Queues ----

    [AvaloniaFact]
    public void Adding_renaming_and_deleting_a_queue_updates_the_profile()
    {
        var (window, vm) = Open();
        var page = Page<QueuesViewModel>(vm);
        var action = Profile.Profile.Actions[0];

        page.AddQueueCommand.Execute(null);
        var queue = Assert.Single(Profile.Profile.Queues);
        Assert.Equal("New queue", queue.Name);
        Assert.Equal("New queue", Assert.Single(Profile.Snapshot.Queues).Name);
        var row = page.Queues.Single(q => q.Definition == queue);

        row.Blocking = true;
        Assert.True(Profile.Snapshot.Queues[0].Blocking);
        Assert.True(Engine.Queues.IsBlocking("New queue"));

        action.Queue = "New queue";
        Profile.NotifyChanged();
        row.NameText = "Alerts";
        Assert.Equal("Alerts", queue.Name);
        Assert.Equal("Alerts", action.Queue);   // actions follow the rename
        row.NameText = "Default";
        Assert.NotEmpty(row.NameError);
        Assert.Equal("Alerts", queue.Name);
        row.NameText = "Alerts";
        Assert.Empty(row.NameError);

        page.PauseCommand.Execute(row);
        Pump();
        Assert.True(row.Paused);
        Assert.True(Engine.Queues.IsPaused("Alerts"));
        page.ResumeAllCommand.Execute(null);
        Pump();
        Assert.False(row.Paused);

        page.DeleteQueueCommand.Execute(row);
        Assert.Empty(Profile.Profile.Queues);
        Assert.Equal("", action.Queue);   // moved back to Default
        Assert.DoesNotContain(page.Queues, q => q.IsEditable);   // Default (and the runtime-only paused name) stay
        window.Close();
    }

    [AvaloniaFact]
    public async Task Pending_and_history_lists_follow_the_engine()
    {
        var quick = new ActionDefinition
        {
            Name = "Quick",
            SubActions = [new SubActionDefinition { Type = "core.setArgument", Parameters = { ["name"] = "done", ["value"] = "yes" } }],
        };
        var held = new ActionDefinition { Name = "Held", Queue = "Hold", SubActions = [new SubActionDefinition { Type = "core.comment" }] };
        Profile.Profile.Actions.AddRange([quick, held]);
        Profile.Profile.Queues.Add(new QueueDefinition { Name = "Hold" });
        Profile.NotifyChanged();
        var (window, vm) = Open();
        var page = Page<QueuesViewModel>(vm);
        vm.SelectedPage = page;

        Assert.True(await Engine.RunAsync(Profile.Snapshot.FindAction("Quick")!, new Dictionary<string, object?> { ["user"] = "Ann" }));
        await Until(() => page.History.Any(r => r.ActionName == "Quick"));
        var run = page.History.First(r => r.ActionName == "Quick");
        Assert.True(run.IsDone);
        page.SelectedHistory = run;
        Assert.Same(run, page.SelectedRun);
        Assert.Contains(page.ArgsWhenQueued, a => a is { Name: "user", Value: "Ann" });
        Assert.Contains(page.ArgsAfterRun, a => a is { Name: "done", Value: "yes" });

        // A paused queue keeps its run pending until cancelled.
        Engine.Queues.Pause("Hold");
        var pending = Engine.RunAsync(Profile.Snapshot.FindAction("Held")!);
        await Until(() => page.Pending.Any(r => r.ActionName == "Held"));
        page.SelectedPending = page.Pending.Single();
        Assert.True(page.CanCancelSelected);
        TestApp.BindingErrors.Clear();
        Save(window, "queues-pending");
        vm.Settings.Theme = AppTheme.Light;
        Save(window, "queues-pending-light");
        vm.Settings.Theme = AppTheme.Dark;
        AssertNoBindingErrors();

        page.CancelRunCommand.Execute(null);
        try { await pending; }
        catch (OperationCanceledException) { }
        await Until(() => page.Pending.Count == 0);
        Assert.Contains(page.History, r => r is { ActionName: "Held", StateText: "Cancelled" });

        // Requeue replays the arguments; Clear history keeps nothing finished.
        page.SelectedHistory = page.History.First(r => r.ActionName == "Quick");
        await page.RequeueCommand.ExecuteAsync(null);
        await Until(() => page.History.Count(r => r.ActionName == "Quick") == 2);
        Assert.Equal("Requeue", page.History.First(r => r.ActionName == "Quick").Source);
        page.ClearHistoryCommand.Execute(null);
        await Until(() => page.History.Count == 0);
        Assert.Null(page.SelectedRun);
        window.Close();
    }

    // ---- Timers ----

    [AvaloniaFact]
    public void Timer_fields_write_through_and_cron_is_validated()
    {
        var (window, vm) = Open();
        var page = Page<TimersViewModel>(vm);
        page.AddTimerCommand.Execute(null);
        var timer = page.Timers.Single();

        timer.Repeat = false;
        timer.RandomInterval = true;
        timer.MinIntervalSeconds = 120;
        timer.MaxIntervalSeconds = 60;
        Assert.NotEmpty(timer.IntervalError);
        timer.MaxIntervalSeconds = 600;
        Assert.Empty(timer.IntervalError);
        timer.MinChatLines = 5;
        Assert.True(timer.ShowsRandomInterval);

        var stored = Profile.Snapshot.Timers.Single();
        Assert.False(stored.Repeat);
        Assert.True(stored.RandomInterval);
        Assert.Equal(120, stored.MinIntervalSeconds);
        Assert.Equal(600, stored.MaxIntervalSeconds);
        Assert.Equal(5, stored.MinChatLines);

        timer.Cron = "*/15 * * *";
        Assert.True(timer.HasCronError);
        timer.Cron = "*/15 * * * *";
        Assert.False(timer.HasCronError);
        Assert.StartsWith("Next:", timer.CronNext);
        Assert.Equal("*/15 * * * *", Profile.Snapshot.Timers.Single().Cron);
        Assert.False(timer.ShowsRandomInterval);
        timer.Cron = "  ";
        Assert.Null(Profile.Snapshot.Timers.Single().Cron);

        // Live state comes from the timer service.
        timer.RandomInterval = false;
        timer.Repeat = true;
        _host.Get<TimerService>().Tick();
        page.RefreshStates();
        Assert.StartsWith("Next in", timer.LiveStatus);
        Assert.Contains("0/5 chat lines", timer.LiveStatus);
        _host.Get<TimerService>().SetState(timer.Definition.Id.ToString(), enabled: false);
        Pump();
        Assert.Equal("Off", timer.LiveStatus);

        vm.SelectedPage = page;
        timer.Cron = "0 9 * * MON";
        TestApp.BindingErrors.Clear();
        Save(window, "timers-cron");
        AssertNoBindingErrors();
        window.Close();
    }

    // ---- Users & groups ----

    private void Chat(string platform, string userId, string user, bool moderator = false) =>
        _host.Get<UserRegistry>().Handle(new BotEvent(ChatHub.MessageEvent, new Dictionary<string, object?>
        {
            ["platform"] = platform, ["userId"] = userId, ["user"] = user, ["userName"] = user.ToLowerInvariant(),
            ["message"] = "hi", ["isModerator"] = moderator,
        }));

    [AvaloniaFact]
    public void Users_and_groups_write_through()
    {
        Chat("twitch", "1", "Alice", moderator: true);
        Chat("twitch", "1", "Alice");
        Chat("rumble", "r9", "Bob");
        var groups = _host.Get<GroupStore>();
        var (window, vm) = Open();
        var page = Page<UsersViewModel>(vm);
        vm.SelectedPage = page;

        Assert.Equal(2, page.Users.Count);
        Assert.Contains("rumble", page.Platforms);
        page.PlatformFilter = "rumble";
        Assert.Equal("Bob", Assert.Single(page.Users).Name);
        page.PlatformFilter = ChatHub.AllPlatforms;
        page.Search = "ali";
        var alice = Assert.Single(page.Users);
        Assert.Equal("2", alice.Messages);
        page.Search = "";

        Assert.True(_host.Get<UserRegistry>().HasSpoken("twitch", "1"));
        page.ResetFirstWordsCommand.Execute(null);
        Assert.False(_host.Get<UserRegistry>().HasSpoken("twitch", "1"));

        page.PresentMinutes = 15;
        page.PresentIntervalMinutes = 0;
        page.ResetFirstWordsOnStreamOnline = false;
        var settings = _host.Get<UserRegistry>().Settings;
        Assert.Equal(15, settings.PresentMinutes);
        Assert.Equal(0, settings.PresentIntervalMinutes);
        Assert.False(settings.ResetFirstWordsOnStreamOnline);

        page.NewGroupName = "VIPs";
        page.AddGroupCommand.Execute(null);
        Assert.True(groups.Exists("VIPs"));
        Assert.Equal("VIPs", page.SelectedGroup!.Name);

        page.MemberPlatform = "twitch";
        page.MemberName = "@alice";
        page.AddMemberCommand.Execute(null);
        var member = Assert.Single(groups.UsersIn("VIPs"));
        Assert.Equal("1", member.UserId);   // id filled in from the users Cuebot has seen
        page.SelectedUser = page.Users.Single(u => u.Name == "Bob");
        page.AddSelectedUserToGroupCommand.Execute(null);
        Assert.Equal(2, groups.UsersIn("VIPs").Count);
        Assert.Equal(2, page.Members.Count);

        var row = page.SelectedGroup!;
        row.NameText = "Friends";
        Assert.True(groups.Exists("Friends"));
        Assert.False(groups.Exists("VIPs"));
        Assert.Same(row, page.Groups.Single());   // the row being edited survives the refresh
        Assert.Same(row, page.SelectedGroup);

        page.RemoveMemberCommand.Execute(page.Members.First());
        Assert.Single(groups.UsersIn("Friends"));

        TestApp.BindingErrors.Clear();
        Save(window, "users-list");
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "Sections");
        tabs.SelectedIndex = 1;
        Save(window, "users-groups");
        vm.Settings.Theme = AppTheme.Light;
        Save(window, "users-groups-light");
        vm.Settings.Theme = AppTheme.Dark;
        AssertNoBindingErrors();

        page.ClearGroupCommand.Execute(null);
        Assert.Empty(groups.UsersIn("Friends"));
        page.DeleteGroupCommand.Execute(null);
        Assert.Empty(groups.Names);
        Assert.Null(page.SelectedGroup);
        window.Close();
    }

    // ---- Variables ----

    [AvaloniaFact]
    public async Task Variable_viewer_edits_honour_auto_type_and_refresh_both_tabs()
    {
        var viewer = _host.Get<VariableViewer>();
        var globals = _host.Get<GlobalVariables>();
        var (window, vm) = Open();
        var page = Page<VariablesViewModel>(vm);
        vm.SelectedPage = page;

        page.EditName = "points";
        page.EditValue = "5";
        page.EditPersisted = true;
        page.SetCommand.Execute(null);
        Assert.Equal(5L, globals.GetValue("points"));
        var row = Assert.Single(page.Persisted);
        Assert.Equal("5", row.Value);
        Assert.NotEqual("string", row.Type);

        page.AutoType = false;
        page.EditName = "code";
        page.EditValue = "007";
        page.EditPersisted = false;
        page.SetCommand.Execute(null);
        Assert.Equal("007", globals.GetValue("code"));
        Assert.Single(page.Temporary);

        // A change made elsewhere (e.g. a sub-action on another thread) shows up in both tabs.
        await Task.Run(() => globals.SetValue("points", 6m, persist: false));
        await Until(() => page.Temporary.Any(r => r.Name == "points"));
        Assert.Empty(page.Persisted);

        // User variables, filtered by platform.
        viewer.SetUserVariable("twitch", "1", "alice", "coins", "10", persisted: true);
        viewer.SetUserVariable("rumble", "9", "bob", "coins", "3", persisted: true);
        viewer.SetUserVariable("twitch", "1", "alice", "hat", "red", persisted: false);
        await Until(() => page.UserVariables.Count == 3);
        page.PlatformFilter = "rumble";
        Assert.Equal("bob", Assert.Single(page.UserVariables).UserName);
        page.PlatformFilter = ChatHub.AllPlatforms;
        page.SelectedTabIndex = 2;
        Assert.True(page.IsUsersTab);
        page.SelectedUserVariable = page.UserVariables.First(r => r.Name == "coins");
        Assert.Equal("coins", page.EditName);

        TestApp.BindingErrors.Clear();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "Tabs");
        Assert.Equal(2, tabs.SelectedIndex);
        Save(window, "variables-users");
        page.SelectedTabIndex = 1;
        Save(window, "variables-temporary");
        page.SelectedTabIndex = 2;
        AssertNoBindingErrors();

        page.DeleteForAllUsersCommand.Execute(null);
        Assert.Equal("hat", Assert.Single(viewer.UserVariables()).Name);
        page.DeleteAllCommand.Execute(null);
        Assert.True(page.IsConfirmingDeleteAll);
        page.ConfirmDeleteAllCommand.Execute(null);
        Assert.Empty(viewer.UserVariables());

        page.SelectedTabIndex = 1;
        page.DeleteCommand.Execute(page.Temporary.Single(r => r.Name == "code"));
        Assert.Null(globals.GetValue("code"));
        window.Close();
    }

    // ---- Watchers ----

    [AvaloniaFact]
    public void Watcher_edits_save_to_the_watcher_service()
    {
        var service = _host.Get<WatcherService>();
        var (window, vm) = Open();
        var page = Page<WatchersViewModel>(vm);
        vm.SelectedPage = page;

        page.AddFileWatcherCommand.Execute(null);
        page.AddFileTailCommand.Execute(null);
        page.AddProcessWatcherCommand.Execute(null);
        Assert.Single(page.FileWatchers);
        Assert.Single(page.FileTails);
        Assert.Single(page.ProcessWatchers);

        var folder = page.FileWatchers.Single();
        folder.Folder = _dataDir;
        Assert.True(folder.HasPendingEdit);
        Assert.Equal("", service.FileWatchers.Single().Folder);   // text edits are debounced
        folder.Flush();
        Assert.Equal(_dataDir, service.FileWatchers.Single().Folder);
        folder.Filter = "*.txt";
        folder.Deleted = false;   // check boxes save at once, with pending text
        var stored = service.FileWatchers.Single();
        Assert.Equal("*.txt", stored.Filter);
        Assert.False(stored.Deleted);
        folder.IncludeSubfolders = true;
        Assert.True(service.FileWatchers.Single().IncludeSubfolders);

        var tail = page.FileTails.Single();
        tail.FilePath = Path.Combine(_dataDir, "log.txt");
        tail.SkipEmptyLines = false;
        Assert.Equal(Path.Combine(_dataDir, "log.txt"), service.FileTails.Single().FilePath);

        var process = page.ProcessWatchers.Single();
        process.ProcessName = "obs64";
        process.Name = "OBS";
        process.Flush();
        Assert.Equal("obs64", service.ProcessWatchers.Single().ProcessName);
        Assert.Equal("OBS", service.ProcessWatchers.Single().Name);

        // Enabling starts it; the status comes from the service. "Set watcher state" shows up too.
        service.StartAsync().GetAwaiter().GetResult();
        folder.Enabled = true;
        Pump();
        Assert.True(service.FileWatchers.Single().Enabled);
        Assert.Equal(service.GetStatus(folder.Id).State == WatcherState.Running, folder.IsRunning);
        Assert.NotEqual("Off", folder.StatusText);
        service.SetEnabled("OBS", true);
        Pump();
        Assert.True(process.Enabled);

        page.DuplicateCommand.Execute(process);
        Assert.Equal(2, service.ProcessWatchers.Count);
        Assert.Equal(2, page.ProcessWatchers.Count);

        TestApp.BindingErrors.Clear();
        var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "Kinds");
        for (var i = 0; i < 3; i++)
        {
            tabs.SelectedIndex = i;
            Save(window, $"watchers-{i}");
        }
        AssertNoBindingErrors();

        page.RemoveCommand.Execute(page.ProcessWatchers[1]);
        Assert.Single(service.ProcessWatchers);
        window.Close();
    }

    // ---- Settings ----

    [AvaloniaFact]
    public async Task Settings_show_audio_devices_and_apply_the_text_culture()
    {
        var (window, vm) = Open();
        await Until(() => vm.Settings.AudioLoaded, 30000);
        Assert.NotEqual("Loading...", vm.Settings.AudioBackend);
        Assert.NotEmpty(vm.Settings.OutputDevices);
        Assert.NotEmpty(vm.Settings.InputDevices);
        var text = _host.Get<TextEnvironment>();
        Assert.Same(TextEnvironment.DefaultCulture, text.Culture);
        Assert.Equal("", vm.Settings.TextCulture.Name);

        vm.Settings.TextCulture = SettingsViewModel.CultureOptions.Single(c => c.Name == "de-DE");
        Assert.Equal("de-DE", text.Culture.Name);
        Assert.Contains("1.234,50", vm.Settings.CulturePreview);
        Assert.Equal("de-DE", new AppSettingsStore(new AppPaths(_dataDir)).Settings.Text.Culture);

        vm.Settings.TextCulture = SettingsViewModel.CultureOptions[0];
        Assert.Same(TextEnvironment.DefaultCulture, text.Culture);
        Assert.Equal(TextEnvironment.DefaultCulture, TextCulture.Resolve("not-a-culture"));
        window.Close();
    }

    // ---- Everything populated, both themes ----

    [AvaloniaFact]
    public async Task Populated_wave2_pages_render_in_both_themes()
    {
        var store = _host.Get<CommandStore>();
        store.Save(new CommandDefinition { Name = "Hello", Group = "Greetings", Commands = ["!hello", "!hi"] });
        store.Save(new CommandDefinition { Name = "Dice", Group = "Fun", Mode = CommandMode.Regex, Commands = ["^!roll (?<n>\\d+)$"] });
        store.Save(new CommandDefinition { Name = "Lurk", Commands = ["!lurk"], Enabled = false });
        Profile.Profile.Queues.Add(new QueueDefinition { Name = "Alerts", Blocking = true });
        Profile.Profile.Timers.Add(new TimerDefinition { Name = "Socials", RandomInterval = true, MinChatLines = 10 });
        Profile.Profile.Timers.Add(new TimerDefinition { Name = "Hydrate", Cron = "0 * * * *" });
        Profile.NotifyChanged();
        _host.Get<GroupStore>().Add("Regulars");
        Chat("twitch", "1", "Alice", moderator: true);
        Chat("twitch", "2", "Carol");
        _host.Get<VariableViewer>().SetGlobal("deaths", "3", persisted: true);
        _host.Get<VariableViewer>().SetGlobal("lastRaider", "Dana", persisted: false);
        _host.Get<WatcherService>().Save(new FileWatcherDefinition { Name = "Clips", Folder = _dataDir, Filter = "*.mp4", Enabled = false });

        TestApp.BindingErrors.Clear();
        var app = Avalonia.Application.Current!;
        var original = app.RequestedThemeVariant;
        var (window, vm) = Open();
        await Until(() => vm.Settings.AudioLoaded, 30000);
        try
        {
            var commands = Page<CommandsViewModel>(vm);
            commands.SelectCommand.Execute(commands.Groups.SelectMany(g => g.Commands).Single(c => c.Name == "Dice"));
            foreach (var (theme, suffix) in new[] { (AppTheme.Dark, ""), (AppTheme.Light, "-light") })
            {
                vm.Settings.Theme = theme;
                foreach (var page in vm.Pages.Where(p => p is CommandsViewModel or QueuesViewModel or TimersViewModel or UsersViewModel
                             or VariablesViewModel or WatchersViewModel or ActionsViewModel))
                {
                    vm.SelectedPage = page;
                    Save(window, $"populated-{MainWindowTests.FileName(page.Title)}{suffix}");
                }
                vm.SelectedPage = vm.Settings;
                Pump();
                var tabs = window.GetVisualDescendants().OfType<TabControl>().Single(t => t.Name == "Sections");
                tabs.SelectedIndex = 1;
                Save(window, $"settings-audio-text{suffix}");
                tabs.SelectedIndex = 0;
            }
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
