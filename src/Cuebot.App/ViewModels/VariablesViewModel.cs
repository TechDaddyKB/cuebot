using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Variables;

namespace Cuebot.App.ViewModels;

/// <summary>One global or user variable in the viewer.</summary>
public sealed record VariableRow(string Name, string Value, string Type, bool Persisted, DateTimeOffset LastWriteAt,
    string Platform = "", string UserId = "", string UserName = "")
{
    public string LastWrite => LastWriteAt == default ? "—" : LastWriteAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
    public string User => UserName.Length > 0 && UserName != UserId ? $"{UserName} ({UserId})" : UserId;
    public string Scope => Persisted ? "persisted" : "temporary";
}

public enum VariableTab { Persisted, Temporary, Users }

/// <summary>
/// Global Variable Viewer: persisted and temporary globals (~name~) and per-user variables, with an editor that
/// honours Auto type ("5" is stored as a number).
/// </summary>
public sealed partial class VariablesViewModel : PageViewModel
{
    private readonly VariableViewer _viewer;
    private bool _refreshPosted;

    public VariablesViewModel(VariableViewer viewer) : base("Variables")
    {
        _viewer = viewer;
        viewer.Changed += _ => OnUi(ScheduleRefresh);
        Refresh();
    }

    public ObservableCollection<VariableRow> Persisted { get; } = [];
    public ObservableCollection<VariableRow> Temporary { get; } = [];
    public ObservableCollection<VariableRow> UserVariables { get; } = [];
    public ObservableCollection<string> Platforms { get; } = [ChatHub.AllPlatforms];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsUsersTab), nameof(DeleteAllText))]
    private int _selectedTabIndex;

    [ObservableProperty] private string _platformFilter = ChatHub.AllPlatforms;
    [ObservableProperty] private VariableRow? _selectedPersisted;
    [ObservableProperty] private VariableRow? _selectedTemporary;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DeleteForAllUsersCommand))]
    private VariableRow? _selectedUserVariable;

    // Editor
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private string _editValue = "";
    [ObservableProperty] private bool _editPersisted = true;
    [ObservableProperty] private bool _autoType = true;
    [ObservableProperty] private string _editPlatform = "twitch";
    [ObservableProperty] private string _editUserId = "";
    [ObservableProperty] private string _editUserName = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isConfirmingDeleteAll;

    public VariableTab Tab => (VariableTab)Math.Clamp(SelectedTabIndex, 0, 2);
    public bool IsUsersTab => Tab == VariableTab.Users;

    public string DeleteAllText => Tab switch
    {
        VariableTab.Persisted => "Delete every persisted global variable?",
        VariableTab.Temporary => "Delete every temporary global variable?",
        _ => string.Equals(PlatformFilter, ChatHub.AllPlatforms, StringComparison.OrdinalIgnoreCase)
            ? "Delete every user variable on every platform?"
            : $"Delete every user variable on {PlatformFilter}?",
    };

    partial void OnSelectedTabIndexChanged(int value)
    {
        IsConfirmingDeleteAll = false;
        if (Tab != VariableTab.Users) EditPersisted = Tab == VariableTab.Persisted;
    }

    partial void OnPlatformFilterChanged(string value)
    {
        if (value is null) return;
        OnPropertyChanged(nameof(DeleteAllText));
        RefreshUsers();
    }

    partial void OnSelectedPersistedChanged(VariableRow? value) => Load(value);
    partial void OnSelectedTemporaryChanged(VariableRow? value) => Load(value);
    partial void OnSelectedUserVariableChanged(VariableRow? value) => Load(value);

    private void ScheduleRefresh()
    {
        if (_refreshPosted) return;
        _refreshPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshPosted = false;
            Refresh();
        }, DispatcherPriority.Background);
    }

    /// <summary>Re-reads both global tabs and the user tab (a variable can move between persisted and temporary).</summary>
    internal void Refresh()
    {
        Fill(Persisted, _viewer.PersistedGlobals().OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Select(ToRow), SelectedPersisted, r => SelectedPersisted = r);
        Fill(Temporary, _viewer.TemporaryGlobals().OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase).Select(ToRow), SelectedTemporary, r => SelectedTemporary = r);
        RefreshUsers();
    }

    private void RefreshUsers()
    {
        foreach (var platform in _viewer.UserPlatforms().Where(p => !Platforms.Contains(p, StringComparer.OrdinalIgnoreCase)))
            Platforms.Add(platform);
        var filter = string.Equals(PlatformFilter, ChatHub.AllPlatforms, StringComparison.OrdinalIgnoreCase) ? null : PlatformFilter;
        var rows = _viewer.UserVariables(filter)
            .OrderBy(v => v.Platform, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.UserName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .Select(v => new VariableRow(v.Name, v.Text, v.TypeName, v.Persisted, v.LastWrite, v.Platform, v.UserId, v.UserName));
        Fill(UserVariables, rows, SelectedUserVariable, r => SelectedUserVariable = r);
    }

    private static VariableRow ToRow(GlobalVariableInfo v) => new(v.Name, v.Text, v.TypeName, v.Persisted, v.LastWrite);

    private static void Fill(ObservableCollection<VariableRow> target, IEnumerable<VariableRow> rows, VariableRow? selected, Action<VariableRow?> select)
    {
        var desired = rows
            .Select(r => target.FirstOrDefault(t => t == r) ?? r)   // unchanged rows keep their instance (and selection)
            .ToArray();
        target.SyncTo(desired);
        if (selected is not null && !target.Contains(selected))
            select(target.FirstOrDefault(r => r.Name == selected.Name && r.Platform == selected.Platform && r.UserId == selected.UserId));
    }

    private void Load(VariableRow? row)
    {
        if (row is null) return;
        EditName = row.Name;
        EditValue = row.Value;
        EditPersisted = row.Persisted;
        if (row.Platform.Length > 0)
        {
            EditPlatform = row.Platform;
            EditUserId = row.UserId;
            EditUserName = row.UserName;
        }
    }

    [RelayCommand]
    private void Set()
    {
        var name = EditName.Trim();
        if (name.Length == 0)
        {
            Status = "Enter a variable name.";
            return;
        }
        if (IsUsersTab)
        {
            if (EditPlatform.Trim().Length == 0 || EditUserId.Trim().Length == 0)
            {
                Status = "Enter the user's platform and id.";
                return;
            }
            _viewer.SetUserVariable(EditPlatform.Trim(), EditUserId.Trim(), EditUserName.Trim() is { Length: > 0 } n ? n : null,
                name, EditValue, EditPersisted, AutoType);
            Status = $"Set {name} for {EditUserId.Trim()}.";
        }
        else
        {
            _viewer.SetGlobal(name, EditValue, EditPersisted, AutoType);
            Status = $"Set ~{name}~ ({(EditPersisted ? "persisted" : "temporary")}).";
        }
        Refresh();
    }

    [RelayCommand]
    private void New()
    {
        EditName = EditValue = "";
        EditPersisted = Tab != VariableTab.Temporary;
        SelectedPersisted = SelectedTemporary = SelectedUserVariable = null;
        Status = "";
    }

    [RelayCommand]
    private void Delete(VariableRow row)
    {
        var deleted = row.Platform.Length > 0
            ? _viewer.DeleteUserVariable(row.Platform, row.UserId, row.Name)
            : _viewer.DeleteGlobal(row.Name);
        Status = deleted ? $"Deleted {row.Name}." : $"{row.Name} was already gone.";
        Refresh();
    }

    private bool CanDeleteForAllUsers() => SelectedUserVariable is not null;

    /// <summary>Removes the selected variable name from every user (of the filtered platform).</summary>
    [RelayCommand(CanExecute = nameof(CanDeleteForAllUsers))]
    private void DeleteForAllUsers()
    {
        if (SelectedUserVariable is not { } row) return;
        var filter = string.Equals(PlatformFilter, ChatHub.AllPlatforms, StringComparison.OrdinalIgnoreCase) ? null : PlatformFilter;
        var count = _viewer.DeleteUserVariableForAllUsers(row.Name, filter);
        Status = $"Deleted {row.Name} from {count} user(s).";
        Refresh();
    }

    [RelayCommand]
    private void DeleteAll() => IsConfirmingDeleteAll = true;

    [RelayCommand]
    private void CancelDeleteAll() => IsConfirmingDeleteAll = false;

    [RelayCommand]
    private void ConfirmDeleteAll()
    {
        IsConfirmingDeleteAll = false;
        var count = Tab switch
        {
            VariableTab.Persisted => _viewer.DeleteAllGlobals(persisted: true),
            VariableTab.Temporary => _viewer.DeleteAllGlobals(persisted: false),
            _ => _viewer.DeleteAllUserVariables(
                string.Equals(PlatformFilter, ChatHub.AllPlatforms, StringComparison.OrdinalIgnoreCase) ? null : PlatformFilter),
        };
        Status = $"Deleted {count} variable(s).";
        Refresh();
    }
}
