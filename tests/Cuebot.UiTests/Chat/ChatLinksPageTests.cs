using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Cuebot.App.ViewModels;
using Cuebot.App.ViewModels.Chat;
using Cuebot.App.Views;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Storage;
using Cuebot.Core.Users;

namespace Cuebot.UiTests.Chat;

/// <summary>
/// Identity links on the Chat page (phases 4 and 5): off by default with nothing shown; the switch with its
/// pending-review text; the user card's linked accounts, suggestions and notes; the linked ban dialog with its preview,
/// second confirmation, results and Undo; "Undo incident" in the mod log.
/// </summary>
public sealed class ChatLinksPageTests : IDisposable
{
    private static readonly string ScreenshotDir =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "screenshots"));

    private readonly ChatPageFixture _f = new();
    private readonly KickModerator _kick = new();
    private readonly ModLog _log;
    private readonly JsonFile<ModerationSettings> _settings;
    private readonly IdentityLinks _links;
    private readonly ModerationService _service;
    private readonly List<string> _opened = [];

    public ChatLinksPageTests()
    {
        _log = new ModLog(Path.Combine(_f.DataDir, ModLog.FileName));
        _settings = new JsonFile<ModerationSettings>(Path.Combine(_f.DataDir, "moderation.json"), _f.Log);
        _links = new IdentityLinks(_log, () => _settings.Value);
        _service = new ModerationService(new ChatModerationService([_f.Moderator, _kick], [_f.Announcer]), _log, _f.Hub,
            settings: () => _settings.Value)
        {
            Links = _links,
        };
    }

    public void Dispose()
    {
        _service.Dispose();
        _links.Dispose();
        _log.Dispose();
        _settings.Dispose();
        _f.Dispose();
    }

    private ChatViewModel CreateViewModel() => new(_f.Hub, new ChatPageServices
    {
        Emotes = _f.Emotes,
        Engine = _f.Engine,
        Settings = _f.Settings,
        ModerationActions = _service,
        ModerationSettings = _settings,
        OpenUrl = _opened.Add,
    });

    private static (Window Window, ChatView View) Show(ChatViewModel vm)
    {
        var view = new ChatView { DataContext = vm };
        var window = new Window { Content = view, Width = 1400, Height = 900 };
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

    private static bool Shown(Control c) => c.IsEffectivelyVisible;

    /// <summary>Bob on Twitch and Bob on Kick, linked (a mod suggested it, the streamer confirmed), then the switch set.</summary>
    private void LinkBob(bool leaveOn)
    {
        _settings.Value.IdentityLinksEnabled = true;
        var proposed = _links.ProposeAsync(new ViewerRef("Twitch", "id-bob", "Bob"), new ViewerRef("Kick", "id-bob", "Bob"),
            ModActor.Member("member-sam", "Sam")).GetAwaiter().GetResult();
        Assert.True(proposed.Ok, proposed.Message);
        Assert.True(_links.Confirm(proposed.LinkIds[0], ModActor.Streamer).Ok);
        _settings.Value.IdentityLinksEnabled = leaveOn;
    }

    [AvaloniaFact]
    public void Identity_links_are_off_by_default_and_nothing_about_them_shows()
    {
        TestApp.BindingErrors.Clear();
        Assert.False(_settings.Value.IdentityLinksEnabled);
        LinkBob(leaveOn: false);
        var vm = CreateViewModel();
        _f.Chat("Bob", "hello", id: "t1");
        _f.Chat("Bob", "hi from kick", platform: "Kick", id: "k1");
        var (window, view) = Show(vm);
        vm.SelectedLine = vm.Messages.First(m => m.Platform == "Twitch");
        vm.ShowOptions = true;
        ChatPageFixture.Pump();

        Assert.False(vm.ShowLinkTools);
        Assert.Empty(vm.LinkedAccounts);
        Assert.Empty(vm.Notes);
        Assert.False(Named<CheckBox>(view, "IdentityLinksBox").IsChecked);
        Assert.False(Named<CheckBox>(view, "MirroringBox").IsEnabled);
        Assert.Contains("Pending review", Named<TextBlock>(view, "IdentityLinksText").Text);
        Save(window, "chat-links-setting-off");
        vm.ShowOptions = false;
        ChatPageFixture.Pump();
        Assert.False(Shown(Named<StackPanel>(view, "LinkSection")));

        // Ban works on the one account, the old way: no preview, nothing on Kick.
        vm.BanSelectedCommand.Execute(null);
        vm.BanSelectedCommand.Execute(null);
        ChatPageFixture.Until(() => _f.Moderator.Calls.Count == 1);
        Assert.False(vm.HasLinkedAction);
        Assert.Empty(_kick.Calls);
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
        window.Close();
    }

    [AvaloniaFact]
    public void Turning_links_on_shows_the_card_and_ban_opens_the_linked_preview()
    {
        TestApp.BindingErrors.Clear();
        LinkBob(leaveOn: false);
        var vm = CreateViewModel();
        _f.Chat("Bob", "hello", id: "t1");
        _f.Chat("Bob", "hi from kick", platform: "Kick", id: "k1");
        var (window, view) = Show(vm);
        vm.SelectedLine = vm.Messages.First(m => m.Platform == "Twitch");

        vm.Options.IdentityLinks = true;
        ChatPageFixture.Pump();
        Assert.True(_settings.Value.IdentityLinksEnabled);
        Assert.True(vm.Options.CanMirror);
        Assert.False(_settings.Value.AutomaticMirroring);
        Assert.True(vm.ShowLinkTools);
        Assert.Equal(["this account", "confirmed by Sam and Streamer"], vm.LinkedAccounts.Select(a => a.StatusText));
        Assert.True(Shown(Named<StackPanel>(view, "LinkSection")));

        vm.NoteDraft = "Was warned about link spam on Kick";
        vm.AddNoteCommand.Execute(null);
        Assert.Single(vm.Notes);
        vm.ModReason = "ban evasion";
        Save(window, "chat-links-user-card");

        vm.BanSelectedCommand.Execute(null);
        ChatPageFixture.Until(() => vm.HasLinkedAction);
        var dialog = vm.LinkedAction!;
        Assert.Equal(["Twitch: Bob", "Kick: Bob"], dialog.Rows.Select(r => r.Account));
        Assert.All(dialog.Rows, r => Assert.True(r.IsTicked));
        Assert.Equal("ban evasion", dialog.Reason);
        Assert.True(Shown(Named<Border>(view, "LinkedActionOverlay")));
        Save(window, "chat-linked-ban-preview");

        dialog.RunCommand.Execute(null);
        ChatPageFixture.Until(() => dialog.Confirming);
        Assert.Equal("Ban 2 accounts on 2 platforms?", dialog.ConfirmText);
        Assert.Empty(_kick.Calls);
        dialog.RunCommand.Execute(null);
        ChatPageFixture.Until(() => dialog.IsDone);
        Assert.Equal(["Done", "Done"], dialog.Rows.Select(r => r.Result));
        Assert.Equal(["Ban Bob ban evasion"], _kick.Calls);   // the streamer's own reason: no "[mod: ...]"
        Assert.Single(_f.Moderator.Calls, c => c.StartsWith("ban id-bob", StringComparison.Ordinal));
        Assert.True(dialog.CanUndo);
        Assert.True(vm.HasLastIncident);
        Save(window, "chat-linked-ban-done");

        // The mod log offers "Undo incident" on the rows still in force.
        ChatPageFixture.Until(() => vm.ModLogLines.Count(l => l.Entry.Action == "ban") == 2);
        Assert.All(vm.ModLogLines.Where(l => l.Entry.Action == "ban"), l => Assert.True(l.CanUndoIncident));
        Assert.Contains(vm.ModLogLines, l => l.Detail.Contains("Through a linked account"));

        dialog.UndoCommand.Execute(null);
        ChatPageFixture.Until(() => _kick.Calls.Count == 2);
        Assert.StartsWith("Unban Bob Undo of incident", _kick.Calls[1]);
        dialog.CloseCommand.Execute(null);
        Assert.False(vm.HasLinkedAction);
        Assert.True(TestApp.BindingErrors.Errors.Count == 0, string.Join("\n", TestApp.BindingErrors.Errors));
        window.Close();
    }

    [AvaloniaFact]
    public void A_suggestion_made_on_the_card_waits_for_someone_else()
    {
        _settings.Value.IdentityLinksEnabled = true;
        var vm = CreateViewModel();
        _f.Chat("Bob", "hello", id: "t1");
        _f.Chat("Bobby", "I'm Bob on Twitch", platform: "Kick", id: "k1");
        var (window, view) = Show(vm);
        vm.SelectedLine = vm.Messages.First(m => m.Platform == "Twitch");
        Assert.Empty(vm.LinkedAccounts);

        vm.ToggleProposeFormCommand.Execute(null);
        ChatPageFixture.Pump();
        Assert.True(Shown(Named<StackPanel>(view, "ProposeForm")));
        Assert.Equal(["Kick"], vm.LinkPlatforms);
        vm.ProposeName = "Nobody";
        vm.ProposeCommand.Execute(null);
        ChatPageFixture.Until(() => vm.LinkStatus.Contains("Nobody called"));

        vm.ProposeName = "Bobby";
        vm.ProposeEvidence = "I'm Bob on Twitch";
        vm.ProposeCommand.Execute(null);
        ChatPageFixture.Until(() => vm.LinkedAccounts.Count == 2);
        var kick = vm.LinkedAccounts[1];
        Assert.Equal("suggested by Streamer, not confirmed", kick.StatusText);
        Assert.True(kick.CanConfirm);
        kick.ConfirmCommand.Execute(null);
        ChatPageFixture.Until(() => vm.LinkStatus.Contains("someone else"));

        vm.StartVerificationCommand.Execute(null);
        ChatPageFixture.Until(() => vm.HasVerification);
        Assert.Contains("!link", vm.VerificationText);

        // Banning a suggested link: the Kick row starts unticked.
        vm.BanSelectedCommand.Execute(null);
        ChatPageFixture.Until(() => vm.HasLinkedAction);
        Assert.Equal([true, false], vm.LinkedAction!.Rows.Select(r => r.IsTicked));
        Save(window, "chat-linked-ban-suggested");

        kick.UnlinkCommand.Execute(null);
        ChatPageFixture.Until(() => vm.HasPendingUnlink);
        vm.PendingUnlink!.MarkedWrong = true;
        vm.PendingUnlink.ConfirmCommand.Execute(null);
        ChatPageFixture.Until(() => vm.LinkedAccounts.Count == 0);
        window.Close();
    }

    /// <summary>A Kick moderator that records calls ("Ban name reason").</summary>
    private sealed class KickModerator : IChatModerator
    {
        public List<string> Calls { get; } = [];
        public string Platform => "Kick";
        public ChatModerationActions Available => ChatModerationActions.Ban | ChatModerationActions.Timeout;
        public Task DeleteMessageAsync(string messageId, CancellationToken ct = default) => Task.CompletedTask;
        public Task TimeoutAsync(string userId, string userName, TimeSpan duration, string? reason, CancellationToken ct = default) => Task.CompletedTask;
        public Task BanAsync(string userId, string userName, string? reason, CancellationToken ct = default) => Task.CompletedTask;
        public ModerationCapability Capability(ModerationAction action) => ModerationCapability.Works(action) with { MaxReasonLength = 100 };

        public Task<ModerationOutcome> ExecuteAsync(ModerationRequest r, CancellationToken ct = default)
        {
            Calls.Add($"{r.Action} {r.UserName} {r.Reason}".TrimEnd());
            return Task.FromResult(ModerationOutcome.Done());
        }
    }
}
