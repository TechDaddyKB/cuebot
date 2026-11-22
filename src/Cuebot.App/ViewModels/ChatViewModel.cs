using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.App.ViewModels.Chat;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Emotes;
using Cuebot.Core.Chat.Moderation;

namespace Cuebot.App.ViewModels;

/// <summary>Colours that identify each platform in the combined chat.</summary>
internal static class PlatformColors
{
    public static IBrush For(string platform) => platform.ToLowerInvariant() switch
    {
        "twitch" => new SolidColorBrush(Color.Parse("#9146FF")),
        "youtube" => new SolidColorBrush(Color.Parse("#FF3B3B")),
        "kick" => new SolidColorBrush(Color.Parse("#53FC18")),
        "rumble" => new SolidColorBrush(Color.Parse("#85C742")),
        _ => new SolidColorBrush(Color.Parse("#6CA8FF")),
    };

    public static IBrush Highlight(HighlightKind kind) => kind switch
    {
        HighlightKind.Mention => new SolidColorBrush(Color.Parse("#40E5534B")),
        HighlightKind.Keyword => new SolidColorBrush(Color.Parse("#40F2B33D")),
        HighlightKind.FirstTime => new SolidColorBrush(Color.Parse("#404CAF50")),
        _ => Brushes.Transparent,
    };
}

public sealed partial class ChatLineViewModel : ObservableObject
{
    public ChatLineViewModel(ChatMessage message, IReadOnlyList<ChatSegment>? segments = null,
        HighlightResult highlight = default, IReadOnlyDictionary<string, object?>? rawArgs = null)
    {
        Message = message;
        RawArgs = rawArgs;
        _segments = segments ?? [new TextSegment(message.Text)];
        _highlight = highlight;
        PlatformBrush = PlatformColors.For(message.Platform);
        UserBrush = message.Color is { } c && Color.TryParse(c, out var color) ? new SolidColorBrush(color) : null;
    }

    public ChatMessage Message { get; }

    /// <summary>The platform event's arguments (from the emote service's index), when known.</summary>
    public IReadOnlyDictionary<string, object?>? RawArgs { get; }

    public string Platform => Message.Platform;
    public IBrush PlatformBrush { get; }
    public string Time => Message.Time.ToLocalTime().ToString("HH:mm");
    public string User => Message.User;

    /// <summary>Login name (lowercase) for lookups such as pronouns: the event's userName, else the display name.</summary>
    public string Login => (RawArgs?.GetValueOrDefault("userName") as string is { Length: > 0 } login ? login : Message.User).ToLowerInvariant();

    /// <summary>Platform name colour, or null to use the theme's text colour.</summary>
    public IBrush? UserBrush { get; }
    public string Text => Message.Text;
    public string? Amount => Message.Amount;
    public bool HasAmount => Message.Amount is not null;

    /// <summary>Plain-text role badges (no emoji, so they render with any font on Linux).</summary>
    public string Badges => string.Concat(
        Message.Roles.HasFlag(ChatRoles.Broadcaster) ? "[host] " : "",
        Message.Roles.HasFlag(ChatRoles.Moderator) ? "[mod] " : "",
        Message.Roles.HasFlag(ChatRoles.Vip) ? "[vip] " : "",
        Message.Roles.HasFlag(ChatRoles.Subscriber) ? "[sub] " : "",
        Message.Roles.HasFlag(ChatRoles.Verified) ? "[verified] " : "");

    [ObservableProperty] private IReadOnlyList<ChatSegment> _segments;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPronouns))]
    private string? _pronouns;

    public bool HasPronouns => !string.IsNullOrEmpty(Pronouns);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHighlighted), nameof(HighlightBrush), nameof(HighlightReason))]
    private HighlightResult _highlight;

    public bool IsHighlighted => Highlight.IsHighlighted;
    public IBrush HighlightBrush => PlatformColors.Highlight(Highlight.Kind);
    public string? HighlightReason => Highlight.IsHighlighted ? Highlight.Reason : null;

    /// <summary>Set after a successful delete, from Cuebot or reported by the platform (a mod on the website).</summary>
    [ObservableProperty] private bool _isDeleted;

    /// <summary>"banned", "timed out for 10 min": set when the platform reports the author was actioned.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasActionNote))]
    private string? _actionNote;

    public bool HasActionNote => !string.IsNullOrEmpty(ActionNote);

    /// <summary>"suspicious: monitored (likely ban evader)" when the platform flags the author; null otherwise.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuspiciousNote))]
    private string? _suspiciousNote;

    public bool HasSuspiciousNote => !string.IsNullOrEmpty(SuspiciousNote);

    /// <summary>False when the platform gave the message no id of its own, so it cannot be deleted.</summary>
    public bool CanDelete => Message.CanDelete && !IsDeleted;

    /// <summary>Top-level emote segments (overlays hang off them).</summary>
    public IEnumerable<EmoteSegment> Emotes => Segments.OfType<EmoteSegment>();
}

public sealed class FeedLineViewModel(FeedItem item)
{
    public FeedItem Item { get; } = item;
    public string Platform => Item.Platform;
    public IBrush PlatformBrush { get; } = PlatformColors.For(item.Platform);
    public string Time => Item.Time.ToLocalTime().ToString("HH:mm");
    public string Title => Item.Title;
    public string? Detail => Item.Detail;
    public bool HasDetail => !string.IsNullOrEmpty(Item.Detail);
    public string Kind => Item.Kind.ToString();
}

/// <summary>A quick action button on the chat page.</summary>
public sealed partial class QuickActionButtonViewModel(QuickActionDefinition definition, ChatViewModel owner) : ObservableObject
{
    public QuickActionDefinition Definition { get; } = definition;
    public string Label => Definition.Label.Length > 0 ? Definition.Label : Definition.ActionName;
    public string ToolTip => $"Runs \"{Definition.ActionName}\" ({Definition.Scope})";

    public bool CanRun => Definition.ActionName.Length > 0 && (Definition.Scope == QuickActionScope.Global || owner.SelectedLine is not null);

    internal void Refresh() => OnPropertyChanged(nameof(CanRun));

    [RelayCommand]
    private Task Run() => owner.RunQuickActionAsync(Definition, owner.SelectedLine);
}

/// <summary>An emote offered by send-box autocomplete.</summary>
public sealed class EmoteSuggestionViewModel(Emote emote) : EmoteSegment(new EmoteMatch(0, emote.Code.Length, emote.Code, emote))
{
    public Emote Emote { get; } = emote;
}

/// <summary>An entry in a chat line's actions menu; a null <see cref="Run"/> is a separator.</summary>
/// <summary>
/// One entry of a chat line's menu. With <see cref="ConfirmLabel"/> the entry opens a submenu holding one item with that
/// label, and only that item runs <see cref="Run"/> (used for Ban, which cannot be undone from Cuebot).
/// </summary>
public sealed record ChatMenuEntry(string Label, Func<Task>? Run, string? ConfirmLabel = null)
{
    public bool IsSeparator => Run is null;
    public static ChatMenuEntry Separator { get; } = new("-", null);
}

/// <summary>
/// Combined chat across all platforms, the activity feed, and a send box with emote autocomplete, slash commands and
/// quick actions. Emotes, pronouns, highlights and moderation come from <see cref="ChatPageServices"/> when available.
/// </summary>
public sealed partial class ChatViewModel : PageViewModel
{
    private const int MaxLines = 500;
    private const int MaxSuggestions = 12;

    private readonly ChatHub _hub;
    private readonly ChatPageServices _services;
    private readonly ChatPageSettings _settings;
    private readonly List<ChatMessage> _all = [];
    private readonly ModerationService? _moderation;

    public ChatViewModel(ChatHub hub, ChatPageServices? services = null) : base("Chat")
    {
        _hub = hub;
        _services = services ?? new ChatPageServices();
        _settings = _services.Settings?.Value ?? new ChatPageSettings();
        // One way into moderation (permission, safety, log); a page given only the platform lookup gets one without a log.
        _moderation = _services.ModerationActions
                      ?? (_services.Moderation is { } lookup ? new ModerationService(lookup) : null);
        Queues = new ModerationQueuesViewModel(_moderation, OnUi);
        Queues.QueueChanged += OnQueueChanged;
        PlatformFilters = [ChatHub.AllPlatforms, .. hub.Platforms];
        PlatformChips.Add(ChatHub.AllPlatforms);
        SendTargets = [ChatHub.AllPlatforms, .. hub.Platforms];
        Options = new ChatOptionsViewModel(_settings, () => _services.Settings?.ScheduleSave(), _services.Emotes,
            _services.EmoteSettings, _services.ActionNames ?? (() => []), _services.ModerationSettings);
        Options.Changed += OnOptionsChanged;
        if (_services.Profile is { } profile) profile.ActionRenamed += (from, to) => OnUi(() => FollowActionRename(from, to));
        RebuildQuickActions();

        foreach (var message in hub.RecentMessages()) AddLine(message);
        foreach (var item in hub.RecentFeed()) Feed.Insert(0, new FeedLineViewModel(item));
        RefilterFeed();
        // Always queued (even on the UI thread): the hub raises this while the platform event is still being
        // published, before the emote service has indexed that event's emote arguments.
        hub.MessageReceived += m => Avalonia.Threading.Dispatcher.UIThread.Post(() => AddLine(m));
        hub.FeedItemAdded += f => OnUi(() => AddFeed(f));
        hub.ModerationSeen += n => OnUi(() => ApplyNotice(n));
        if (_moderation?.Log is { } log)
        {
            foreach (var entry in log.Recent(MaxLogLines).Reverse()) AddLogLine(entry);
            log.Changed += e => OnUi(() => AddLogLine(e));
        }
        RefreshSendAs();
        WireLinks();
        if (_services.Emotes is { } emotes)
        {
            emotes.EnsureStarted();
            emotes.EmotesChanged += _ => OnUi(RefreshEmotes);
        }
    }

    public ObservableCollection<ChatLineViewModel> Messages { get; } = [];
    public ObservableCollection<FeedLineViewModel> Feed { get; } = [];
    public ObservableCollection<QuickActionButtonViewModel> QuickActions { get; } = [];
    public ObservableCollection<EmoteSuggestionViewModel> Suggestions { get; } = [];
    public IReadOnlyList<string> PlatformFilters { get; }

    /// <summary>
    /// The chat's filter chips: All, then the platforms that have sent a message (in <see cref="PlatformFilters"/>
    /// order), so two dozen sources that are not in use do not crowd the chat.
    /// </summary>
    public ObservableCollection<string> PlatformChips { get; } = [];

    private void AddPlatformChip(string platform)
    {
        var name = PlatformFilters.FirstOrDefault(p => string.Equals(p, platform, StringComparison.OrdinalIgnoreCase));
        if (name is null || PlatformChips.Contains(name)) return;
        var order = PlatformFilters.ToList();
        var index = 1;
        while (index < PlatformChips.Count && order.IndexOf(PlatformChips[index]) < order.IndexOf(name)) index++;
        PlatformChips.Insert(index, name);
    }
    public IReadOnlyList<string> SendTargets { get; }
    public ChatOptionsViewModel Options { get; }

    /// <summary>The Held lane and the Mod tools tab (Shield Mode, unban requests, blocked terms), for platforms with queues.</summary>
    public ModerationQueuesViewModel Queues { get; }

    public bool HasQuickActions => QuickActions.Count > 0;

    /// <summary>Raised after a line is appended, so the view can keep the newest message in sight.</summary>
    public event Action? LineAdded;

    [ObservableProperty] private string _filter = ChatHub.AllPlatforms;
    [ObservableProperty] private string _sendTarget = ChatHub.AllPlatforms;
    [ObservableProperty] private string _draft = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _showOptions;
    [ObservableProperty] private ChatLineViewModel? _selectedLine;
    [ObservableProperty] private bool _isSuggesting;
    [ObservableProperty] private EmoteSuggestionViewModel? _selectedSuggestion;

    partial void OnFilterChanged(string value)
    {
        // The chips list only platforms seen so far: a filter set from code gets its chip, and a cleared chip
        // selection means All.
        if (value is null)
        {
            Filter = ChatHub.AllPlatforms;
            return;
        }
        if (value != ChatHub.AllPlatforms) AddPlatformChip(value);
        Messages.Clear();
        foreach (var m in _all.Where(Visible)) Messages.Add(CreateLine(m));
        RebuildTools();
    }

    partial void OnSelectedLineChanged(ChatLineViewModel? value)
    {
        foreach (var q in QuickActions) q.Refresh();
        ConfirmingBan = false;
        RefreshUserLines();
        OnPropertyChanged(nameof(HasSelectedLine));
        RefreshModeration();
    }

    /// <summary>
    /// Re-reads what each platform offers (connections and sign-ins change) and rebuilds the tool buttons. Called when the
    /// selection or the filter changes; tests call it after changing a fake platform.
    /// </summary>
    public void RefreshModeration()
    {
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(CanTimeoutSelected));
        OnPropertyChanged(nameof(CanBanSelected));
        OnPropertyChanged(nameof(ModerationHint));
        OnPropertyChanged(nameof(TimeoutNote));
        OnPropertyChanged(nameof(HasTimeoutNote));
        DeleteSelectedCommand.NotifyCanExecuteChanged();
        RebuildTools();
        RefreshUserLog();
        RefreshSendAs();
        Queues.Refresh();
        RefreshLinks();
    }

    // ---------------------------------------------------------------- user details (right pane)

    public bool HasSelectedLine => SelectedLine is not null;

    /// <summary>The selected chatter's recent messages on that platform, newest first.</summary>
    public ObservableCollection<ChatLineViewModel> UserLines { get; } = [];

    /// <summary>What a tool does on the platform of <paramref name="line"/> (not available without moderation).</summary>
    private ModerationCapability CapabilityFor(string platform, ModerationAction action) =>
        _moderation?.Capability(platform, action) ?? ModerationCapability.NotAvailable(action, "Moderation is not available here.");

    private bool Runs(ChatLineViewModel? line, ModerationAction action) => line is not null && CapabilityFor(line.Platform, action).CanRun;

    public bool CanDeleteSelected => Runs(SelectedLine, ModerationAction.Delete) && SelectedLine is { CanDelete: true, Message.Id.Length: > 0 };
    public bool CanTimeoutSelected => Runs(SelectedLine, ModerationAction.Timeout) && SelectedLine is { } line && CanPunish(line);
    public bool CanBanSelected => Runs(SelectedLine, ModerationAction.Ban) && SelectedLine is { } line && CanPunish(line);

    /// <summary>Why the pane offers no Timeout/Ban for the selected line, or "" when it does (or nothing is selected).</summary>
    public string ModerationHint =>
        SelectedLine is not { } line || CanTimeoutSelected || CanBanSelected ? ""
        : !CanPunish(line) ? "This is the broadcaster's own message: they can't be timed out or banned in their own chat."
        : CapabilityFor(line.Platform, ModerationAction.Ban) is { State: ModerationToolState.OnWebsite } web
            ? $"{line.Platform}: {web.Note} The buttons below open the page."
            : "This platform offers no moderation from Cuebot right now.";

    /// <summary>
    /// The duration a timeout really gets on the selected platform when it differs ("Kick: 1 min, rounded up from 30 s"),
    /// shown under the timeout buttons; empty when the platform applies what is asked.
    /// </summary>
    public string TimeoutNote
    {
        get
        {
            if (SelectedLine is not { } line || !CanTimeoutSelected || _moderation is null) return "";
            var notes = TimeoutChoices.Select(t => _moderation.PreviewDuration(line.Platform, t).Note).Where(n => n.Length > 0).ToArray();
            return string.Join(". ", notes);
        }
    }

    public bool HasTimeoutNote => TimeoutNote.Length > 0;

    /// <summary>The timeout buttons' lengths.</summary>
    internal static readonly TimeSpan[] TimeoutChoices = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(10)];

    /// <summary>Reason for timeouts, bans and warnings (optional; members' reasons get their name in front on Twitch and Kick).</summary>
    [ObservableProperty] private string _modReason = "";

    // ---------------------------------------------------------------- moderation tools

    /// <summary>Tools about the selected person: unban and warn, for their platform.</summary>
    public ObservableCollection<ModToolViewModel> UserTools { get; } = [];

    /// <summary>Tools for the whole chat (clear, slow, followers-only, subscribers-only, emote-only) of <see cref="ToolPlatform"/>.</summary>
    public ObservableCollection<ModToolViewModel> ChannelTools { get; } = [];

    public bool HasChannelTools => ChannelTools.Count > 0;

    /// <summary>The selected message's platform, else the filtered platform; null shows no chat tools.</summary>
    public string? ToolPlatform => SelectedLine?.Platform ?? (Filter != ChatHub.AllPlatforms ? Filter : null);

    /// <summary>"YouTube quota: 7,400 left today, 2,000 kept for reading chat" for a platform that charges one.</summary>
    public string QuotaText =>
        ToolPlatform is { } p && _moderation?.QuotaFor(p) is { } q
            ? string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0} quota: {1:N0} units left today, {2:N0} kept for reading chat.",
                q.Platform, q.Remaining, q.Reserve)
            : "";

    public bool HasQuotaText => QuotaText.Length > 0;

    private static readonly ModerationAction[] UserToolActions = [ModerationAction.Unban, ModerationAction.Warn];

    private static readonly ModerationAction[] SuspiciousToolActions =
        [ModerationAction.MonitorUser, ModerationAction.RestrictUser, ModerationAction.ClearSuspicious];

    private static readonly ModerationAction[] ChannelToolActions =
    [
        ModerationAction.Clear, ModerationAction.SlowMode, ModerationAction.FollowersOnly, ModerationAction.SubscribersOnly, ModerationAction.EmoteOnly,
    ];

    private readonly Dictionary<(string, ModerationAction), bool> _modes = [];

    private void RebuildTools()
    {
        UserTools.Clear();
        ChannelTools.Clear();
        if (_moderation is not null && SelectedLine is { } line && CanPunish(line))
        {
            foreach (var action in UserToolActions)
                UserTools.Add(new ModToolViewModel(line.Platform, CapabilityFor(line.Platform, action), RunUserToolAsync));
            // Suspicious user tools, where the platform has them (Twitch).
            if (_moderation.QueuesFor(line.Platform) is not null)
                foreach (var action in SuspiciousToolActions)
                    UserTools.Add(new ModToolViewModel(line.Platform, CapabilityFor(line.Platform, action), RunUserToolAsync));
        }
        if (_moderation is not null && ToolPlatform is { } platform)
            foreach (var action in ChannelToolActions)
            {
                var toggle = action != ModerationAction.Clear;
                ChannelTools.Add(new ModToolViewModel(platform, CapabilityFor(platform, action), RunChannelToolAsync, toggle)
                {
                    IsOn = toggle && _modes.GetValueOrDefault((platform, action)),
                });
            }
        OnPropertyChanged(nameof(HasChannelTools));
        OnPropertyChanged(nameof(ToolPlatform));
        OnPropertyChanged(nameof(QuotaText));
        OnPropertyChanged(nameof(HasQuotaText));
    }

    private async Task RunUserToolAsync(ModToolViewModel tool)
    {
        if (SelectedLine is not { } line) return;
        await RunAsync(Command(line, tool.Action));
    }

    private async Task RunChannelToolAsync(ModToolViewModel tool)
    {
        var turnOn = !tool.IsOn;
        var result = await RunAsync(new ModerationCommand(tool.Platform, tool.Action)
        {
            Enabled = turnOn,
            ChannelId = SelectedLine?.Platform == tool.Platform ? SelectedLine.Message.ChannelId : null,
            Reason = ModReason.Trim().Length > 0 ? ModReason.Trim() : null,
        });
        if (result?.Ok == true && tool.IsToggle)
        {
            _modes[(tool.Platform, tool.Action)] = turnOn;
            tool.IsOn = turnOn;
        }
    }

    /// <summary>A command about <paramref name="line"/> and its author, with the reason box.</summary>
    private ModerationCommand Command(ChatLineViewModel line, ModerationAction action) =>
        ModerationCommand.For(line.Message, action) with
        {
            TargetLogin = line.Login,
            Reason = action is ModerationAction.Delete || ModReason.Trim().Length == 0 ? null : ModReason.Trim(),
        };

    /// <summary>Runs a command as the streamer; opens the website for website tools. Null when moderation is unavailable.</summary>
    private async Task<ModerationResult?> RunAsync(ModerationCommand command)
    {
        if (_moderation is null)
        {
            Status = $"{command.Platform} has no moderation support";
            return null;
        }
        var result = await _moderation.ExecuteAsync(command, ModActor.Streamer);
        if (result.WebsiteUrl is { } url && result.Result == Cuebot.Core.Chat.Moderation.ModResult.Skipped)
        {
            (_services.OpenUrl ?? Cuebot.App.Shell.UrlLauncher.Open)(url);
            Status = result.Message;
            return result;
        }
        Status = result.Message;
        return result;
    }

    // ---------------------------------------------------------------- mod log

    private const int MaxLogLines = 200;

    /// <summary>The mod log, newest first (empty without one).</summary>
    public ObservableCollection<ModLogLineViewModel> ModLogLines { get; } = [];

    /// <summary>The selected person's rows in the mod log, newest first.</summary>
    public ObservableCollection<ModLogLineViewModel> UserLogLines { get; } = [];

    public bool HasModLog => _moderation?.Log is not null;
    public bool HasModLogLines => ModLogLines.Count > 0;
    public bool HasUserLog => UserLogLines.Count > 0;

    private void AddLogLine(ModLogEntry entry)
    {
        var existing = ModLogLines.FirstOrDefault(l => l.Entry.Id == entry.Id);
        if (existing is not null) ModLogLines[ModLogLines.IndexOf(existing)] = LogLine(entry);
        else ModLogLines.Insert(0, LogLine(entry));
        while (ModLogLines.Count > MaxLogLines) ModLogLines.RemoveAt(ModLogLines.Count - 1);
        OnPropertyChanged(nameof(HasModLogLines));
        if (SelectedLine is { } line && entry.Platform == line.Platform && entry.TargetUserId == line.Message.UserId) RefreshUserLog();
    }

    private void RefreshUserLog()
    {
        UserLogLines.Clear();
        if (_moderation?.Log is { } log && SelectedLine is { Message.UserId.Length: > 0 } line)
            foreach (var entry in log.ForTarget(line.Platform, line.Message.UserId, 30)) UserLogLines.Add(LogLine(entry));
        OnPropertyChanged(nameof(HasUserLog));
    }

    /// <summary>Marks lines the platform reports deleted, and the author's lines when they were banned or timed out.</summary>
    private void ApplyNotice(ChatModerationNotice notice)
    {
        foreach (var line in Messages.Where(l => string.Equals(l.Platform, notice.Platform, StringComparison.OrdinalIgnoreCase)))
        {
            if (notice.Kind == ChatModerationKind.MessageDeleted)
            {
                if (notice.MessageId.Length > 0 && line.Message.Id == notice.MessageId) line.IsDeleted = true;
            }
            else if (notice.UserId.Length > 0 && line.Message.UserId == notice.UserId)
            {
                line.ActionNote = notice.Kind == ChatModerationKind.UserBanned ? "banned"
                    : notice.DurationSeconds is { } s ? "timed out for " + ModerationService.FormatDuration(TimeSpan.FromSeconds(s)) : "timed out";
            }
        }
        RefreshModeration();
    }

    /// <summary>"suspicious: restricted (possible ban evader)" for a flagged chatter; null otherwise.</summary>
    private string? SuspiciousNote(string platform, string userId)
    {
        if (userId.Length == 0 || _moderation?.QueuesFor(platform)?.SuspiciousUser(userId) is not { Level: not SuspiciousLevel.None } flagged)
            return null;
        var level = flagged.Level == SuspiciousLevel.Restricted ? "restricted" : "monitored";
        return $"suspicious: {level}" + (flagged.Why.Length > 0 ? $" ({flagged.Why})" : "");
    }

    /// <summary>Re-flags the lines of a chatter whose suspicious status changed.</summary>
    private void OnQueueChanged(ModerationQueueChange change)
    {
        if (change.Kind != ModerationQueueKind.SuspiciousUsers || change.ItemId.Length == 0) return;
        var note = SuspiciousNote(change.Platform, change.ItemId);
        foreach (var line in Messages.Where(l => l.Message.UserId == change.ItemId
                                                 && string.Equals(l.Platform, change.Platform, StringComparison.OrdinalIgnoreCase)))
            line.SuspiciousNote = note;
    }

    /// <summary>Timeout and Ban make no sense for the channel's own account (the platforms refuse them).</summary>
    private static bool CanPunish(ChatLineViewModel line) => !line.Message.Roles.HasFlag(ChatRoles.Broadcaster);

    /// <summary>Ban asks once more in the pane ("Confirm ban"); the message menu asks through a submenu.</summary>
    [ObservableProperty] private bool _confirmingBan;

    private void RefreshUserLines()
    {
        UserLines.Clear();
        if (SelectedLine is not { } line) return;
        foreach (var m in Messages.Reverse().Where(m => m.Platform == line.Platform && m.Login == line.Login).Take(20))
            UserLines.Add(m);
    }

    [RelayCommand(CanExecute = nameof(CanDeleteSelected))]
    private Task DeleteSelected() => SelectedLine is { } line ? DeleteAsync(line) : Task.CompletedTask;

    [RelayCommand]
    private Task TimeoutSelected(string minutes) =>
        SelectedLine is { } line ? TimeoutAsync(line, TimeSpan.FromMinutes(double.Parse(minutes, System.Globalization.CultureInfo.InvariantCulture))) : Task.CompletedTask;


    [RelayCommand]
    private async Task BanSelected()
    {
        if (SelectedLine is not { } line) return;
        if (IsLinked(line))
        {
            // The linked dialog shows the preview and asks again for bans on more than one platform.
            ConfirmingBan = false;
            await BanAsync(line);
            return;
        }
        if (!ConfirmingBan)
        {
            ConfirmingBan = true;
            return;
        }
        ConfirmingBan = false;
        await BanAsync(line);
    }

    [RelayCommand]
    private void ReplyToSelected()
    {
        if (SelectedLine is not { } line) return;
        SendTarget = SendTargets.FirstOrDefault(t => string.Equals(t, line.Platform, StringComparison.OrdinalIgnoreCase)) ?? SendTarget;
        Draft = "@" + line.User + " " + Draft.TrimStart();
    }

    // ---------------------------------------------------------------- activity filter

    /// <summary>Activity filter chips: All, then the kinds that can happen.</summary>
    public IReadOnlyList<string> FeedFilters { get; } = ["All", "Follows", "Subs", "Tips", "Raids", "Redemptions", "Stream", "Other"];

    [ObservableProperty] private string _feedFilter = "All";

    /// <summary>The activity feed under the current filter (<see cref="Feed"/> keeps everything).</summary>
    public ObservableCollection<FeedLineViewModel> VisibleFeed { get; } = [];

    public bool HasVisibleFeed => VisibleFeed.Count > 0;

    partial void OnFeedFilterChanged(string value) => RefilterFeed();

    private void RefilterFeed()
    {
        VisibleFeed.Clear();
        foreach (var line in Feed.Where(InFeedFilter)) VisibleFeed.Add(line);
        OnPropertyChanged(nameof(HasVisibleFeed));
    }

    internal static string FeedGroup(FeedKind kind) => kind switch
    {
        FeedKind.Follow => "Follows",
        FeedKind.Subscription or FeedKind.GiftSubscription => "Subs",
        FeedKind.Tip => "Tips",
        FeedKind.Raid => "Raids",
        FeedKind.Redemption => "Redemptions",
        FeedKind.StreamOnline or FeedKind.StreamOffline => "Stream",
        _ => "Other",
    };

    private bool InFeedFilter(FeedLineViewModel line) => FeedFilter == "All" || FeedGroup(line.Item.Kind) == FeedFilter;

    partial void OnDraftChanged(string value) => UpdateSuggestions();

    partial void OnSendTargetChanged(string value)
    {
        UpdateSuggestions();
        RefreshSendAs();
    }

    // ---------------------------------------------------------------- send as

    public const string SendAsDefault = "Default";
    public const string SendAsBroadcaster = "Broadcaster";
    public const string SendAsBot = "Bot";

    /// <summary>Default (each platform's own setting), Broadcaster, and Bot where a bot account is signed in.</summary>
    public ObservableCollection<string> SendAsOptions { get; } = [SendAsDefault, SendAsBroadcaster];

    [ObservableProperty] private string _sendAs = SendAsDefault;

    private void RefreshSendAs()
    {
        var bot = _hub.CanSendAsBot(SendTarget);
        if (bot && !SendAsOptions.Contains(SendAsBot)) SendAsOptions.Add(SendAsBot);
        if (!bot && SendAsOptions.Contains(SendAsBot))
        {
            if (SendAs == SendAsBot) SendAs = SendAsDefault;
            SendAsOptions.Remove(SendAsBot);
        }
    }

    private ChatSendOptions SendOptions => SendAs switch
    {
        SendAsBot => new ChatSendOptions(Bot: true),
        SendAsBroadcaster => new ChatSendOptions(Bot: false),
        _ => ChatSendOptions.Default,
    };

    private bool Visible(ChatMessage m) =>
        Filter == ChatHub.AllPlatforms || string.Equals(m.Platform, Filter, StringComparison.OrdinalIgnoreCase);

    private void AddLine(ChatMessage message)
    {
        _all.Add(message);
        AddPlatformChip(message.Platform);
        if (_all.Count > MaxLines) _all.RemoveAt(0);
        if (!Visible(message)) return;
        var line = CreateLine(message);
        Messages.Add(line);
        while (Messages.Count > MaxLines) Messages.RemoveAt(0);
        if (SelectedLine is { } selected && selected.Platform == line.Platform && selected.Login == line.Login) UserLines.Insert(0, line);
        LineAdded?.Invoke();
    }

    private ChatLineViewModel CreateLine(ChatMessage message)
    {
        var raw = _services.Emotes?.ArgsFor(message.Id);
        var line = new ChatLineViewModel(message, Segment(message, raw), ChatHighlighter.Evaluate(message, raw, _settings.Highlights), raw)
        {
            IsDeleted = _hub.IsDeleted(message),
            SuspiciousNote = SuspiciousNote(message.Platform, message.UserId),
        };
        LoadImages(line);
        LookUpPronouns(line);
        return line;
    }

    private IReadOnlyList<ChatSegment> Segment(ChatMessage message, IReadOnlyDictionary<string, object?>? raw)
    {
        if (!_settings.ShowEmotes || _services.Emotes is not { } emotes) return [new TextSegment(message.Text)];
        return ChatSegmenter.Build(message.Text, emotes.FindEmotes(message.Platform, message.Text, raw));
    }

    private void LoadImages(ChatLineViewModel line)
    {
        foreach (var emote in line.Emotes) emote.Load(_services.Images, OnUi);
    }

    private void LookUpPronouns(ChatLineViewModel line)
    {
        if (!_settings.ShowPronouns || _services.Pronouns is not { } pronouns
            || !string.Equals(line.Platform, EmoteProviders.Twitch, StringComparison.OrdinalIgnoreCase))
        {
            line.Pronouns = null;
            return;
        }
        var (known, cached) = pronouns.TryGetCached(line.Login);
        if (known)
        {
            line.Pronouns = cached?.Display;
            return;
        }
        _ = pronouns.GetAsync(line.Login).ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result is { } found) OnUi(() => line.Pronouns = found.Display);
        }, TaskScheduler.Default);
    }

    /// <summary>Re-renders emotes after a set loads or changes (so earlier lines pick up new emotes).</summary>
    private void RefreshEmotes()
    {
        foreach (var line in Messages)
        {
            line.Segments = Segment(line.Message, line.RawArgs);
            LoadImages(line);
        }
        if (IsSuggesting) UpdateSuggestions();
    }

    private void OnOptionsChanged()
    {
        foreach (var line in Messages)
        {
            line.Segments = Segment(line.Message, line.RawArgs);
            line.Highlight = ChatHighlighter.Evaluate(line.Message, line.RawArgs, _settings.Highlights);
            LoadImages(line);
            LookUpPronouns(line);
        }
        RebuildQuickActions();
    }

    private void FollowActionRename(string oldName, string newName)
    {
        var renamed = _settings.QuickActions.Where(q => string.Equals(q.ActionName.Trim(), oldName.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (renamed.Length == 0) return;
        foreach (var quick in renamed) quick.ActionName = newName;
        _services.Settings?.ScheduleSave();
        RebuildQuickActions();
    }

    private void RebuildQuickActions()
    {
        QuickActions.Clear();
        foreach (var q in _settings.QuickActions.Where(q => q.ActionName.Length > 0)) QuickActions.Add(new QuickActionButtonViewModel(q, this));
        OnPropertyChanged(nameof(HasQuickActions));
    }

    private void AddFeed(FeedItem item)
    {
        var line = new FeedLineViewModel(item);
        Feed.Insert(0, line);
        while (Feed.Count > MaxLines) Feed.RemoveAt(Feed.Count - 1);
        if (InFeedFilter(line)) VisibleFeed.Insert(0, line);
        while (VisibleFeed.Count > MaxLines) VisibleFeed.RemoveAt(VisibleFeed.Count - 1);
        OnPropertyChanged(nameof(HasVisibleFeed));
    }

    // ---------------------------------------------------------------- send box

    [RelayCommand]
    private async Task Send()
    {
        var text = Draft.Trim();
        if (text.Length == 0) return;
        IsSuggesting = false;
        if (SlashCommand.Parse(text) is { } command)
        {
            await RunSlashCommand(command);
            return;
        }
        try
        {
            RefreshSendAs();
            var sent = await _hub.SendAsync(SendTarget, SlashCommand.Unescape(text), SendOptions);
            Draft = "";
            Status = $"Sent to {string.Join(", ", sent)}" + (SendAs == SendAsDefault ? "" : $" as {SendAs.ToLowerInvariant()}");
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

    private async Task RunSlashCommand(SlashCommand command)
    {
        switch (command.Name)
        {
            case "clear":
                ClearView();
                Draft = "";
                Status = "Chat view cleared";
                break;
            case "help":
                Status = string.Join("   ", SlashCommand.Known.Select(k => k.Usage));
                break;
            case "action":
                await RunActionCommand(command.Argument);
                break;
            case "announce":
                await Announce(command.Argument, ChatAnnouncementKind.Announcement);
                break;
            case "me":
                await Announce(command.Argument, ChatAnnouncementKind.Action);
                break;
            default:
                Status = $"Unknown command /{command.Name}. Type /help for the list, or // to send a slash.";
                break;
        }
    }

    private async Task RunActionCommand(string argument)
    {
        if (_services.Engine is not { } engine)
        {
            Status = "Actions are not available here";
            return;
        }
        if (argument.Length == 0)
        {
            Status = "Usage: /action <name> [input]";
            return;
        }
        if (SlashCommand.SplitAction(argument, n => engine.FindAction(n) is not null) is not { } split)
        {
            Status = $"No action named '{argument}'";
            return;
        }
        var args = SelectionArgs(SelectedLine, includeMessage: true);
        args["platform"] = SelectedLine?.Platform ?? SendTarget;
        args["rawInput"] = split.Input;
        var words = split.Input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++) args[$"input{i}"] = words[i];
        args["source"] = "chat";
        Draft = "";
        var ok = await engine.RunByNameAsync(split.Name, args);
        Status = ok ? $"Ran action '{split.Name}'" : $"Action '{split.Name}' did not complete";
    }

    private async Task Announce(string text, ChatAnnouncementKind kind)
    {
        var verb = kind == ChatAnnouncementKind.Action ? "/me" : "/announce";
        if (text.Length == 0)
        {
            Status = $"Usage: {verb} <text>";
            return;
        }
        var announcers = _services.Moderation?.AnnouncersFor(SendTarget, kind) ?? [];
        if (announcers.Count == 0)
        {
            Status = $"{verb} is not supported on {(SendTarget == ChatHub.AllPlatforms ? "any connected platform" : SendTarget)}";
            return;
        }
        var done = new List<string>();
        var errors = new List<string>();
        foreach (var a in announcers)
        {
            try
            {
                await a.AnnounceAsync(text, kind);
                done.Add(a.Platform);
            }
            catch (Exception ex)
            {
                errors.Add($"{a.Platform}: {ex.Message}");
            }
        }
        if (done.Count > 0) Draft = "";
        var parts = new List<string>();
        if (done.Count > 0) parts.Add($"Posted to {string.Join(", ", done)}");
        parts.AddRange(errors);
        Status = string.Join("; ", parts);
    }

    /// <summary>Clears the chat view only (the /clear command).</summary>
    public void ClearView()
    {
        _all.Clear();
        Messages.Clear();
        SelectedLine = null;
    }

    [RelayCommand]
    private void ClearFeed()
    {
        Feed.Clear();
        RefilterFeed();
    }

    [RelayCommand]
    private void ToggleOptions() => ShowOptions = !ShowOptions;

    // ---------------------------------------------------------------- emote autocomplete

    /// <summary>The ":partial" word being typed at the end of the draft, if any (at least one character after ':').</summary>
    internal static (int Start, string Prefix)? AutocompleteToken(string draft)
    {
        if (draft.Length < 2) return null;
        var start = draft.LastIndexOfAny([' ', '\t', '\n']) + 1;
        var word = draft[start..];
        if (word.Length < 2 || word[0] != ':' || word.IndexOf(':', 1) >= 0) return null;
        return (start, word[1..]);
    }

    private void UpdateSuggestions()
    {
        Suggestions.Clear();
        if (_services.Emotes is { } emotes && AutocompleteToken(Draft) is { } token)
            foreach (var emote in emotes.Search(SendTarget, token.Prefix, MaxSuggestions))
            {
                var suggestion = new EmoteSuggestionViewModel(emote);
                suggestion.Load(_services.Images, OnUi);
                Suggestions.Add(suggestion);
            }
        IsSuggesting = Suggestions.Count > 0;
        SelectedSuggestion = Suggestions.FirstOrDefault();
    }

    /// <summary>Moves the highlighted suggestion (Up/Down keys), wrapping around.</summary>
    public void MoveSuggestion(int delta)
    {
        if (Suggestions.Count == 0) return;
        var index = SelectedSuggestion is null ? -1 : Suggestions.IndexOf(SelectedSuggestion);
        index = ((index + delta) % Suggestions.Count + Suggestions.Count) % Suggestions.Count;
        SelectedSuggestion = Suggestions[index];
    }

    /// <summary>Replaces the ":partial" word with the emote code and a trailing space. False when nothing was offered.</summary>
    public bool AcceptSuggestion(EmoteSuggestionViewModel? suggestion = null)
    {
        suggestion ??= SelectedSuggestion;
        if (suggestion is null || AutocompleteToken(Draft) is not { } token) return false;
        Draft = Draft[..token.Start] + suggestion.Emote.Code + " ";
        IsSuggesting = false;
        return true;
    }

    [RelayCommand]
    private void PickSuggestion(EmoteSuggestionViewModel? suggestion) => AcceptSuggestion(suggestion);

    public void CloseSuggestions() => IsSuggesting = false;

    // ---------------------------------------------------------------- message actions and quick actions

    /// <summary>The actions menu for a line: moderation the platform offers now, then quick actions.</summary>
    public IReadOnlyList<ChatMenuEntry> GetMenu(ChatLineViewModel line)
    {
        var entries = new List<ChatMenuEntry>();
        if (Runs(line, ModerationAction.Delete) && line.CanDelete && line.Message.Id.Length > 0)
            entries.Add(new("Delete message", () => DeleteAsync(line)));
        if (Runs(line, ModerationAction.Timeout) && CanPunish(line))
        {
            entries.Add(new(TimeoutLabel(line, TimeSpan.FromMinutes(1), "Timeout 1 minute"), () => TimeoutAsync(line, TimeSpan.FromMinutes(1))));
            entries.Add(new(TimeoutLabel(line, TimeSpan.FromMinutes(10), "Timeout 10 minutes"), () => TimeoutAsync(line, TimeSpan.FromMinutes(10))));
        }
        if (Runs(line, ModerationAction.Ban) && CanPunish(line))
            entries.Add(new($"Ban {line.User}…", () => BanAsync(line), $"Yes, ban {line.User}"));

        var quick = _settings.QuickActions.Where(q => q.ActionName.Length > 0).ToArray();
        if (quick.Length > 0 && entries.Count > 0) entries.Add(ChatMenuEntry.Separator);
        foreach (var q in quick)
            entries.Add(new(q.Label.Length > 0 ? q.Label : q.ActionName, () => RunQuickActionAsync(q, line)));

        if (entries.Count > 0) entries.Add(ChatMenuEntry.Separator);
        entries.Add(new("Copy message to send box", () =>
        {
            Draft = line.Text;
            return Task.CompletedTask;
        }));
        return entries;
    }

    /// <summary>The menu label, with what the platform will really apply when it differs ("Timeout 1 minute (Rumble: 5 min...)").</summary>
    private string TimeoutLabel(ChatLineViewModel line, TimeSpan duration, string label) =>
        _moderation?.PreviewDuration(line.Platform, duration).Note is { Length: > 0 } note ? $"{label} ({note})" : label;

    public async Task DeleteAsync(ChatLineViewModel line)
    {
        if (await RunAsync(Command(line, ModerationAction.Delete)) is { Ok: true }) line.IsDeleted = true;
        RefreshModeration();
    }

    /// <summary>Times the author out; when their account is linked, opens the linked action dialog with the preview instead.</summary>
    public Task TimeoutAsync(ChatLineViewModel line, TimeSpan duration) => IsLinked(line)
        ? OpenLinkedAsync(Command(line, ModerationAction.Timeout) with { Duration = duration })
        : RunAsync(Command(line, ModerationAction.Timeout) with { Duration = duration });

    /// <summary>Bans the author; when their account is linked, opens the linked action dialog with the preview instead.</summary>
    public Task BanAsync(ChatLineViewModel line) => IsLinked(line)
        ? OpenLinkedAsync(Command(line, ModerationAction.Ban))
        : RunAsync(Command(line, ModerationAction.Ban));

    /// <summary>Runs a quick action with the line's arguments (message and/or user, by scope).</summary>
    public async Task RunQuickActionAsync(QuickActionDefinition quick, ChatLineViewModel? line)
    {
        if (_services.Engine is not { } engine)
        {
            Status = "Actions are not available here";
            return;
        }
        if (quick.Scope != QuickActionScope.Global && line is null)
        {
            Status = "Select a message first";
            return;
        }
        var args = SelectionArgs(line, includeMessage: quick.Scope != QuickActionScope.User);
        args["quickAction"] = quick.Label;
        args["quickActionScope"] = quick.Scope.ToString();
        args["source"] = "chat";
        var ok = await engine.RunByNameAsync(quick.ActionName, args);
        Status = ok ? $"Ran '{quick.ActionName}'" : $"Action '{quick.ActionName}' did not complete (see log)";
    }

    /// <summary>Arguments describing a selected line: platform, user, userName, userId (+ message, messageId, rawInput).</summary>
    internal static Dictionary<string, object?> SelectionArgs(ChatLineViewModel? line, bool includeMessage)
    {
        var args = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        if (line is null) return args;
        args["platform"] = line.Platform;
        args["user"] = line.User;
        args["userName"] = line.Login;
        args["userId"] = line.Message.UserId;
        if (!includeMessage) return args;
        args["message"] = line.Text;
        args["messageId"] = line.Message.Id;
        args["rawInput"] = line.Text;
        return args;
    }
}
