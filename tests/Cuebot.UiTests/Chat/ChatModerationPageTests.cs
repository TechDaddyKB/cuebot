using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels;
using Cuebot.App.ViewModels.Chat;
using Cuebot.App.Views;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Events;

namespace Cuebot.UiTests.Chat;

/// <summary>
/// The Chat page's moderation tools (each tool's state per platform), the mod log view and user card rows, platform
/// deletions and bans marking lines, and the send-as selector.
/// </summary>
public sealed class ChatModerationPageTests : IDisposable
{
    private static readonly string ScreenshotDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshots"));

    private readonly ChatPageFixture _f = new();
    private readonly V2Moderator _kick = new();
    private readonly ModLog _log;
    private readonly List<string> _opened = [];

    public ChatModerationPageTests() => _log = new ModLog(Path.Combine(_f.DataDir, ModLog.FileName));

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
        ModerationActions = new ModerationService(new ChatModerationService([_f.Moderator, _kick], [_f.Announcer]), _log, _f.Hub),
        OpenUrl = _opened.Add,
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

    private static IEnumerable<Button> ToolButtons(Control root, string container) =>
        root.GetVisualDescendants().OfType<Control>().First(c => c.Name == container).GetVisualDescendants().OfType<Button>();

    [AvaloniaFact]
    public void Each_tool_shows_what_it_does_on_the_selected_platform()
    {
        TestApp.BindingErrors.Clear();
        var vm = CreateViewModel();
        _f.Chat("Kicker", "spam", platform: "Kick", id: "k1");
        _f.Chat("Bob", "hello", id: "t1");
        var (window, view) = Show(vm);
        Assert.False(vm.HasChannelTools);   // All platforms, nothing selected

        vm.SelectedLine = vm.Messages.First(m => m.Platform == "Kick");
        Assert.Equal(["Unban", "Warn"], vm.UserTools.Select(t => t.Label));
        Assert.Equal([ModerationToolState.Works, ModerationToolState.Closest], vm.UserTools.Select(t => t.State));
        Assert.Contains("chat message that mentions", vm.UserTools[1].Hint);
        Assert.Equal(["Clear chat (website)", "Slow mode: turn on", "Followers-only (website)", "Subscribers-only", "Emote-only"],
            vm.ChannelTools.Select(t => t.Label));
        Assert.Equal([true, true, true, false, false], vm.ChannelTools.Select(t => t.IsEnabled));
        Assert.Contains("Kick has no subscribers-only", vm.ChannelTools[3].Hint);
        // This platform counts in 5-minute steps: the pane says what the 1-minute button really does.
        Assert.Equal("Kick: 5 min, rounded up from 1 min", vm.TimeoutNote);
        Assert.Contains(vm.GetMenu(vm.SelectedLine!), e => e.Label == "Timeout 1 minute (Kick: 5 min, rounded up from 1 min)");
        ChatPageFixture.Pump();
        Assert.Equal(vm.ChannelTools.Select(t => t.IsEnabled), ToolButtons(view, "ChannelToolBar").Select(b => b.IsEnabled));
        Assert.Equal(["Unban", "Warn"], ToolButtons(view, "UserTools").Select(b => (string)b.Content!));
        Save(window, "chat-moderation-tools");

        // A website tool opens the platform's page and sends nothing.
        vm.ChannelTools[0].RunCommand.Execute(null);
        ChatPageFixture.Until(() => _opened.Count == 1);
        Assert.Equal("https://kick.test/streamer", _opened[0]);
        Assert.Empty(_kick.Calls);

        // A chat mode switches on, then offers to switch off.
        vm.ChannelTools[1].RunCommand.Execute(null);
        ChatPageFixture.Until(() => _kick.Calls.Count == 1);
        Assert.Equal("SlowMode on", _kick.Calls[0]);
        Assert.Equal("Slow mode: turn off", vm.ChannelTools[1].Label);

        // Warn uses the reason box.
        vm.ModReason = "no spam please";
        vm.UserTools[1].RunCommand.Execute(null);
        ChatPageFixture.Until(() => _kick.Calls.Count == 2);
        Assert.Equal("Warn kicker no spam please", _kick.Calls[1]);

        // Twitch here only has the three classic tools: everything else is greyed out with the reason.
        vm.SelectedLine = vm.Messages.First(m => m.Platform == "Twitch");
        Assert.All(vm.ChannelTools, t => Assert.False(t.IsEnabled));
        Assert.All(vm.UserTools, t => Assert.Equal(ModerationToolState.NotAvailable, t.State));
        Assert.Equal("", vm.TimeoutNote);
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
        window.Close();
    }

    [AvaloniaFact]
    public void Mod_log_shows_page_actions_and_platform_deletions_and_bans_mark_the_lines()
    {
        var vm = CreateViewModel();
        _f.Chat("Bob", "buy followers", id: "t1");
        _f.Chat("Bob", "again", id: "t2");
        _f.Chat("Alice", "hi", id: "t3");
        var (window, _) = Show(vm);
        Assert.True(vm.HasModLog);
        Assert.False(vm.HasModLogLines);

        var bob = vm.Messages[0];
        vm.SelectedLine = bob;
        vm.TimeoutSelectedCommand.Execute("10");
        ChatPageFixture.Until(() => vm.ModLogLines.Count == 1);
        Assert.Equal("You", vm.ModLogLines[0].Actor);
        Assert.Equal("timed out Bob for 10 min", vm.ModLogLines[0].Summary);

        // A mod deletes a message on the website; another bans Alice there.
        _f.Bus.Publish(new BotEvent(ChatHub.MessageDeletedEvent, new Dictionary<string, object?>
        {
            ["platform"] = "Twitch", ["messageId"] = "t2", ["userId"] = "id-bob", ["user"] = "Bob", ["moderator"] = "WebMod",
        }));
        _f.Bus.Publish(new BotEvent(ChatHub.UserBannedEvent, new Dictionary<string, object?>
        {
            ["platform"] = "Twitch", ["userId"] = "id-alice", ["user"] = "Alice", ["moderator"] = "WebMod", ["reason"] = "rude",
        }));
        ChatPageFixture.Pump();
        Assert.True(vm.Messages[1].IsDeleted);
        Assert.False(vm.Messages[1].CanDelete);
        Assert.Equal("banned", vm.Messages[2].ActionNote);
        Assert.Equal(["WebMod (on Twitch)", "WebMod (on Twitch)", "You"], vm.ModLogLines.Select(l => l.Actor));
        Assert.Equal("Message: again", vm.ModLogLines[1].Detail);

        // The user card lists Bob's rows.
        vm.SelectedLine = vm.Messages[1];
        Assert.Equal(["deleted a message of Bob", "timed out Bob for 10 min"], vm.UserLogLines.Select(l => l.Summary));
        Assert.True(vm.HasUserLog);

        // A fresh page reads deletions and the log back.
        var again = CreateViewModel();
        Assert.True(again.Messages.Single(m => m.Message.Id == "t2").IsDeleted);
        Assert.Equal(3, again.ModLogLines.Count);
        Save(window, "chat-mod-log");
        window.Close();
    }

    [AvaloniaFact]
    public void Messages_without_a_platform_id_offer_no_delete()
    {
        var vm = CreateViewModel();
        _f.Bus.Publish(new BotEvent("twitch.chatMessage", new Dictionary<string, object?> { ["user"] = "Bob", ["message"] = "x" }));
        ChatPageFixture.Pump();
        // The fake adapter keeps the empty id; a real adapter (ChatMessage.Create) also marks it not deletable.
        Assert.DoesNotContain(vm.GetMenu(vm.Messages[0]), e => e.Label == "Delete message");
        var made = ChatMessage.Create("Twitch", "", "Bob", "1", "x", DateTimeOffset.Now);
        Assert.False(made.CanDelete);
        Assert.False(new ChatLineViewModel(made).CanDelete);
    }

    [AvaloniaFact]
    public void Send_as_offers_bot_only_where_a_bot_account_is_signed_in()
    {
        var vm = CreateViewModel();
        var (window, view) = Show(vm);
        Assert.Equal(["Default", "Broadcaster"], vm.SendAsOptions);

        _f.Sender.CanSendAsBot = true;
        vm.SendTarget = "Twitch";
        Assert.Equal(["Default", "Broadcaster", "Bot"], vm.SendAsOptions);
        var box = view.GetVisualDescendants().OfType<ComboBox>().First(c => c.Name == "SendAsBox");
        Assert.Equal(3, box.ItemCount);

        vm.SendAs = "Bot";
        vm.Draft = "hello from the bot";
        vm.SendCommand.Execute(null);
        ChatPageFixture.Until(() => _f.Sender.Sent.Count == 1);
        Assert.Equal(true, _f.Sender.Options.Single().Bot);
        Assert.Equal("Sent to Twitch as bot", vm.Status);

        vm.SendAs = "Broadcaster";
        vm.Draft = "as me";
        vm.SendCommand.Execute(null);
        ChatPageFixture.Until(() => _f.Sender.Sent.Count == 2);
        Assert.Equal(false, _f.Sender.Options[^1].Bot);

        // The bot signs out: Bot disappears and the choice falls back to Default.
        vm.SendAs = "Bot";
        _f.Sender.CanSendAsBot = false;
        vm.SendTarget = ChatHub.AllPlatforms;
        Assert.Equal(["Default", "Broadcaster"], vm.SendAsOptions);
        Assert.Equal("Default", vm.SendAs);
        vm.Draft = "plain";
        vm.SendCommand.Execute(null);
        ChatPageFixture.Until(() => _f.Sender.Sent.Count == 3);
        Assert.Equal(2, _f.Sender.Options.Count);   // the default send passes no options
        window.Close();
    }

    /// <summary>A Kick-like moderator with v2 capabilities: whole-minute timeouts, website tools, soft warnings.</summary>
    private sealed class V2Moderator : IChatModerator
    {
        public List<string> Calls { get; } = [];
        public string Platform => "Kick";
        public ChatModerationActions Available => ChatModerationActions.DeleteMessage | ChatModerationActions.Timeout | ChatModerationActions.Ban;
        public Task DeleteMessageAsync(string messageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task TimeoutAsync(string userId, string userName, TimeSpan duration, string? reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task BanAsync(string userId, string userName, string? reason, CancellationToken ct = default) => Task.CompletedTask;

        public ModerationCapability Capability(ModerationAction action) => action switch
        {
            ModerationAction.Timeout => new ModerationCapability(action, ModerationToolState.Closest)
            {
                DurationUnit = TimeSpan.FromMinutes(5), MinDuration = TimeSpan.FromMinutes(5), Note = "Steps of 5 minutes.",
            },
            ModerationAction.Warn => new ModerationCapability(action, ModerationToolState.Closest)
            {
                Note = "Kick has no warnings: Cuebot posts a chat message that mentions the viewer.",
            },
            ModerationAction.Clear or ModerationAction.FollowersOnly => ModerationCapability.Website(action, "https://kick.test/streamer", "Type it in Kick."),
            ModerationAction.SubscribersOnly or ModerationAction.EmoteOnly => ModerationCapability.NotAvailable(action, "Kick has no subscribers-only switch here."),
            _ => ModerationCapability.Works(action),
        };

        public Task<ModerationOutcome> ExecuteAsync(ModerationRequest r, CancellationToken ct = default)
        {
            Calls.Add(r.Action switch
            {
                ModerationAction.SlowMode => $"SlowMode {(r.Enabled ? "on" : "off")}",
                _ => $"{r.Action} {r.UserName} {r.Reason}".TrimEnd(),
            });
            return Task.FromResult(ModerationOutcome.Done());
        }
    }
}
