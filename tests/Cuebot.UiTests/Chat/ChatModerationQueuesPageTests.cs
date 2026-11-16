using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels;
using Cuebot.App.ViewModels.Chat;
using Cuebot.App.Views;
using Cuebot.Core.Chat.Moderation;

namespace Cuebot.UiTests.Chat;

/// <summary>
/// Phase 3 on the Chat page: the Held lane (AutoMod), the Mod tools tab (Shield Mode, unban requests, blocked terms),
/// suspicious-user tools and flags, and the "sign in again" notes when a permission is missing.
/// </summary>
public sealed class ChatModerationQueuesPageTests : IDisposable
{
    private static readonly string ScreenshotDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshots"));

    private readonly ChatPageFixture _f = new();
    private readonly QueueModerator _twitch = new();
    private readonly ModLog _log;

    public ChatModerationQueuesPageTests() => _log = new ModLog(Path.Combine(_f.DataDir, ModLog.FileName));

    public void Dispose()
    {
        _log.Dispose();
        _f.Dispose();
    }

    private ChatViewModel CreateViewModel() => new(_f.Hub, new ChatPageServices
    {
        Emotes = _f.Emotes,
        Engine = _f.Engine,
        Settings = _f.Settings,
        ModerationActions = new ModerationService(new ChatModerationService([_twitch], [_f.Announcer]), _log, _f.Hub),
    });

    private static (Window Window, ChatView View) Show(ChatViewModel vm)
    {
        var view = new ChatView { DataContext = vm };
        var window = new Window { Content = view, Width = 1280, Height = 760 };
        window.Show();
        ChatPageFixture.Pump();
        return (window, view);
    }

    private static void Save(Window window, string name)
    {
        ChatPageFixture.Pump();
        Directory.CreateDirectory(ScreenshotDir);
        TestApp.SaveFrame(window, Path.Combine(ScreenshotDir, name + ".png"));
    }

    private static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().First(c => c.Name == name);

    private static void Select(ChatView view, string tab)
    {
        Named<TabControl>(view, "SideTabs").SelectedItem = Named<TabItem>(view, tab);
        ChatPageFixture.Pump();
    }

    private HeldChatMessage Held(string id, string user, string text) =>
        new("Twitch", id, DateTimeOffset.Now) { UserId = "id-" + user.ToLowerInvariant(), UserName = user, Text = text, Reason = "AutoMod: aggression, level 2" };

    [AvaloniaFact]
    public void Held_lane_lists_held_messages_and_allows_or_denies_them()
    {
        TestApp.BindingErrors.Clear();
        _twitch.Hold(Held("h1", "Troll", "you are terrible"));
        var vm = CreateViewModel();
        var (window, view) = Show(vm);
        Assert.True(vm.Queues.HasQueues);
        Assert.Equal("Held (1)", vm.Queues.HeldHeader);
        Assert.True(Named<TabItem>(view, "HeldTab").IsVisible);

        // A second message is held while the page is open.
        _twitch.Hold(Held("h2", "Linker", "see example.com"));
        ChatPageFixture.Until(() => vm.Queues.HeldLines.Count == 2);
        Assert.Equal(["h1", "h2"], vm.Queues.HeldLines.Select(l => l.Held.Id));
        Select(view, "HeldTab");
        Assert.Equal(2, Named<ListBox>(view, "HeldList").ItemCount);
        Save(window, "chat-held-lane");

        vm.Queues.HeldLines[0].AllowCommand.Execute(null);
        ChatPageFixture.Until(() => _twitch.Calls.Count == 1);
        Assert.Equal("ApproveHeld h1", _twitch.Calls[0]);
        ChatPageFixture.Until(() => vm.Queues.PendingCount == 1);
        Assert.Equal("Held (1)", vm.Queues.HeldHeader);
        Assert.Equal("Allowed Troll's held message", vm.Queues.Status);
        // Decided lines stay below the waiting ones, with the outcome.
        Assert.Equal(["h2", "h1"], vm.Queues.HeldLines.Select(l => l.Held.Id));
        Assert.Equal("Allowed", vm.Queues.HeldLines[1].Outcome);

        vm.Queues.HeldLines[0].DenyCommand.Execute(null);
        ChatPageFixture.Until(() => _twitch.Calls.Count == 2);
        Assert.Equal("DenyHeld h2", _twitch.Calls[1]);
        ChatPageFixture.Until(() => vm.Queues.PendingCount == 0);
        Assert.Equal("Held", vm.Queues.HeldHeader);
        Assert.Equal(["held-deny", "held-approve"], _log.Recent().Select(r => r.Action));
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
        window.Close();
    }

    [AvaloniaFact]
    public void Mod_tools_toggle_shield_mode_answer_unban_requests_and_manage_blocked_terms()
    {
        TestApp.BindingErrors.Clear();
        _twitch.Requests.Add(new UnbanRequestInfo("Twitch", "r1", "id-bob", DateTimeOffset.Now) { UserName = "Bob", Text = "I am sorry" });
        _twitch.Terms.Add(new BlockedTermInfo("Twitch", "t1", "spoiler"));
        var vm = CreateViewModel();
        var (window, view) = Show(vm);
        Select(view, "ModToolsTab");
        Assert.Equal("Shield Mode: not known yet.", vm.Queues.ShieldText);

        vm.Queues.ReloadCommand.Execute(null);
        ChatPageFixture.Until(() => vm.Queues.UnbanRequests.Count == 1 && vm.Queues.BlockedTerms.Count == 1);
        Assert.Equal("Shield Mode is off.", vm.Queues.ShieldText);
        Assert.Equal("Turn Shield Mode on", (string)Named<Button>(view, "ShieldButton").Content!);
        Save(window, "chat-mod-tools");

        vm.Queues.ToggleShieldCommand.Execute(null);
        ChatPageFixture.Until(() => vm.Queues.IsShieldOn);
        Assert.Equal("ShieldMode on", _twitch.Calls[^1]);
        Assert.StartsWith("Shield Mode is on (turned on by Streamer", vm.Queues.ShieldText);
        Assert.Equal("Turn Shield Mode off", vm.Queues.ShieldButton);

        var request = vm.Queues.UnbanRequests[0];
        request.Answer = "One more chance";
        request.ApproveCommand.Execute(null);
        ChatPageFixture.Until(() => vm.Queues.UnbanRequests.Count == 0);
        Assert.Equal("ApproveUnbanRequest id-bob r1 One more chance", _twitch.Calls[^1]);
        Assert.Equal("No unban requests waiting.", vm.Queues.UnbanRequestsText);

        vm.Queues.NewTerm = "hate*";
        vm.Queues.AddTermCommand.Execute(null);
        ChatPageFixture.Until(() => vm.Queues.BlockedTerms.Count == 2);
        Assert.Equal("AddBlockedTerm hate*", _twitch.Calls[^1]);
        Assert.Equal("", vm.Queues.NewTerm);
        vm.Queues.BlockedTerms.Single(t => t.Text == "spoiler").RemoveCommand.Execute(null);
        ChatPageFixture.Until(() => vm.Queues.BlockedTerms.Count == 1);
        Assert.Equal("RemoveBlockedTerm t1", _twitch.Calls[^1]);
        Assert.Equal(["term-remove", "term-add", "unban-request-approve", "shield"], _log.Recent().Select(r => r.Action));
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
        window.Close();
    }

    [AvaloniaFact]
    public void Missing_permissions_say_to_sign_in_again_instead_of_failing()
    {
        _twitch.Missing = true;
        _twitch.Hold(Held("h1", "Troll", "x"));
        var vm = CreateViewModel();
        var (window, view) = Show(vm);
        Select(view, "ModToolsTab");
        Assert.False(vm.Queues.CanShield);
        Assert.False(Named<Button>(view, "ShieldButton").IsEnabled);
        Assert.StartsWith("Sign in to Twitch again and allow", vm.Queues.ShieldNote);
        Assert.StartsWith("Sign in to Twitch again and allow", vm.Queues.UnbanRequestsNote);
        Assert.StartsWith("Sign in to Twitch again and allow", vm.Queues.BlockedTermsNote);
        Assert.StartsWith("Sign in to Twitch again and allow", vm.Queues.HeldNote);

        // Clicking anyway sends nothing and says why.
        vm.Queues.HeldLines[0].AllowCommand.Execute(null);
        ChatPageFixture.Until(() => vm.Queues.Status.Length > 0);
        Assert.StartsWith("Sign in to Twitch again", vm.Queues.Status);
        Assert.Empty(_twitch.Calls);

        // After signing in again the tools come back.
        _twitch.Missing = false;
        vm.RefreshModeration();
        Assert.True(vm.Queues.CanShield);
        Assert.False(vm.Queues.HasShieldNote);
        window.Close();
    }

    [AvaloniaFact]
    public void Suspicious_users_get_tools_and_their_lines_are_flagged()
    {
        var vm = CreateViewModel();
        _f.Chat("Evader", "hi again", id: "t1");
        _f.Chat("Alice", "hello", id: "t2");
        var (window, view) = Show(vm);

        vm.SelectedLine = vm.Messages[0];
        Assert.Equal(["Unban", "Warn", "Monitor", "Restrict", "Not suspicious"], vm.UserTools.Select(t => t.Label));
        vm.UserTools[3].RunCommand.Execute(null);
        ChatPageFixture.Until(() => _twitch.Calls.Count == 1);
        Assert.Equal("RestrictUser id-evader", _twitch.Calls[0]);
        ChatPageFixture.Until(() => vm.Messages[0].HasSuspiciousNote);
        Assert.Equal("suspicious: restricted", vm.Messages[0].SuspiciousNote);
        Assert.False(vm.Messages[1].HasSuspiciousNote);
        ChatPageFixture.Pump();
        Assert.True(Named<TextBlock>(view, "SuspiciousNoteText").IsVisible);

        // Twitch flags a chatter by itself (a likely ban evader).
        _twitch.Flag(new SuspiciousUserInfo("Twitch", "id-alice", SuspiciousLevel.Monitored) { Why = "likely ban evader" });
        ChatPageFixture.Until(() => vm.Messages[1].HasSuspiciousNote);
        Assert.Equal("suspicious: monitored (likely ban evader)", vm.Messages[1].SuspiciousNote);
        // New lines of a flagged chatter are flagged too.
        _f.Chat("Alice", "again", id: "t3");
        ChatPageFixture.Until(() => vm.Messages.Count == 3);
        Assert.True(vm.Messages[2].HasSuspiciousNote);
        Save(window, "chat-suspicious-users");
        window.Close();
    }

    /// <summary>A Twitch-like moderator with queues; with <see cref="Missing"/> the queue tools lack their permission.</summary>
    private sealed class QueueModerator : IChatModerator, IModerationQueues
    {
        private readonly List<HeldChatMessage> _held = [];
        private readonly Dictionary<string, SuspiciousUserInfo> _suspicious = [];
        private ShieldModeState? _shield;

        public List<string> Calls { get; } = [];
        public List<UnbanRequestInfo> Requests { get; } = [];
        public List<BlockedTermInfo> Terms { get; } = [];
        public bool Missing { get; set; }
        public string Platform => "Twitch";
        public ChatModerationActions Available => ChatModerationActions.DeleteMessage | ChatModerationActions.Timeout | ChatModerationActions.Ban;
        public Task DeleteMessageAsync(string messageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task TimeoutAsync(string userId, string userName, TimeSpan duration, string? reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task BanAsync(string userId, string userName, string? reason, CancellationToken ct = default) => Task.CompletedTask;

        public ModerationCapability Capability(ModerationAction action) =>
            Missing && ModerationCapability.IsQueueTool(action)
                ? ModerationCapability.NotAvailable(action, "Sign in to Twitch again and allow \"Shield mode: events and toggle\".")
                : ModerationCapability.Works(action);

        public Task<ModerationOutcome> ExecuteAsync(ModerationRequest r, CancellationToken ct = default)
        {
            Calls.Add(r.Action switch
            {
                ModerationAction.ApproveHeld or ModerationAction.DenyHeld => $"{r.Action} {r.MessageId}",
                ModerationAction.ShieldMode => $"ShieldMode {(r.Enabled ? "on" : "off")}",
                ModerationAction.AddBlockedTerm => $"AddBlockedTerm {r.Term}",
                _ => $"{r.Action} {r.UserId} {r.ItemId} {r.Reason}".Replace("  ", " ").TrimEnd(),
            });
            switch (r.Action)
            {
                case ModerationAction.ApproveHeld or ModerationAction.DenyHeld:
                    var i = _held.FindIndex(h => h.Id == r.MessageId);
                    _held[i] = _held[i] with { Status = r.Action == ModerationAction.ApproveHeld ? HeldMessageStatus.Approved : HeldMessageStatus.Denied };
                    Raise(ModerationQueueKind.HeldMessages);
                    break;
                case ModerationAction.ShieldMode:
                    _shield = new ShieldModeState(r.Enabled) { ModeratorName = "Streamer", Since = DateTimeOffset.Now };
                    break;
                case ModerationAction.RestrictUser:
                    Flag(new SuspiciousUserInfo("Twitch", r.UserId, SuspiciousLevel.Restricted));
                    break;
                case ModerationAction.ApproveUnbanRequest or ModerationAction.DenyUnbanRequest:
                    Requests.RemoveAll(x => x.Id == r.ItemId);
                    break;
                case ModerationAction.AddBlockedTerm:
                    Terms.Add(new BlockedTermInfo("Twitch", "t" + (Terms.Count + 2), r.Term.Trim()));
                    break;
                case ModerationAction.RemoveBlockedTerm:
                    Terms.RemoveAll(t => t.Id == r.ItemId);
                    break;
            }
            return Task.FromResult(ModerationOutcome.Done());
        }

        public void Hold(HeldChatMessage held)
        {
            _held.Add(held);
            Raise(ModerationQueueKind.HeldMessages, held.Id);
        }

        public void Flag(SuspiciousUserInfo user)
        {
            _suspicious[user.UserId] = user;
            Raise(ModerationQueueKind.SuspiciousUsers, user.UserId);
        }

        // Raised from a worker thread, like the real platform events.
        private void Raise(ModerationQueueKind kind, string id = "") =>
            Task.Run(() => Changed?.Invoke(new ModerationQueueChange("Twitch", kind, id))).Wait();

        public IReadOnlyList<HeldChatMessage> HeldMessages => _held.ToArray();
        public ShieldModeState? ShieldMode => _shield;
        public SuspiciousUserInfo? SuspiciousUser(string userId) => _suspicious.GetValueOrDefault(userId);

        public Task RefreshAsync(CancellationToken ct = default)
        {
            _shield ??= new ShieldModeState(false);
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<UnbanRequestInfo>> GetUnbanRequestsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<UnbanRequestInfo>>(Requests.ToArray());

        public Task<IReadOnlyList<BlockedTermInfo>> GetBlockedTermsAsync(CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<BlockedTermInfo>>(Terms.ToArray());

        public event Action<ModerationQueueChange>? Changed;
    }
}
