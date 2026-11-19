using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.App.ViewModels.Chat;
using Cuebot.Core.Chat;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Users;

namespace Cuebot.App.ViewModels;

/// <summary>
/// Identity links on the Chat page (docs/design/UNIFIED_MOD_CHAT_PLAN.md, phases 4 and 5): the user card's linked accounts,
/// suggestions, <c>!link</c> verification, confirm, reject and unlink, notes and same-name hints; the linked ban and
/// timeout dialog; "Undo incident"; the later-arrival strip. All of it only while the streamer has turned identity links
/// on (Chat options); otherwise nothing here shows and Ban and Timeout work on the one account as before.
/// </summary>
public sealed partial class ChatViewModel
{
    /// <summary>Shown above the notes box (section 9.2: notes are free text).</summary>
    public const string NoteWarning =
        "Write only what helps moderate: what happened and when. Never record health, religion or beliefs, politics, sexuality, " +
        "ethnicity or anything similar. Notes are kept 24 months after their last edit.";

    private IdentityLinks? Links => _moderation?.Links;

    /// <summary>Identity links are on (the streamer's setting) and available.</summary>
    public bool LinksOn => _moderation?.LinksEnabled == true;

    /// <summary>The selected chatter can be linked: links on, a real platform user id, not the broadcaster.</summary>
    public bool ShowLinkTools => LinksOn && SelectedLine is { Message.UserId.Length: > 0 } line && CanPunish(line);

    public ObservableCollection<LinkedAccountRowViewModel> LinkedAccounts { get; } = [];
    public bool HasLinkedAccounts => LinkedAccounts.Count > 0;

    public ObservableCollection<NoteRowViewModel> Notes { get; } = [];
    public bool HasNotes => Notes.Count > 0;

    public ObservableCollection<NameHintViewModel> NameHints { get; } = [];
    public bool HasNameHints => NameHints.Count > 0;

    public ObservableCollection<LaterArrivalViewModel> LaterArrivals { get; } = [];
    public bool HasLaterArrivals => LaterArrivals.Count > 0;

    [ObservableProperty] private string _noteDraft = "";

    /// <summary>What the last link operation said.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkStatus))]
    private string _linkStatus = "";

    public bool HasLinkStatus => LinkStatus.Length > 0;

    // ---- "Same person as…"

    [ObservableProperty] private bool _showProposeForm;
    [ObservableProperty] private string _proposePlatform = "";
    [ObservableProperty] private string _proposeName = "";

    /// <summary>A quote that shows it, like the viewer saying "I'm X on Twitch".</summary>
    [ObservableProperty] private string _proposeEvidence = "";

    [ObservableProperty] private string _proposeNote = "";

    /// <summary>The other account already belongs to a person: merge or move.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkConflict))]
    private string _linkConflict = "";

    public bool HasLinkConflict => LinkConflict.Length > 0;

    /// <summary>The platforms a link can go to: every chat platform but the selected one's.</summary>
    public IReadOnlyList<string> LinkPlatforms =>
        PlatformFilters.Where(p => p != ChatHub.AllPlatforms && !string.Equals(p, SelectedLine?.Platform, StringComparison.OrdinalIgnoreCase)).ToArray();

    /// <summary>The code and what to tell the viewer, after "Verify with !link".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVerification))]
    private string _verificationText = "";

    public bool HasVerification => VerificationText.Length > 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPendingUnlink))]
    private UnlinkViewModel? _pendingUnlink;

    public bool HasPendingUnlink => PendingUnlink is not null;

    /// <summary>The linked ban or timeout dialog, while open.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkedAction))]
    private LinkedActionViewModel? _linkedAction;

    public bool HasLinkedAction => LinkedAction is not null;

    // ---- the last linked incident, with Undo for 24 hours

    private (string Id, DateTimeOffset At)? _lastIncident;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastIncident))]
    private string _lastIncidentText = "";

    public bool HasLastIncident => LastIncidentText.Length > 0 && _lastIncident is { } i && _moderation?.UndoStillOffered(i.At) == true;

    private void WireLinks()
    {
        if (_moderation is null) return;
        if (Links is { } links) links.Changed += _ => OnUi(RefreshLinks);
        _moderation.LaterArrivalsChanged += () => OnUi(RefreshLaterArrivals);
        Options.ModerationChanged += () =>
        {
            RefreshLinks();
            RefreshLaterArrivals();
        };
        RefreshLaterArrivals();
    }

    /// <summary>Rebuilds the user card's link section for the selected chatter.</summary>
    public void RefreshLinks()
    {
        LinkedAccounts.Clear();
        Notes.Clear();
        NameHints.Clear();
        OnPropertyChanged(nameof(LinksOn));
        OnPropertyChanged(nameof(ShowLinkTools));
        OnPropertyChanged(nameof(LinkPlatforms));
        if (!ShowLinkTools || Links is not { } links || SelectedLine is not { } line)
        {
            ShowProposeForm = false;
            LinkConflict = "";
            VerificationText = "";
            PendingUnlink = null;
            NotifyLinkLists();
            return;
        }
        var platform = line.Platform;
        var userId = line.Message.UserId;
        if (links.PersonOf(platform, userId) is { } person && person.Account(Core.Users.LinkPlatforms.Canon(platform), userId) is { } me)
            foreach (var account in person.Accounts.OrderByDescending(a => a.LinkId == me.LinkId))
                LinkedAccounts.Add(new LinkedAccountRowViewModel(account, account.LinkId == me.LinkId ? me.Status : IdentityLinks.Between(me, account),
                    account.LinkId == me.LinkId, ActOnLinkAsync));
        foreach (var note in links.NotesFor(platform, userId))
            Notes.Add(new NoteRowViewModel(note, !Core.Users.IdentityLinks.SamePlatform(note.Platform, platform) || note.UserId != userId, DeleteNoteAsync));
        foreach (var hint in links.SameNameHints(platform, userId, line.User))
            NameHints.Add(new NameHintViewModel(hint, UseHint));
        if (ProposePlatform.Length == 0 || !LinkPlatforms.Contains(ProposePlatform)) ProposePlatform = LinkPlatforms.FirstOrDefault() ?? "";
        NotifyLinkLists();
    }

    private void NotifyLinkLists()
    {
        OnPropertyChanged(nameof(HasLinkedAccounts));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(HasNameHints));
    }

    private void UseHint(NameHintViewModel hint)
    {
        ShowProposeForm = true;
        ProposePlatform = LinkPlatforms.FirstOrDefault(p => Core.Users.IdentityLinks.SamePlatform(p, hint.Account.Platform)) ?? hint.Account.Platform;
        ProposeName = hint.Account.UserName;
    }

    [RelayCommand]
    private void ToggleProposeForm()
    {
        ShowProposeForm = !ShowProposeForm;
        LinkConflict = "";
    }

    [RelayCommand]
    private Task Propose() => ProposeAsync(LinkConflictChoice.Ask);

    [RelayCommand]
    private Task MergeLink() => ProposeAsync(LinkConflictChoice.Merge);

    [RelayCommand]
    private Task MoveLink() => ProposeAsync(LinkConflictChoice.Move);

    private async Task ProposeAsync(LinkConflictChoice choice)
    {
        if (Links is not { } links || SelectedLine is not { } line) return;
        var name = ProposeName.Trim().TrimStart('@');
        // The other account comes from the chat Cuebot has seen: its platform user id is the stable key.
        var other = _all.LastOrDefault(m => string.Equals(m.Platform, ProposePlatform, StringComparison.OrdinalIgnoreCase) && m.UserId.Length > 0
                                            && string.Equals(m.User, name, StringComparison.OrdinalIgnoreCase));
        if (other is null)
        {
            LinkStatus = $"Nobody called \"{name}\" has chatted on {ProposePlatform} recently. Pick someone who is in the chat.";
            return;
        }
        var evidence = ProposeEvidence.Trim().Length > 0 ? new[] { new LinkEvidenceInput(LinkEvidenceKinds.ViewerSaid, ProposeEvidence.Trim()) } : [];
        var result = await links.ProposeAsync(new ViewerRef(line.Platform, line.Message.UserId, line.User),
            new ViewerRef(other.Platform, other.UserId, other.User), ModActor.Streamer, ProposeNote, evidence, choice,
            line.Message.Roles, other.Roles);
        if (result.NeedsChoice)
        {
            LinkConflict = result.Message + " It is linked to: " +
                           string.Join(", ", result.Conflict!.Accounts.Select(a => $"{a.Name} on {a.Platform}"));
            return;
        }
        LinkStatus = result.Message;
        if (!result.Ok) return;
        LinkConflict = "";
        ShowProposeForm = false;
        ProposeName = ProposeEvidence = ProposeNote = "";
        RefreshLinks();
    }

    [RelayCommand]
    private async Task StartVerification()
    {
        if (Links is not { } links || SelectedLine is not { } line) return;
        var (verification, message) = await links.StartVerificationAsync(new ViewerRef(line.Platform, line.Message.UserId, line.User),
            ModActor.Streamer, line.Message.Roles);
        VerificationText = verification is null ? "" : $"Code {verification.Code}. {message}";
        LinkStatus = verification is null ? message : "";
    }

    private async Task ActOnLinkAsync(LinkedAccountRowViewModel row, string what)
    {
        if (Links is not { } links || _moderation is null) return;
        switch (what)
        {
            case "confirm":
                LinkStatus = links.Confirm(row.Account.LinkId, ModActor.Streamer).Message;
                break;
            case "reject":
                LinkStatus = links.Reject(row.Account.LinkId, ModActor.Streamer).Message;
                break;
            default:
                PendingUnlink = new UnlinkViewModel(_moderation, row, ConfirmUnlinkAsync, () => PendingUnlink = null);
                return;
        }
        RefreshLinks();
        await Task.CompletedTask;
    }

    private async Task ConfirmUnlinkAsync(UnlinkViewModel unlink)
    {
        if (_moderation is null) return;
        var (result, reversed) = await _moderation.UnlinkAsync(unlink.Row.Account.LinkId, ModActor.Streamer, unlink.Dissolve, unlink.MarkedWrong,
            unlink.Reason, unlink.Reversible.Where(r => r.Reverse).Select(r => r.Entry.Id).ToArray());
        LinkStatus = result.Message + (reversed is null ? "" : " " + reversed.Summary);
        PendingUnlink = null;
        RefreshLinks();
    }

    [RelayCommand]
    private void AddNote()
    {
        if (Links is not { } links || SelectedLine is not { } line) return;
        var (note, message) = links.AddNote(new ViewerRef(line.Platform, line.Message.UserId, line.User), NoteDraft, ModActor.Streamer);
        LinkStatus = note is null ? message : "";
        if (note is not null) NoteDraft = "";
        RefreshLinks();
    }

    private Task DeleteNoteAsync(NoteRowViewModel note)
    {
        if (Links is { } links) LinkStatus = links.DeleteNote(note.Note.Id, ModActor.Streamer).Message;
        RefreshLinks();
        return Task.CompletedTask;
    }

    // ---------------------------------------------------------------- linked ban and timeout

    /// <summary>The account belongs to a person with other accounts (and links are on): Ban and Timeout open the dialog.</summary>
    private bool IsLinked(ChatLineViewModel line) =>
        LinksOn && line.Message.UserId.Length > 0 && Links?.PersonOf(line.Platform, line.Message.UserId) is { Accounts.Count: > 1 };

    /// <summary>Builds the preview and opens the linked action dialog; a preview with one row runs as before.</summary>
    private async Task OpenLinkedAsync(ModerationCommand command)
    {
        if (_moderation is null) return;
        var preview = await _moderation.BuildPreviewAsync(command, ModActor.Streamer);
        if (!preview.IsLinked)
        {
            await RunAsync(command);
            return;
        }
        LinkedAction = new LinkedActionViewModel(_moderation, preview, ModActor.Streamer, _services.OpenUrl ?? Cuebot.App.Shell.UrlLauncher.Open,
            dialog => { if (ReferenceEquals(LinkedAction, dialog)) LinkedAction = null; }, OnLinkedDone, ModReason.Trim());
    }

    private void OnLinkedDone(LinkedIncidentResult result)
    {
        _lastIncident = (result.IncidentId, DateTimeOffset.UtcNow);
        LastIncidentText = result.Summary;
        OnPropertyChanged(nameof(HasLastIncident));
        Status = result.Summary;
        RefreshModeration();
    }

    [RelayCommand]
    private async Task UndoLastIncident()
    {
        if (_moderation is null || _lastIncident is not { } incident) return;
        var undo = await _moderation.UndoIncidentAsync(incident.Id, ModActor.Streamer);
        Status = undo.Refused ?? undo.Summary;
        if (!_moderation.CanUndoIncident(incident.Id)) DismissLastIncident();
    }

    [RelayCommand]
    private void DismissLastIncident()
    {
        _lastIncident = null;
        LastIncidentText = "";
        OnPropertyChanged(nameof(HasLastIncident));
    }

    /// <summary>"Undo incident" on a mod log row.</summary>
    private async Task UndoIncidentFromLogAsync(ModLogLineViewModel line)
    {
        if (_moderation is null) return;
        var undo = await _moderation.UndoIncidentAsync(line.Entry.IncidentId, ModActor.Streamer);
        Status = undo.Refused ?? undo.Summary;
    }

    /// <summary>"Undo this mod's last hour" on a member's mod log row.</summary>
    private async Task UndoMemberFromLogAsync(ModLogLineViewModel line)
    {
        if (_moderation is null || line.Entry.ActorMemberId is not { } member) return;
        var undo = await _moderation.UndoMemberRecentAsync(member, ModActor.Streamer);
        Status = undo.Refused ?? undo.Summary;
    }

    private ModLogLineViewModel LogLine(ModLogEntry entry) =>
        new(entry, _moderation is null ? null : UndoIncidentFromLogAsync, _moderation is null ? null : UndoMemberFromLogAsync, () => DateTimeOffset.UtcNow);

    // ---------------------------------------------------------------- later arrivals

    private void RefreshLaterArrivals()
    {
        LaterArrivals.Clear();
        if (LinksOn && _moderation is not null)
            foreach (var prompt in _moderation.LaterArrivals)
                LaterArrivals.Add(new LaterArrivalViewModel(prompt, ApplyLaterArrivalAsync, DismissLaterArrival));
        OnPropertyChanged(nameof(HasLaterArrivals));
    }

    private async Task ApplyLaterArrivalAsync(LaterArrivalViewModel item)
    {
        if (_moderation is null) return;
        var reason = ModReason.Trim().Length > 0 ? ModReason.Trim()
            : $"Same person as an account banned on {string.Join(" and ", item.Prompt.BannedOn)}";
        var result = await _moderation.ApplyLaterArrivalAsync(item.Prompt.Id, ModActor.Streamer, reason);
        Status = result.Message;
        RefreshLaterArrivals();
    }

    private void DismissLaterArrival(LaterArrivalViewModel item)
    {
        _moderation?.DismissLaterArrival(item.Prompt.Id);
        RefreshLaterArrivals();
    }
}
