using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Chat;
using Cuebot.Core.Users;

namespace Cuebot.App.ViewModels;

/// <summary>A chatter seen on one platform.</summary>
public sealed class UserRowViewModel(UserRecord user)
{
    public UserRecord User { get; } = user;
    public string Key => User.Key;
    public string Name => User.DisplayName.Length > 0 ? User.DisplayName : User.UserName;
    public string Platform => User.Platform;
    public string FirstSeen => Time(User.FirstSeen);
    public string LastSeen => Time(User.LastSeen);
    public string Messages => User.MessageCount.ToString("N0", CultureInfo.InvariantCulture);
    public string Roles => User.Roles == ChatRoles.None ? "" : User.Roles.ToString();
    public string Tip => $"{User.Platform} · id {(User.UserId.Length > 0 ? User.UserId : "(none)")} · {User.UserName}";

    private static string Time(DateTimeOffset t) =>
        t == default ? "—" : t.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}

/// <summary>A group member row.</summary>
public sealed class GroupMemberRow(GroupMember member, Action<GroupMemberRow> remove)
{
    public GroupMember Member { get; } = member;
    public IRelayCommand RemoveCommand { get; } = new RelayCommand<GroupMemberRow>(row => remove(row!));
    public string Platform => Member.Platform;
    public string UserName => Member.UserName.Length > 0 ? Member.UserName : "(no name)";
    public string UserId => Member.UserId.Length > 0 ? Member.UserId : "(no id)";
}

/// <summary>A group in the list; renaming writes through to groups.json when the new name is free.</summary>
public sealed partial class GroupRowViewModel : ObservableObject
{
    private readonly UsersViewModel _page;
    private string _nameText;

    public GroupRowViewModel(UsersViewModel page, GroupDefinition group)
    {
        _page = page;
        Name = group.Name;
        _nameText = group.Name;
        Count = group.Users.Count;
    }

    public string Name { get; internal set; }

    [ObservableProperty] private int _count;
    [ObservableProperty] private string _nameError = "";

    public string CountText => Count == 1 ? "1 member" : $"{Count} members";

    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(CountText));

    public string NameText
    {
        get => _nameText;
        set
        {
            if (_nameText == value) return;
            _nameText = value;
            OnPropertyChanged();
            NameError = _page.RenameGroup(this, value) ?? "";
        }
    }
}

/// <summary>Users &amp; Groups page: chatters Cuebot has seen, first-words / present-viewer settings, and user groups.</summary>
public sealed partial class UsersViewModel : PageViewModel
{
    private readonly UserRegistry _users;
    private readonly GroupStore _groups;
    private readonly ViewerData? _viewerData;
    private readonly Dictionary<string, GroupRowViewModel> _groupRows = new(StringComparer.OrdinalIgnoreCase);
    private bool _usersRefreshPosted;
    private bool _loading;

    public UsersViewModel(UserRegistry users, GroupStore groups, ViewerData? viewerData = null) : base("Users & Groups")
    {
        _users = users;
        _groups = groups;
        _viewerData = viewerData;

        _loading = true;
        var settings = users.Settings;
        _presentMinutes = settings.PresentMinutes;
        _presentIntervalMinutes = settings.PresentIntervalMinutes;
        _resetFirstWordsOnStreamOnline = settings.ResetFirstWordsOnStreamOnline;
        _loading = false;

        users.UserSeen += _ => OnUi(ScheduleUsersRefresh);
        groups.Changed += () => OnUi(RefreshGroups);
        RefreshUsers();
        RefreshGroups();
    }

    // ---- users ----

    public ObservableCollection<string> Platforms { get; } = [ChatHub.AllPlatforms];
    public ObservableCollection<UserRowViewModel> Users { get; } = [];

    [ObservableProperty] private string _platformFilter = ChatHub.AllPlatforms;
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private UserRowViewModel? _selectedUser;
    [ObservableProperty] private string _usersStatus = "";

    public string UsersSummary => $"{Users.Count} of {_users.Count} user(s)";

    partial void OnPlatformFilterChanged(string value)
    {
        if (value is not null) RefreshUsers();
    }

    partial void OnSearchChanged(string value) => RefreshUsers();

    partial void OnSelectedUserChanged(UserRowViewModel? value)
    {
        AddSelectedUserToGroupCommand.NotifyCanExecuteChanged();
        ForgetSelectedUserCommand.NotifyCanExecuteChanged();
        IsConfirmingForget = false;
    }

    // ---- forget a viewer ----

    /// <summary>True while the "Forget viewer" confirmation is shown.</summary>
    [ObservableProperty] private bool _isConfirmingForget;

    public string ForgetPrompt => SelectedUser is { } u
        ? $"Forget {u.Name} ({u.Platform}) everywhere? This removes their stored points and other values, group memberships, command counts and voice settings, and their name from quotes. It cannot be undone; logs and older backups keep what they already have."
        : "";

    private bool CanForgetSelectedUser() => SelectedUser is not null && _viewerData is not null;

    [RelayCommand(CanExecute = nameof(CanForgetSelectedUser))]
    private void ForgetSelectedUser()
    {
        OnPropertyChanged(nameof(ForgetPrompt));
        IsConfirmingForget = true;
    }

    [RelayCommand]
    private void CancelForget() => IsConfirmingForget = false;

    [RelayCommand]
    private void ConfirmForget()
    {
        IsConfirmingForget = false;
        if (SelectedUser is not { } row || _viewerData is null) return;
        var result = _viewerData.Forget(row.User.Platform, row.User.UserId, row.User.UserName);
        UsersStatus = $"Forgot {row.Name} ({result.Total} record(s) removed).";
        RefreshUsers();
    }

    private void ScheduleUsersRefresh()
    {
        if (_usersRefreshPosted) return;
        _usersRefreshPosted = true;
        // A busy chat updates users many times a second; refresh once per burst.
        Dispatcher.UIThread.Post(() =>
        {
            _usersRefreshPosted = false;
            RefreshUsers();
        }, DispatcherPriority.Background);
    }

    internal void RefreshUsers()
    {
        var all = _users.All();
        foreach (var platform in all.Select(u => u.Platform).Distinct(StringComparer.OrdinalIgnoreCase)
                     .Where(p => !Platforms.Contains(p, StringComparer.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase))
            Platforms.Add(platform);

        var filter = PlatformFilter ?? ChatHub.AllPlatforms;
        var selectedKey = SelectedUser?.Key;
        var rows = all
            .Where(u => string.Equals(filter, ChatHub.AllPlatforms, StringComparison.OrdinalIgnoreCase)
                        || string.Equals(u.Platform, filter, StringComparison.OrdinalIgnoreCase))
            .Where(u => Search.Length == 0
                        || u.DisplayName.Contains(Search, StringComparison.OrdinalIgnoreCase)
                        || u.UserName.Contains(Search, StringComparison.OrdinalIgnoreCase)
                        || u.UserId.Contains(Search, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(u => u.LastSeen)
            .Select(u => new UserRowViewModel(u))
            .ToArray();
        Users.Clear();
        foreach (var row in rows) Users.Add(row);
        SelectedUser = Users.FirstOrDefault(u => u.Key == selectedKey);
        OnPropertyChanged(nameof(UsersSummary));
    }

    /// <summary>Forgets who has spoken (on the filtered platform, or everywhere), so "First words" fires again.</summary>
    [RelayCommand]
    private void ResetFirstWords()
    {
        var count = _users.ResetFirstWords(PlatformFilter);
        var where = string.Equals(PlatformFilter, ChatHub.AllPlatforms, StringComparison.OrdinalIgnoreCase) ? "every platform" : PlatformFilter;
        UsersStatus = $"First words reset on {where} ({count} user(s) forgotten).";
    }

    [ObservableProperty] private decimal? _presentMinutes;
    [ObservableProperty] private decimal? _presentIntervalMinutes;
    [ObservableProperty] private bool _resetFirstWordsOnStreamOnline;

    partial void OnPresentMinutesChanged(decimal? value) =>
        UpdateSettings(s => s.PresentMinutes = (int)Math.Max(0, value ?? 0));

    partial void OnPresentIntervalMinutesChanged(decimal? value) =>
        UpdateSettings(s => s.PresentIntervalMinutes = (int)Math.Max(0, value ?? 0));

    partial void OnResetFirstWordsOnStreamOnlineChanged(bool value) =>
        UpdateSettings(s => s.ResetFirstWordsOnStreamOnline = value);

    private void UpdateSettings(Action<UserSettings> change)
    {
        if (!_loading) _users.UpdateSettings(change);
    }

    // ---- groups ----

    public ObservableCollection<GroupRowViewModel> Groups { get; } = [];
    public ObservableCollection<GroupMemberRow> Members { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedGroup))]
    [NotifyCanExecuteChangedFor(nameof(AddSelectedUserToGroupCommand))]
    private GroupRowViewModel? _selectedGroup;

    [ObservableProperty] private string _newGroupName = "";
    [ObservableProperty] private string _groupsStatus = "";
    [ObservableProperty] private string _memberPlatform = "twitch";
    [ObservableProperty] private string _memberName = "";
    [ObservableProperty] private string _memberId = "";

    public bool HasSelectedGroup => SelectedGroup is not null;

    public string GroupsSummary => Groups.Count == 0
        ? "No groups yet. Groups can be allowed or denied on commands, and managed by sub-actions."
        : $"{Groups.Count} group(s)";

    partial void OnSelectedGroupChanged(GroupRowViewModel? value) => RefreshMembers();

    internal void RefreshGroups()
    {
        var all = _groups.All();
        var rows = new List<GroupRowViewModel>();
        foreach (var group in all.OrderBy(g => g.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (!_groupRows.TryGetValue(group.Name, out var row))
            {
                row = new GroupRowViewModel(this, group);
                _groupRows[group.Name] = row;
            }
            row.Count = group.Users.Count;
            rows.Add(row);
        }
        foreach (var stale in _groupRows.Where(p => !rows.Contains(p.Value)).Select(p => p.Key).ToArray()) _groupRows.Remove(stale);
        Groups.SyncTo(rows);
        if (SelectedGroup is not null && !rows.Contains(SelectedGroup)) SelectedGroup = null;
        RefreshMembers();
        OnPropertyChanged(nameof(GroupsSummary));
    }

    private void RefreshMembers()
    {
        Members.Clear();
        if (SelectedGroup is null) return;
        foreach (var member in _groups.UsersIn(SelectedGroup.Name)
                     .OrderBy(m => m.Platform, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(m => m.UserName, StringComparer.OrdinalIgnoreCase))
            Members.Add(new GroupMemberRow(member, RemoveMember));
    }

    /// <summary>Applies a rename typed in the group list; returns an error message when the name is not usable.</summary>
    internal string? RenameGroup(GroupRowViewModel row, string newName)
    {
        var name = newName.Trim();
        if (name.Length == 0) return "A group needs a name.";
        if (string.Equals(name, row.Name, StringComparison.Ordinal)) return null;
        var oldName = row.Name;
        if (_groups.Exists(name) && !string.Equals(name, oldName, StringComparison.OrdinalIgnoreCase)) return "Another group has this name.";
        // Re-key the row first: the store's Changed event refreshes the list synchronously and must find it.
        _groupRows.Remove(oldName);
        row.Name = name;
        _groupRows[name] = row;
        if (_groups.Rename(oldName, name)) return null;
        _groupRows.Remove(name);
        row.Name = oldName;
        _groupRows[oldName] = row;
        return "Could not rename the group.";
    }

    [RelayCommand]
    private void AddGroup()
    {
        var name = NewGroupName.Trim();
        if (name.Length == 0) return;
        if (!_groups.Add(name))
        {
            GroupsStatus = $"A group named '{name}' already exists.";
            return;
        }
        NewGroupName = "";
        GroupsStatus = "";
        SelectedGroup = Groups.FirstOrDefault(g => string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase));
    }

    [RelayCommand]
    private void DeleteGroup()
    {
        if (SelectedGroup is not { } group) return;
        if (_groups.Remove(group.Name)) GroupsStatus = $"Deleted group '{group.Name}'.";
    }

    [RelayCommand]
    private void ClearGroup()
    {
        if (SelectedGroup is not { } group) return;
        var count = _groups.Clear(group.Name);
        GroupsStatus = $"Removed {Math.Max(0, count)} member(s) from '{group.Name}'.";
    }

    [RelayCommand]
    private void AddMember()
    {
        if (SelectedGroup is not { } group) return;
        var platform = MemberPlatform.Trim();
        var userName = MemberName.Trim().TrimStart('@');
        var userId = MemberId.Trim();
        if (platform.Length == 0 || (userName.Length == 0 && userId.Length == 0))
        {
            GroupsStatus = "Enter a platform and a user name or id.";
            return;
        }
        // Fill in the id from a user Cuebot has seen, so the membership survives a rename on the platform.
        if (userId.Length == 0 && _users.FindByName(platform, userName) is { } seen)
        {
            userId = seen.UserId;
            userName = seen.UserName;
        }
        GroupsStatus = _groups.AddUser(group.Name, platform, userId, userName)
            ? $"Added {(userName.Length > 0 ? userName : userId)} to '{group.Name}'."
            : $"{(userName.Length > 0 ? userName : userId)} is already in '{group.Name}'.";
        MemberName = MemberId = "";
    }

    [RelayCommand]
    private void RemoveMember(GroupMemberRow row)
    {
        if (SelectedGroup is not { } group) return;
        _groups.RemoveUser(group.Name, row.Member.Platform, row.Member.UserId, row.Member.UserName);
    }

    private bool CanAddSelectedUser() => SelectedUser is not null && SelectedGroup is not null;

    [RelayCommand(CanExecute = nameof(CanAddSelectedUser))]
    private void AddSelectedUserToGroup()
    {
        if (SelectedUser is not { } user || SelectedGroup is not { } group) return;
        UsersStatus = _groups.AddUser(group.Name, user.User.Platform, user.User.UserId, user.User.UserName)
            ? $"Added {user.Name} to '{group.Name}'."
            : $"{user.Name} is already in '{group.Name}'.";
    }
}
