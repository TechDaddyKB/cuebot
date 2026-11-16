using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Chat.Moderation;

namespace Cuebot.App.ViewModels.Chat;

/// <summary>A message the platform held for review, in the Chat page's Held lane.</summary>
public sealed partial class HeldLineViewModel(HeldChatMessage held, Func<HeldLineViewModel, bool, Task> decide) : ObservableObject
{
    public HeldChatMessage Held { get; private set; } = held;
    public string Platform => Held.Platform;
    public IBrush PlatformBrush { get; } = PlatformColors.For(held.Platform);
    public string User => Held.UserName.Length > 0 ? Held.UserName : Held.UserLogin;
    public string Text => Held.Text;
    public string Reason => Held.Reason;
    public string Time => Held.HeldAt.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
    public bool IsPending => Held.IsPending;

    /// <summary>"Allowed by Mod", "Denied", "Expired: nobody decided in time".</summary>
    public string Outcome => Held.Status switch
    {
        HeldMessageStatus.Approved => "Allowed" + By,
        HeldMessageStatus.Denied => "Denied" + By,
        HeldMessageStatus.Expired => "Expired: nobody decided in time",
        _ => "",
    };

    private string By => Held.DecidedBy.Length > 0 ? " by " + Held.DecidedBy : "";

    [ObservableProperty] private bool _isBusy;

    internal void Update(HeldChatMessage held)
    {
        Held = held;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private Task Allow() => decide(this, true);

    [RelayCommand]
    private Task Deny() => decide(this, false);
}

/// <summary>An unban request in the Mod tools tab.</summary>
public sealed partial class UnbanRequestLineViewModel(UnbanRequestInfo request, Func<UnbanRequestLineViewModel, bool, Task> resolve) : ObservableObject
{
    public UnbanRequestInfo Request { get; } = request;
    public string User => Request.UserName.Length > 0 ? Request.UserName : Request.UserLogin;
    public string Text => Request.Text;
    public string Time => Request.CreatedAt == DateTimeOffset.MinValue ? "" : Request.CreatedAt.ToLocalTime().ToString("MMM d HH:mm", CultureInfo.CurrentCulture);

    /// <summary>The answer the viewer gets (optional).</summary>
    [ObservableProperty] private string _answer = "";

    [RelayCommand]
    private Task Approve() => resolve(this, true);

    [RelayCommand]
    private Task Deny() => resolve(this, false);
}

/// <summary>A blocked term in the Mod tools tab.</summary>
public sealed partial class BlockedTermLineViewModel(BlockedTermInfo term, Func<BlockedTermLineViewModel, Task> remove) : ObservableObject
{
    public BlockedTermInfo Term { get; } = term;
    public string Text => Term.Text;

    [RelayCommand]
    private Task Remove() => remove(this);
}

/// <summary>
/// The Chat page's Held lane and moderation tools for platforms with moderation queues (Twitch): held messages,
/// Shield Mode, unban requests and blocked terms. Every change goes through <see cref="ModerationService"/> as the
/// streamer, so it is logged; a tool the sign-in does not allow says which permission to add.
/// </summary>
public sealed partial class ModerationQueuesViewModel : ObservableObject
{
    private const int MaxHeldLines = 100;

    private readonly ModerationService? _moderation;
    private readonly Action<Action> _onUi;

    public ModerationQueuesViewModel(ModerationService? moderation, Action<Action> onUi)
    {
        _moderation = moderation;
        _onUi = onUi;
        Platforms = moderation?.Queues.Select(q => q.Platform).ToArray() ?? [];
        _platform = Platforms.FirstOrDefault() ?? "";
        if (moderation is null) return;
        moderation.QueuesChanged += c => _onUi(() => OnChanged(c));
        RebuildHeld();
    }

    /// <summary>Platforms with moderation queues (Twitch).</summary>
    public IReadOnlyList<string> Platforms { get; }

    public bool HasQueues => Platforms.Count > 0;
    public bool HasPlatformChoice => Platforms.Count > 1;

    /// <summary>The platform the Mod tools tab works on.</summary>
    [ObservableProperty] private string _platform;

    partial void OnPlatformChanged(string value) => Refresh();

    [ObservableProperty] private string _status = "";

    private IModerationQueues? Queues => _moderation?.QueuesFor(Platform);

    private ModerationCapability Capability(ModerationAction action) =>
        _moderation?.Capability(Platform, action) ?? ModerationCapability.NotAvailable(action, "Moderation is not available here.");

    // ---------------------------------------------------------------- held messages

    /// <summary>Held messages on every platform: waiting ones first (oldest on top), then recent decisions.</summary>
    public ObservableCollection<HeldLineViewModel> HeldLines { get; } = [];

    public int PendingCount => HeldLines.Count(h => h.IsPending);
    public bool HasHeldLines => HeldLines.Count > 0;

    /// <summary>"Held" or "Held (3)" for the tab.</summary>
    public string HeldHeader => PendingCount > 0 ? $"Held ({PendingCount})" : "Held";

    /// <summary>Why held messages cannot be decided here ("Sign in to Twitch again and allow ..."), or "".</summary>
    public string HeldNote => HasQueues && Capability(ModerationAction.ApproveHeld) is { CanRun: false } c ? c.Note : "";

    public bool HasHeldNote => HeldNote.Length > 0;

    private void RebuildHeld()
    {
        var all = _moderation?.Queues.SelectMany(q => q.HeldMessages).ToList() ?? [];
        var ordered = all.Where(h => h.IsPending).OrderBy(h => h.HeldAt)
            .Concat(all.Where(h => !h.IsPending).OrderByDescending(h => h.HeldAt)).Take(MaxHeldLines).ToList();
        var existing = HeldLines.ToDictionary(l => (l.Platform, l.Held.Id));
        HeldLines.Clear();
        foreach (var held in ordered)
        {
            if (existing.TryGetValue((held.Platform, held.Id), out var line)) line.Update(held);
            else line = new HeldLineViewModel(held, DecideAsync);
            HeldLines.Add(line);
        }
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(HasHeldLines));
        OnPropertyChanged(nameof(HeldHeader));
        OnPropertyChanged(nameof(HeldNote));
        OnPropertyChanged(nameof(HasHeldNote));
    }

    private async Task DecideAsync(HeldLineViewModel line, bool allow)
    {
        if (_moderation is null || !line.IsPending) return;
        line.IsBusy = true;
        try
        {
            var result = await _moderation.ExecuteAsync(ModerationCommand.ForHeld(line.Held, allow), ModActor.Streamer);
            Status = result.Message;
        }
        finally
        {
            line.IsBusy = false;
        }
        RebuildHeld();
    }

    // ---------------------------------------------------------------- Shield Mode

    public bool IsShieldOn => Queues?.ShieldMode?.IsActive == true;

    /// <summary>"Shield Mode is on (turned on by Mod at 17:05)." or "Shield Mode is off."</summary>
    public string ShieldText
    {
        get
        {
            if (Queues?.ShieldMode is not { } shield) return "Shield Mode: not known yet.";
            var by = shield.ModeratorName.Length > 0 ? $" by {shield.ModeratorName}" : "";
            var since = shield.Since is { } at ? " at " + at.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture) : "";
            return shield.IsActive ? $"Shield Mode is on (turned on{by}{since})." : "Shield Mode is off.";
        }
    }

    public string ShieldButton => IsShieldOn ? "Turn Shield Mode off" : "Turn Shield Mode on";
    public bool CanShield => Capability(ModerationAction.ShieldMode).CanRun;
    public string ShieldNote => Capability(ModerationAction.ShieldMode).Note;
    public bool HasShieldNote => ShieldNote.Length > 0;

    [RelayCommand]
    private async Task ToggleShield()
    {
        if (_moderation is null) return;
        var result = await _moderation.ExecuteAsync(new ModerationCommand(Platform, ModerationAction.ShieldMode) { Enabled = !IsShieldOn },
            ModActor.Streamer);
        Status = result.Message;
        RaiseShield();
    }

    private void RaiseShield()
    {
        OnPropertyChanged(nameof(IsShieldOn));
        OnPropertyChanged(nameof(ShieldText));
        OnPropertyChanged(nameof(ShieldButton));
        OnPropertyChanged(nameof(CanShield));
        OnPropertyChanged(nameof(ShieldNote));
        OnPropertyChanged(nameof(HasShieldNote));
    }

    // ---------------------------------------------------------------- unban requests

    public ObservableCollection<UnbanRequestLineViewModel> UnbanRequests { get; } = [];
    public bool CanUnbanRequests => Capability(ModerationAction.ApproveUnbanRequest).CanRun;
    public string UnbanRequestsNote => CanUnbanRequests ? "" : Capability(ModerationAction.ApproveUnbanRequest).Note;
    public bool HasUnbanRequests => UnbanRequests.Count > 0;
    public bool HasUnbanRequestsNote => UnbanRequestsNote.Length > 0;

    /// <summary>"No unban requests waiting." once loaded; "" before.</summary>
    [ObservableProperty] private string _unbanRequestsText = "";

    [RelayCommand]
    private async Task LoadUnbanRequests()
    {
        if (Queues is not { } queues || !CanUnbanRequests) return;
        try
        {
            var requests = await queues.GetUnbanRequestsAsync();
            UnbanRequests.Clear();
            foreach (var r in requests.Where(r => r.Status == UnbanRequestStatus.Pending)) UnbanRequests.Add(new UnbanRequestLineViewModel(r, ResolveAsync));
            UnbanRequestsText = UnbanRequests.Count == 0 ? "No unban requests waiting." : "";
        }
        catch (Exception ex)
        {
            UnbanRequestsText = "Could not load the unban requests: " + ex.Message;
        }
        OnPropertyChanged(nameof(HasUnbanRequests));
    }

    private async Task ResolveAsync(UnbanRequestLineViewModel line, bool approve)
    {
        if (_moderation is null) return;
        var result = await _moderation.ExecuteAsync(ModerationCommand.ForUnbanRequest(line.Request, approve, line.Answer), ModActor.Streamer);
        Status = result.Message;
        if (!result.Ok) return;
        UnbanRequests.Remove(line);
        OnPropertyChanged(nameof(HasUnbanRequests));
        if (UnbanRequests.Count == 0) UnbanRequestsText = "No unban requests waiting.";
    }

    // ---------------------------------------------------------------- blocked terms

    public ObservableCollection<BlockedTermLineViewModel> BlockedTerms { get; } = [];
    public bool CanBlockedTerms => Capability(ModerationAction.AddBlockedTerm).CanRun;
    public string BlockedTermsNote => CanBlockedTerms ? "" : Capability(ModerationAction.AddBlockedTerm).Note;
    public bool HasBlockedTermsNote => BlockedTermsNote.Length > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(AddTermCommand))]
    private string _newTerm = "";

    [ObservableProperty] private string _blockedTermsText = "";

    [RelayCommand]
    private async Task LoadBlockedTerms()
    {
        if (Queues is not { } queues || !CanBlockedTerms) return;
        try
        {
            var terms = await queues.GetBlockedTermsAsync();
            BlockedTerms.Clear();
            foreach (var t in terms) BlockedTerms.Add(new BlockedTermLineViewModel(t, RemoveTermAsync));
            BlockedTermsText = BlockedTerms.Count switch { 0 => "No blocked terms yet.", 1 => "1 blocked term.", var n => $"{n} blocked terms." };
        }
        catch (Exception ex)
        {
            BlockedTermsText = "Could not load the blocked terms: " + ex.Message;
        }
    }

    private bool CanAddTerm() => NewTerm.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanAddTerm))]
    private async Task AddTerm()
    {
        if (_moderation is null) return;
        var result = await _moderation.ExecuteAsync(ModerationCommand.BlockTerm(Platform, NewTerm), ModActor.Streamer);
        Status = result.Message;
        if (!result.Ok) return;
        NewTerm = "";
        await LoadBlockedTerms();
    }

    private async Task RemoveTermAsync(BlockedTermLineViewModel line)
    {
        if (_moderation is null) return;
        var result = await _moderation.ExecuteAsync(ModerationCommand.UnblockTerm(line.Term), ModActor.Streamer);
        Status = result.Message;
        if (result.Ok) BlockedTerms.Remove(line);
    }

    // ---------------------------------------------------------------- refresh

    /// <summary>Re-reads the state after a sign-in or when the tab opens: notes, Shield Mode, the lists.</summary>
    [RelayCommand]
    private async Task Reload()
    {
        if (Queues is { } queues) await queues.RefreshAsync();
        Refresh();
        await LoadUnbanRequests();
        await LoadBlockedTerms();
    }

    /// <summary>Re-reads what the platform allows now (scopes change with a new sign-in).</summary>
    public void Refresh()
    {
        RaiseShield();
        OnPropertyChanged(nameof(CanUnbanRequests));
        OnPropertyChanged(nameof(UnbanRequestsNote));
        OnPropertyChanged(nameof(HasUnbanRequestsNote));
        OnPropertyChanged(nameof(CanBlockedTerms));
        OnPropertyChanged(nameof(BlockedTermsNote));
        OnPropertyChanged(nameof(HasBlockedTermsNote));
        OnPropertyChanged(nameof(HeldNote));
        OnPropertyChanged(nameof(HasHeldNote));
    }

    private void OnChanged(ModerationQueueChange change)
    {
        switch (change.Kind)
        {
            case ModerationQueueKind.HeldMessages:
                RebuildHeld();
                break;
            case ModerationQueueKind.ShieldMode when change.Platform == Platform:
                RaiseShield();
                break;
            case ModerationQueueKind.UnbanRequests when change.Platform == Platform && CanUnbanRequests:
                _ = LoadUnbanRequests();
                break;
            case ModerationQueueKind.BlockedTerms when change.Platform == Platform && BlockedTerms.Count > 0:
                _ = LoadBlockedTerms();
                break;
        }
        QueueChanged?.Invoke(change);
    }

    /// <summary>Raised on the UI thread for every change, so the chat can re-flag lines of suspicious users.</summary>
    internal event Action<ModerationQueueChange>? QueueChanged;
}
