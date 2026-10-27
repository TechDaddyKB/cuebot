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

    /// <summary>Set after a successful delete from the actions menu.</summary>
    [ObservableProperty] private bool _isDeleted;

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
public sealed record ChatMenuEntry(string Label, Func<Task>? Run)
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

    public ChatViewModel(ChatHub hub, ChatPageServices? services = null) : base("Chat")
    {
        _hub = hub;
        _services = services ?? new ChatPageServices();
        _settings = _services.Settings?.Value ?? new ChatPageSettings();
        PlatformFilters = [ChatHub.AllPlatforms, .. hub.Platforms];
        SendTargets = [ChatHub.AllPlatforms, .. hub.Platforms];
        Options = new ChatOptionsViewModel(_settings, () => _services.Settings?.ScheduleSave(), _services.Emotes,
            _services.EmoteSettings, _services.ActionNames ?? (() => []));
        Options.Changed += OnOptionsChanged;
        RebuildQuickActions();

        foreach (var message in hub.RecentMessages()) AddLine(message);
        foreach (var item in hub.RecentFeed()) Feed.Insert(0, new FeedLineViewModel(item));
        // Always queued (even on the UI thread): the hub raises this while the platform event is still being
        // published, before the emote service has indexed that event's emote arguments.
        hub.MessageReceived += m => Avalonia.Threading.Dispatcher.UIThread.Post(() => AddLine(m));
        hub.FeedItemAdded += f => OnUi(() => AddFeed(f));
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
    public IReadOnlyList<string> SendTargets { get; }
    public ChatOptionsViewModel Options { get; }
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
        Messages.Clear();
        foreach (var m in _all.Where(Visible)) Messages.Add(CreateLine(m));
    }

    partial void OnSelectedLineChanged(ChatLineViewModel? value)
    {
        foreach (var q in QuickActions) q.Refresh();
    }

    partial void OnDraftChanged(string value) => UpdateSuggestions();

    partial void OnSendTargetChanged(string value) => UpdateSuggestions();

    private bool Visible(ChatMessage m) =>
        Filter == ChatHub.AllPlatforms || string.Equals(m.Platform, Filter, StringComparison.OrdinalIgnoreCase);

    private void AddLine(ChatMessage message)
    {
        _all.Add(message);
        if (_all.Count > MaxLines) _all.RemoveAt(0);
        if (!Visible(message)) return;
        Messages.Add(CreateLine(message));
        while (Messages.Count > MaxLines) Messages.RemoveAt(0);
        LineAdded?.Invoke();
    }

    private ChatLineViewModel CreateLine(ChatMessage message)
    {
        var raw = _services.Emotes?.ArgsFor(message.Id);
        var line = new ChatLineViewModel(message, Segment(message, raw), ChatHighlighter.Evaluate(message, raw, _settings.Highlights), raw);
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

    private void RebuildQuickActions()
    {
        QuickActions.Clear();
        foreach (var q in _settings.QuickActions.Where(q => q.ActionName.Length > 0)) QuickActions.Add(new QuickActionButtonViewModel(q, this));
        OnPropertyChanged(nameof(HasQuickActions));
    }

    private void AddFeed(FeedItem item)
    {
        Feed.Insert(0, new FeedLineViewModel(item));
        while (Feed.Count > MaxLines) Feed.RemoveAt(Feed.Count - 1);
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
            var sent = await _hub.SendAsync(SendTarget, SlashCommand.Unescape(text));
            Draft = "";
            Status = $"Sent to {string.Join(", ", sent)}";
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
    private void ClearFeed() => Feed.Clear();

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
        var available = _services.Moderation?.AvailableFor(line.Platform) ?? ChatModerationActions.None;
        if (available.HasFlag(ChatModerationActions.DeleteMessage) && !line.IsDeleted && line.Message.Id.Length > 0)
            entries.Add(new("Delete message", () => DeleteAsync(line)));
        if (available.HasFlag(ChatModerationActions.Timeout))
        {
            entries.Add(new("Timeout 1 minute", () => TimeoutAsync(line, TimeSpan.FromMinutes(1))));
            entries.Add(new("Timeout 10 minutes", () => TimeoutAsync(line, TimeSpan.FromMinutes(10))));
        }
        if (available.HasFlag(ChatModerationActions.Ban)) entries.Add(new($"Ban {line.User}", () => BanAsync(line)));

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

    public Task DeleteAsync(ChatLineViewModel line) =>
        Moderate(line, async m =>
        {
            await m.DeleteMessageAsync(line.Message.Id);
            line.IsDeleted = true;
        }, $"Deleted {line.User}'s message");

    public Task TimeoutAsync(ChatLineViewModel line, TimeSpan duration) =>
        Moderate(line, m => m.TimeoutAsync(line.Message.UserId, line.Login, duration, null),
            $"Timed out {line.User} for {duration.TotalMinutes:0} min");

    public Task BanAsync(ChatLineViewModel line) =>
        Moderate(line, m => m.BanAsync(line.Message.UserId, line.Login, null), $"Banned {line.User}");

    private async Task Moderate(ChatLineViewModel line, Func<IChatModerator, Task> action, string done)
    {
        if (_services.Moderation?.ModeratorFor(line.Platform) is not { } moderator)
        {
            Status = $"{line.Platform} has no moderation support";
            return;
        }
        try
        {
            await action(moderator);
            Status = done;
        }
        catch (Exception ex)
        {
            Status = ex.Message;
        }
    }

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
