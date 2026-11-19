using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Users;

namespace Cuebot.App.ViewModels.Chat;

/// <summary>One account linked to the selected chatter, on the user card.</summary>
public sealed class LinkedAccountRowViewModel(IdentityAccount account, LinkStatus status, bool isSelf, Func<LinkedAccountRowViewModel, string, Task> act)
{
    public IdentityAccount Account { get; } = account;
    public LinkStatus Status { get; } = status;
    public bool IsSelf { get; } = isSelf;
    public string Platform => Account.Platform;
    public IBrush PlatformBrush { get; } = PlatformColors.For(account.Platform);
    public string Name => Account.Name + (Account.CurrentName.Length > 0 && Account.CurrentName != Account.NameAtLink ? $" (was {Account.NameAtLink})" : "");

    /// <summary>"verified 2026-10-02", "suggested by Sam, not confirmed".</summary>
    public string StatusText => IsSelf ? "this account" : IdentityLinks.Describe(Account, Status);

    /// <summary>The platform user id: display names can be copied, ids cannot.</summary>
    public string UserId => Account.UserId;

    public bool CanConfirm => !IsSelf && Account.Status == LinkStatus.Suggested;
    public bool CanReject => !IsSelf && Account.Status == LinkStatus.Suggested;
    public bool CanUnlink => !IsSelf;

    public IAsyncRelayCommand ConfirmCommand => field ??= new AsyncRelayCommand(() => act(this, "confirm"));
    public IAsyncRelayCommand RejectCommand => field ??= new AsyncRelayCommand(() => act(this, "reject"));
    public IAsyncRelayCommand UnlinkCommand => field ??= new AsyncRelayCommand(() => act(this, "unlink"));
}

/// <summary>A note on the user card (it follows links: notes on a linked account show here too).</summary>
public sealed class NoteRowViewModel(ModNote note, bool elsewhere, Func<NoteRowViewModel, Task> delete)
{
    public ModNote Note { get; } = note;
    public string Text => Note.Text;

    /// <summary>"Sam, Nov 14" and, for a note written on a linked account, where.</summary>
    public string Byline => $"{Note.Author}, {Note.EditedAt.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture)}"
                            + (elsewhere ? $" (on {Note.Platform})" : "");

    public IAsyncRelayCommand DeleteCommand => field ??= new AsyncRelayCommand(() => delete(this));
}

/// <summary>A same-name account on another platform: a hint only; "Same person…" fills in the suggestion form.</summary>
public sealed class NameHintViewModel(ViewerRef account, Action<NameHintViewModel> use)
{
    public ViewerRef Account { get; } = account;
    public string Text => $"{Account.UserName} on {Account.Platform} has the same name";
    public IRelayCommand UseCommand => field ??= new RelayCommand(() => use(this));
}

/// <summary>An action made through a link that is still in force, offered for reversal when unlinking.</summary>
public sealed partial class ReversibleRowViewModel(ModLogEntry entry, bool reverse) : ObservableObject
{
    public ModLogEntry Entry { get; } = entry;
    public string Text => $"{Entry.Platform}: {Entry.Action} of {Entry.TargetName} ({Entry.At.ToLocalTime().ToString("MMM d", CultureInfo.CurrentCulture)})";
    [ObservableProperty] private bool _reverse = reverse;
}

/// <summary>The unlink question: remove one account or dissolve, marked wrong or not, and which actions to reverse.</summary>
public sealed partial class UnlinkViewModel : ObservableObject
{
    private readonly ModerationService _moderation;
    private readonly Func<UnlinkViewModel, Task> _confirm;
    private readonly Action _cancel;

    public UnlinkViewModel(ModerationService moderation, LinkedAccountRowViewModel row, Func<UnlinkViewModel, Task> confirm, Action cancel)
    {
        _moderation = moderation;
        Row = row;
        _confirm = confirm;
        _cancel = cancel;
        Rebuild();
    }

    public LinkedAccountRowViewModel Row { get; }
    public ObservableCollection<ReversibleRowViewModel> Reversible { get; } = [];
    public string Title => Dissolve ? "Unlink all of this person's accounts?" : $"Unlink {Row.Account.Name} on {Row.Platform}?";
    public string Question => _moderation.PreviewUnlink(Row.Account.LinkId, Dissolve, MarkedWrong)?.Question ?? "";
    public bool HasReversible => Reversible.Count > 0;

    /// <summary>Wrong: remembered as rejected, and reversing starts ticked. Not wrong: just no longer needed.</summary>
    [ObservableProperty] private bool _markedWrong;
    [ObservableProperty] private bool _dissolve;
    [ObservableProperty] private string _reason = "";

    partial void OnMarkedWrongChanged(bool value) => Rebuild();
    partial void OnDissolveChanged(bool value) => Rebuild();

    private void Rebuild()
    {
        Reversible.Clear();
        if (_moderation.PreviewUnlink(Row.Account.LinkId, Dissolve, MarkedWrong) is { } preview)
            foreach (var entry in preview.InForce) Reversible.Add(new ReversibleRowViewModel(entry, preview.ReverseByDefault));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Question));
        OnPropertyChanged(nameof(HasReversible));
    }

    [RelayCommand]
    private Task Confirm() => _confirm(this);

    [RelayCommand]
    private void Cancel() => _cancel();
}

/// <summary>One row of the linked ban or timeout dialog: the preview, then the result.</summary>
public sealed partial class LinkedRowViewModel : ObservableObject
{
    private readonly LinkedActionViewModel _owner;

    public LinkedRowViewModel(LinkedPreviewRow row, LinkedActionViewModel owner)
    {
        Row = row;
        _owner = owner;
        _isTicked = row.Ticked;
    }

    public LinkedPreviewRow Row { get; }
    public IBrush PlatformBrush => PlatformColors.For(Row.Platform);
    public string Account => $"{Row.Platform}: {Row.Name}";
    public string LinkText => Row.LinkText;
    public string WillHappen => Row.WillHappen;
    public string Notes => Row.Notes;
    public bool HasNotes => Row.Notes.Length > 0;
    public bool CanTick => Row.CanTick && !_owner.IsDone;
    public bool ByHand => Row.ByHand;

    [ObservableProperty] private bool _isTicked;

    /// <summary>After running: "Done", "Failed: ...", "By hand".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string _result = "";

    public bool HasResult => Result.Length > 0;

    [ObservableProperty] private bool _canRetry;
    [ObservableProperty] private bool _awaitingManual;

    partial void OnIsTickedChanged(bool value) => _owner.TicksChanged();

    internal void RefreshTick() => OnPropertyChanged(nameof(CanTick));

    [RelayCommand]
    private void OpenWebsite()
    {
        if (Row.WebsiteUrl is { } url) _owner.OpenUrl(url);
    }

    /// <summary>"I did it": records the by-hand row as done.</summary>
    [RelayCommand]
    private void IDidIt() => _owner.RecordManual(this);

    [RelayCommand]
    private Task Retry() => _owner.RetryAsync(this);
}

/// <summary>
/// The linked ban or timeout dialog (docs/design/UNIFIED_MOD_CHAT_PLAN.md, section 7.4): the preview of every linked account,
/// a required reason, a second confirmation for a ban on more than one platform, then the result per row with "I did
/// it" for by-hand platforms, a retry for failures and "Undo incident".
/// </summary>
public sealed partial class LinkedActionViewModel : ObservableObject
{
    private readonly ModerationService _moderation;
    private readonly ModActor _actor;
    private readonly Action<string> _openUrl;
    private readonly Action<LinkedActionViewModel> _close;
    private readonly Action<LinkedIncidentResult>? _done;

    public LinkedActionViewModel(ModerationService moderation, LinkedPreview preview, ModActor actor, Action<string> openUrl,
        Action<LinkedActionViewModel> close, Action<LinkedIncidentResult>? done = null, string reason = "")
    {
        _moderation = moderation;
        Preview = preview;
        _actor = actor;
        _openUrl = openUrl;
        _close = close;
        _done = done;
        _reason = reason;
        foreach (var row in preview.Rows) Rows.Add(new LinkedRowViewModel(row, this));
    }

    public LinkedPreview Preview { get; }
    public ObservableCollection<LinkedRowViewModel> Rows { get; } = [];

    /// <summary>"Ban rowdy_guy and linked accounts" / "Timeout 10 min: rowdy_guy and linked accounts".</summary>
    public string Title => Preview.Command.Action == ModerationAction.Ban
        ? $"Ban {Preview.Origin.Name} and linked accounts"
        : $"Timeout {ModerationService.FormatDuration(Preview.Command.Duration ?? TimeSpan.FromMinutes(10))}: {Preview.Origin.Name} and linked accounts";

    public string Intro => "Nothing has been sent yet. Ticked accounts are acted on together, as one incident you can undo. " +
                           "Suggested links start unticked.";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun))]
    private string _reason = "";

    /// <summary>"Ban 3 accounts on 3 platforms?" once the first click asked for it.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RunLabel))]
    private bool _confirming;

    [ObservableProperty] private string _confirmText = "";
    [ObservableProperty] private string _status = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRun), nameof(RunLabel))]
    private bool _isDone;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canUndo;

    public string IncidentId { get; private set; } = "";

    public bool CanRun => !IsDone && Reason.Trim().Length > 0 && Ticked.Count > 0;

    public string RunLabel => IsDone ? "Done" : Confirming ? "Yes, " + ConfirmText.TrimEnd('?').ToLowerInvariant()
        : Preview.Command.Action == ModerationAction.Ban ? "Ban ticked accounts" : "Time out ticked accounts";

    internal IReadOnlyList<string> Ticked => Rows.Where(r => r.IsTicked && r.Row.CanTick).Select(r => r.Row.RowId).ToArray();

    internal void TicksChanged()
    {
        Confirming = false;
        OnPropertyChanged(nameof(CanRun));
        RunCommand.NotifyCanExecuteChanged();
    }

    partial void OnReasonChanged(string value) => RunCommand.NotifyCanExecuteChanged();

    internal void OpenUrl(string url) => _openUrl(url);

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task Run()
    {
        IsBusy = true;
        try
        {
            var result = await _moderation.ExecuteLinkedAsync(new LinkedExecution(Preview, Ticked, Reason) { Confirmed = Confirming }, _actor);
            if (result.NeedsConfirmation)
            {
                ConfirmText = result.ConfirmText;
                Confirming = true;
                Status = result.ConfirmText + " Click again to confirm.";
                return;
            }
            if (result.Refused is { } refused)
            {
                Status = refused;
                return;
            }
            IncidentId = result.IncidentId;
            foreach (var r in result.Rows)
            {
                var row = Rows.First(x => x.Row.RowId == r.Row.RowId);
                row.Result = r.AwaitingManual ? "By hand: open the website, then press \"I did it\"."
                    : r.Result switch
                    {
                        ModResult.Ok => "Done",
                        ModResult.Failed => "Failed: " + r.Message,
                        _ => "Not sent: " + r.Message,
                    };
                row.CanRetry = r.Result == ModResult.Failed;
                row.AwaitingManual = r.AwaitingManual;
            }
            IsDone = true;
            foreach (var row in Rows) row.RefreshTick();
            CanUndo = _moderation.CanUndoIncident(IncidentId);
            Status = result.Summary;
            _done?.Invoke(result);
        }
        finally
        {
            IsBusy = false;
        }
    }

    internal void RecordManual(LinkedRowViewModel row)
    {
        if (IncidentId.Length == 0) return;
        if (_moderation.RecordManual(IncidentId, Preview, row.Row.RowId, Reason, _actor) is not null)
        {
            row.Result = "Done by hand";
            row.AwaitingManual = false;
            CanUndo = _moderation.CanUndoIncident(IncidentId);
        }
    }

    internal async Task RetryAsync(LinkedRowViewModel row)
    {
        if (IncidentId.Length == 0) return;
        var result = await _moderation.RetryRowAsync(IncidentId, Preview, row.Row.RowId, Reason, _actor);
        row.Result = result.Result == ModResult.Ok ? "Done" : "Failed: " + result.Message;
        row.CanRetry = result.Result == ModResult.Failed;
        CanUndo = _moderation.CanUndoIncident(IncidentId);
    }

    [RelayCommand]
    private async Task Undo()
    {
        if (IncidentId.Length == 0) return;
        var undo = await _moderation.UndoIncidentAsync(IncidentId, _actor);
        Status = undo.Refused ?? undo.Summary;
        CanUndo = _moderation.CanUndoIncident(IncidentId);
    }

    [RelayCommand]
    private void Close() => _close(this);
}

/// <summary>"This person is banned on Twitch and Kick. Apply here too?" in a strip on the chat page.</summary>
public sealed class LaterArrivalViewModel(LaterArrivalPrompt prompt, Func<LaterArrivalViewModel, Task> apply, Action<LaterArrivalViewModel> dismiss)
{
    public LaterArrivalPrompt Prompt { get; } = prompt;
    public string Text => Prompt.Text;
    public IAsyncRelayCommand ApplyCommand => field ??= new AsyncRelayCommand(() => apply(this));
    public IRelayCommand DismissCommand => field ??= new RelayCommand(() => dismiss(this));
}
