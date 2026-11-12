using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.App.Shell;
using Cuebot.Core.Actions;
using Cuebot.Core.Desktop.Clipboard;
using Cuebot.Portal.Changes;
using Cuebot.Portal.Members;
using Cuebot.Portal.Relay;
using Cuebot.Portal.Uploads;

namespace Cuebot.App.ViewModels;

/// <summary>
/// "Mod portal" page (docs/MOD_PORTAL_PLAN.md): invite mods with a role, change or remove their access, approve their
/// changes and undo them, set what each role may do, and open the portal on the shared relay. Signing in comes in a
/// later version; until then invites can be made and managed but not yet used.
/// </summary>
public sealed partial class ModPortalViewModel : PageViewModel
{
    public const string PageTitle = "Mod portal";

    private readonly PortalDirectory _directory;
    private readonly PortalChanges _changes;
    private readonly IClipboardService? _clipboard;
    private readonly PortalRelayClient? _relay;

    public ModPortalViewModel(PortalDirectory directory, PortalChanges changes, IClipboardService? clipboard = null, PortalRelayClient? relay = null,
        PortalMediaStore? media = null)
        : base(PageTitle)
    {
        _directory = directory;
        _changes = changes;
        _clipboard = clipboard;
        _relay = relay;
        Media = media;
        if (relay is not null) relay.StatusChanged += () => OnUi(() => OnPropertyChanged(nameof(RelayStatus)));
        directory.Changed += () => OnUi(Refresh);
        changes.Changed += () => OnUi(Refresh);
        Refresh();
        _inviteRole = Roles.FirstOrDefault(r => r.Id == DefaultRoles.StandardModId) ?? Roles.FirstOrDefault();
    }

    public static IReadOnlyList<string> Providers { get; } = ["No lock", "Twitch", "Google", "Discord"];
    public static IReadOnlyList<string> ChangeModes { get; } = ["Live", "Live and tell me", "Needs approval"];
    public static IReadOnlyList<string> MemberChangeModes { get; } = ["As the role says", .. ChangeModes];
    public static IReadOnlyList<string> Levels { get; } = ["1 Basic", "2 Stream control", "3 Show-critical", "4 Trusted tech"];

    public string RelayId => _directory.RelayId;

    /// <summary>The portal is open: Cuebot stays connected to the shared relay so mods can reach it.</summary>
    public bool PortalOpen
    {
        get => _directory.Enabled;
        set
        {
            if (value == _directory.Enabled) return;
            _directory.Enabled = value;
            OnPropertyChanged();
            if (_relay is not null) _ = _relay.StartAsync();
        }
    }

    public string RelayStatus => _relay?.StatusMessage ?? "";

    /// <summary>Assistant messages each mod may send per day (0: the portal's Assistant is off).</summary>
    public decimal AssistantMessagesPerDay
    {
        get => _directory.AssistantMessagesPerDay;
        set
        {
            if ((int)value == _directory.AssistantMessagesPerDay) return;
            _directory.AssistantMessagesPerDay = (int)value;
            OnPropertyChanged();
        }
    }

    /// <summary>Which accounts mods can sign in with in this copy of Cuebot (built-in apps; none in a source build).</summary>
    public string SignInOptions
    {
        get
        {
            var apps = Cuebot.Portal.SignIn.PortalProviderApps.BuiltIn;
            string[] names = [.. Enum.GetValues<IdentityProvider>().Where(p => apps.For(p).IsSet).Select(p => p.ToString())];
            return names.Length > 0
                ? $"Mods sign in with {string.Join(", ", names[..^1]) + (names.Length > 1 ? " or " : "") + names[^1]}."
                : "This copy of Cuebot was built without the sign-in apps, so mods cannot sign in. The release downloads have them.";
        }
    }

    public ObservableCollection<ModRoleViewModel> Roles { get; } = [];
    public ObservableCollection<ModMemberViewModel> Members { get; } = [];
    public ObservableCollection<ModInviteViewModel> Invites { get; } = [];
    public ObservableCollection<ModChangeViewModel> Pending { get; } = [];
    public ObservableCollection<ModChangeViewModel> Recent { get; } = [];

    public bool HasNoMembers => Members.Count == 0;
    public bool HasNoInvites => Invites.Count == 0;
    public bool HasNoPending => Pending.Count == 0;
    public bool HasNoRecent => Recent.Count == 0;

    [ObservableProperty] private ModRoleViewModel? _inviteRole;
    [ObservableProperty] private string _inviteLabel = "";
    [ObservableProperty] private decimal _inviteHours = 24;
    [ObservableProperty] private string _inviteLockProvider = "No lock";
    [ObservableProperty] private string _inviteLockValue = "";
    [ObservableProperty] private string _newInviteLink = "";
    [ObservableProperty] private string _message = "";

    public bool HasNewInvite => NewInviteLink.Length > 0;
    partial void OnNewInviteLinkChanged(string value) => OnPropertyChanged(nameof(HasNewInvite));

    internal PortalDirectory Directory => _directory;
    internal PortalChanges Changes => _changes;

    /// <summary>The upload library, to open a pending upload before approving it.</summary>
    internal PortalMediaStore? Media { get; }
    internal void Say(string text) => Message = text;

    [RelayCommand]
    private void CreateInvite()
    {
        if (InviteRole is null) { Message = "Choose a role for the invite first."; return; }
        InviteLock? inviteLock = InviteLockProvider == "No lock" || InviteLockValue.Trim().Length == 0
            ? null
            : new InviteLock { Provider = Enum.Parse<IdentityProvider>(InviteLockProvider), Value = InviteLockValue };
        try
        {
            var created = _directory.CreateInvite(InviteRole.Id, InviteLabel, TimeSpan.FromHours((double)InviteHours), inviteLock);
            NewInviteLink = created.Link;
            InviteLabel = "";
            InviteLockValue = "";
            Message = "Invite made. Copy the link now: it is shown only once.";
        }
        catch (ArgumentOutOfRangeException)
        {
            Message = "An invite lasts from 1 hour up to 30 days (720 hours).";
        }
    }

    [RelayCommand]
    private async Task CopyInvite()
    {
        if (_clipboard is not { IsAvailable: true } clipboard || NewInviteLink.Length == 0) return;
        await clipboard.SetTextAsync(NewInviteLink);
        Message = "Invite link copied. Send it to your mod privately.";
    }

    [RelayCommand]
    private void HideInvite() => NewInviteLink = "";

    [RelayCommand]
    private void AddRole()
    {
        _directory.SaveRole(new PortalRole { Name = "New role", Permissions = [PortalPermission.RunActions, PortalPermission.ViewActivity] });
        Message = "Role added. Choose what it may do below.";
    }

    [RelayCommand]
    private void SignOutEveryone()
    {
        _directory.SignOutEveryone();
        Message = "Everyone is signed out on every device. Mods sign in again with their account.";
    }

    internal void Refresh()
    {
        Sync(Roles, _directory.Roles, r => r.Id, r => new ModRoleViewModel(this, r), (vm, r) => vm.Update(r));
        Sync(Members, _directory.Members, m => m.Id, m => new ModMemberViewModel(this, m), (vm, m) => vm.Update(m));
        Sync(Invites, _directory.Invites, i => i.Id, i => new ModInviteViewModel(this, i), (_, _) => { });
        var all = _changes.All;
        Sync(Pending, all.Where(c => c.Status == ChangeStatus.Pending).Reverse().ToList(), c => c.Id, c => new ModChangeViewModel(this, c), (vm, c) => vm.Update(c));
        Sync(Recent, all.Where(c => c.Status != ChangeStatus.Pending).Reverse().Take(50).ToList(), c => c.Id, c => new ModChangeViewModel(this, c), (vm, c) => vm.Update(c));
        OnPropertyChanged(nameof(HasNoMembers));
        OnPropertyChanged(nameof(HasNoInvites));
        OnPropertyChanged(nameof(HasNoPending));
        OnPropertyChanged(nameof(HasNoRecent));
        if (InviteRole is not null && Roles.All(r => r.Id != InviteRole.Id)) InviteRole = Roles.FirstOrDefault();
    }

    private static void Sync<TVm, T>(ObservableCollection<TVm> target, IReadOnlyList<T> source, Func<T, Guid> id, Func<T, TVm> make, Action<TVm, T> update)
        where TVm : IHasId
    {
        var ids = source.Select(id).ToList();
        for (var i = target.Count - 1; i >= 0; i--) if (!ids.Contains(target[i].Id)) target.RemoveAt(i);
        for (var i = 0; i < source.Count; i++)
        {
            var existing = target.FirstOrDefault(t => t.Id == ids[i]);
            if (existing is null) target.Insert(i, make(source[i]));
            else
            {
                update(existing, source[i]);
                var at = target.IndexOf(existing);
                if (at != i) target.Move(at, i);
            }
        }
    }

    internal static string ModeText(ChangeMode mode) => ChangeModes[(int)mode];
    internal static ChangeMode ModeFrom(string text) => (ChangeMode)Math.Max(0, ChangeModes.ToList().IndexOf(text));
}

public interface IHasId
{
    Guid Id { get; }
}

public sealed partial class ModRoleViewModel : ObservableObject, IHasId
{
    private readonly ModPortalViewModel _page;
    private PortalRole _role;
    private bool _loading;

    public ModRoleViewModel(ModPortalViewModel page, PortalRole role)
    {
        _page = page;
        _role = role;
        Permissions = [.. Enum.GetValues<PortalPermission>().Select(p => new ModPermissionViewModel(this, p, PermissionText(p)))];
        Update(role);
    }

    public Guid Id => _role.Id;
    public ObservableCollection<ModPermissionViewModel> Permissions { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _level = "";
    [ObservableProperty] private string _changes = "";
    [ObservableProperty] private string _groups = "";

    public override string ToString() => Name;

    internal void Update(PortalRole role)
    {
        _role = role;
        _loading = true;
        Name = role.Name;
        Level = ModPortalViewModel.Levels[Math.Clamp((int)role.MaxRisk - 1, 0, 3)];
        Changes = ModPortalViewModel.ModeText(role.Changes);
        Groups = string.Join(", ", role.Groups);
        foreach (var p in Permissions) p.Load(role.Permissions.Contains(p.Permission));
        _loading = false;
    }

    partial void OnNameChanged(string value) => Save();
    partial void OnLevelChanged(string value) => Save();
    partial void OnChangesChanged(string value) => Save();
    partial void OnGroupsChanged(string value) => Save();

    internal void Save()
    {
        if (_loading) return;
        _role.Name = Name;
        _role.MaxRisk = (StepRisk)(ModPortalViewModel.Levels.ToList().IndexOf(Level) + 1);
        _role.Changes = ModPortalViewModel.ModeFrom(Changes);
        _role.Groups = [.. Groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
        _role.Permissions = [.. Permissions.Where(p => p.IsOn).Select(p => p.Permission)];
        _page.Directory.SaveRole(_role);
    }

    [RelayCommand]
    private void Delete() => _page.Say(_page.Directory.DeleteRole(Id)
        ? $"Role '{Name}' deleted."
        : $"'{Name}' is still given to a member or an open invite. Change those first.");

    private static string PermissionText(PortalPermission p) => p switch
    {
        PortalPermission.RunActions => "Run actions",
        PortalPermission.CreateCommands => "Create commands",
        PortalPermission.EditCommands => "Change commands",
        PortalPermission.DeleteCommands => "Delete commands",
        PortalPermission.CreateActions => "Create actions",
        PortalPermission.EditActions => "Change actions",
        PortalPermission.DeleteActions => "Delete actions",
        PortalPermission.AudioLevels => "Audio levels",
        PortalPermission.StreamControl => "Scenes, raids, queues, TTS settings",
        PortalPermission.ShowCritical => "End stream, ban, title and category",
        PortalPermission.UploadSounds => "Upload sounds",
        PortalPermission.UploadMedia => "Upload images, GIFs and video",
        PortalPermission.EditOverlays => "Change overlays and widgets",
        PortalPermission.CreateOverlays => "Create and delete overlays",
        PortalPermission.ViewActivity => "See the activity feed",
        PortalPermission.ViewLogs => "See logs and connection status",
        PortalPermission.UndoOthers => "Undo other mods' changes",
        PortalPermission.UseAssistant => "Use the Assistant (on your AI account)",
        PortalPermission.ManageRewards => "Channel point rewards (Twitch, Kick)",
        _ => p.ToString(),
    };
}

public sealed partial class ModPermissionViewModel(ModRoleViewModel role, PortalPermission permission, string text) : ObservableObject
{
    private bool _loading;

    public PortalPermission Permission => permission;
    public string Text => text;

    [ObservableProperty] private bool _isOn;

    internal void Load(bool on)
    {
        _loading = true;
        IsOn = on;
        _loading = false;
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!_loading) role.Save();
    }
}

public sealed partial class ModMemberViewModel : ObservableObject, IHasId
{
    private readonly ModPortalViewModel _page;
    private PortalMember _member;
    private bool _loading;

    public ModMemberViewModel(ModPortalViewModel page, PortalMember member)
    {
        _page = page;
        _member = member;
        Update(member);
    }

    public Guid Id => _member.Id;
    public IReadOnlyList<ModRoleViewModel> Roles => _page.Roles;
    public string Account => $"{_member.Provider} · joined {_member.JoinedAt.LocalDateTime:d}";
    public string Devices => _member.Devices.Count switch
    {
        0 => "Not signed in on any device",
        1 => "1 device: " + _member.Devices[0].Name,
        var n => $"{n} devices: " + string.Join(", ", _member.Devices.Select(d => d.Name)),
    };
    public bool IsTwitch => _member.Provider == IdentityProvider.Twitch;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private ModRoleViewModel? _role;
    [ObservableProperty] private string _changes = "";
    [ObservableProperty] private bool _suspended;
    [ObservableProperty] private bool _requireTwitchModerator;

    internal void Update(PortalMember member)
    {
        _member = member;
        _loading = true;
        Name = member.Name;
        Role = _page.Roles.FirstOrDefault(r => r.Id == member.RoleId);
        Changes = member.Changes is { } c ? ModPortalViewModel.ModeText(c) : ModPortalViewModel.MemberChangeModes[0];
        Suspended = member.Suspended;
        RequireTwitchModerator = member.RequireTwitchModerator;
        _loading = false;
        OnPropertyChanged(nameof(Devices));
    }

    partial void OnNameChanged(string value) { if (!_loading) _page.Directory.UpdateMember(Id, name: value); }
    partial void OnRoleChanged(ModRoleViewModel? value) { if (!_loading && value is not null) _page.Directory.UpdateMember(Id, roleId: value.Id); }
    partial void OnSuspendedChanged(bool value) { if (!_loading) _page.Directory.SetSuspended(Id, value, value ? "Paused by the streamer" : ""); }
    partial void OnRequireTwitchModeratorChanged(bool value) { if (!_loading) _page.Directory.UpdateMember(Id, requireTwitchModerator: value); }

    partial void OnChangesChanged(string value)
    {
        if (_loading) return;
        if (value == ModPortalViewModel.MemberChangeModes[0]) _page.Directory.UpdateMember(Id, clearChanges: true);
        else _page.Directory.UpdateMember(Id, changes: ModPortalViewModel.ModeFrom(value));
    }

    [RelayCommand]
    private void SignOut()
    {
        foreach (var d in _member.Devices.ToList()) _page.Directory.RemoveDevice(Id, d.Id);
        _page.Say($"{Name} is signed out on every device.");
    }

    [RelayCommand]
    private void Remove()
    {
        _page.Directory.RemoveMember(Id);
        _page.Say($"{Name} no longer has access. Send a new invite to let them back.");
    }
}

public sealed partial class ModInviteViewModel(ModPortalViewModel page, PortalInvite invite) : ObservableObject, IHasId
{
    public Guid Id => invite.Id;

    public string Text
    {
        get
        {
            var role = page.Roles.FirstOrDefault(r => r.Id == invite.RoleId)?.Name ?? "a deleted role";
            var label = invite.Label.Length > 0 ? invite.Label + " · " : "";
            var locked = invite.Lock is { } l ? $" · only for {l.Provider} {l.Value}" : "";
            return $"{label}{role} · until {invite.ExpiresAt.LocalDateTime:g}{locked}";
        }
    }

    [RelayCommand]
    private void Withdraw()
    {
        page.Directory.WithdrawInvite(Id);
        page.Say("Invite withdrawn. Its link no longer works.");
    }
}

public sealed partial class ModChangeViewModel : ObservableObject, IHasId
{
    private readonly ModPortalViewModel _page;
    private PortalChange _change;

    public ModChangeViewModel(ModPortalViewModel page, PortalChange change)
    {
        _page = page;
        _change = change;
    }

    public Guid Id => _change.Id;

    public string Text
    {
        get
        {
            if (_change.Area == UploadArea.AreaName)
            {
                var node = _change.After ?? _change.Before;
                var kind = (string?)node?["kind"] switch { "Image" => "picture", "Video" => "video", _ => "sound" };
                var verb = _change.Kind == ChangeKind.Delete ? "wants to remove" : "uploaded";
                return $"{_change.MemberName} {verb} the {kind} '{_change.TargetName}' · {_change.At.LocalDateTime:g}";
            }
            var what = _change.Kind switch { ChangeKind.Create => "created", ChangeKind.Delete => "deleted", _ => "changed" };
            return $"{_change.MemberName} {what} the {_change.Area} '{_change.TargetName}' · {_change.At.LocalDateTime:g}";
        }
    }

    public string Status => _change.Status switch
    {
        ChangeStatus.Applied => "Live",
        ChangeStatus.Pending => "Waiting for you",
        ChangeStatus.Rejected => "Rejected",
        ChangeStatus.Expired => "Expired (not answered in 7 days)",
        ChangeStatus.Undone => "Undone by " + _change.DecidedBy,
        _ => "",
    };

    public bool CanUndo => _change.Status == ChangeStatus.Applied;

    /// <summary>An upload whose cleaned file can be opened.</summary>
    public bool CanOpen => UploadFile is not null;

    private string? UploadFile =>
        _change.Area == UploadArea.AreaName && _page.Media is { } media && (string?)(_change.After ?? _change.Before)?["file"] is { } file
        && Path.Combine(media.Folder, file) is var path && File.Exists(path) ? path : null;

    [RelayCommand]
    private void Open()
    {
        if (UploadFile is { } path) FileLauncher.Open(path);
    }

    internal void Update(PortalChange change)
    {
        _change = change;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(CanUndo));
    }

    [RelayCommand]
    private async Task Approve()
    {
        var result = await _page.Changes.ApproveAsync(Id, force: _forceNext);
        if (result == ChangeResult.Conflict)
        {
            _page.Say($"'{_change.TargetName}' changed since the mod asked. Approve again to use their version anyway.");
            _forceNext = true;
            return;
        }
        _forceNext = false;
        _page.Say("Approved: it is live now.");
    }

    private bool _forceNext;

    [RelayCommand]
    private async Task Reject()
    {
        await _page.Changes.RejectAsync(Id);
        _page.Say("Rejected. Nothing changed.");
    }

    [RelayCommand]
    private async Task Undo()
    {
        var result = await _page.Changes.UndoAsync(Id, force: _forceNext);
        if (result == ChangeResult.Conflict && !_forceNext)
        {
            _forceNext = true;
            _page.Say($"'{_change.TargetName}' changed again since. Press Undo again to put the older version back anyway.");
            return;
        }
        _forceNext = false;
        _page.Say("Undone: the earlier version is back.");
    }
}
