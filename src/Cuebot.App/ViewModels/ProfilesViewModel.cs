using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.App.Shell;
using Cuebot.Core.Profiles;

namespace Cuebot.App.ViewModels;

/// <summary>One profile in the list on Settings &gt; Profiles.</summary>
public sealed class ProfileRowViewModel(ProfileEntry entry, bool isOpen)
{
    public ProfileEntry Entry { get; } = entry;
    public string Id => Entry.Id;
    public string Name => Entry.Name;
    public string Color => Entry.Color;
    public bool IsOpen { get; } = isOpen;
    public string State => IsOpen ? "open now" : "";
}

/// <summary>A profile to copy a new one from, or "Nothing (empty)".</summary>
public sealed record ProfileSourceOption(string? Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One part of a profile that a new profile can copy.</summary>
public sealed partial class ProfilePartOption(ProfileParts part, string name, string tip) : ObservableObject
{
    public ProfileParts Part { get; } = part;
    public string Name { get; } = name;
    public string Tip { get; } = tip;

    [ObservableProperty]
    private bool _isChecked = true;
}

/// <summary>
/// Settings &gt; Profiles: the streamer profiles (personas), making new ones (empty or copied by parts), renaming,
/// colours, switching and deleting. Switching restarts Cuebot on the other profile.
/// </summary>
public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly IShellHost _shell;
    private readonly ProfileCatalog? _catalog;
    private readonly string? _openId;

    public ProfilesViewModel(IShellHost shell)
    {
        _shell = shell;
        _catalog = shell.Profiles;
        _openId = _catalog?.Active.Id;
        _newColor = Colors[0];
        Refresh();
        _newColor = NextFreeColor();
    }

    /// <summary>False where Cuebot runs without profiles (tests, the designer).</summary>
    public bool IsAvailable => _catalog is not null;

    public IReadOnlyList<string> Colors => ProfileCatalog.Colors;

    public ObservableCollection<ProfileRowViewModel> Rows { get; } = [];

    public ObservableCollection<ProfileSourceOption> Sources { get; } = [];

    public IReadOnlyList<ProfilePartOption> Parts { get; } =
    [
        new(ProfileParts.Actions, "Actions, timers, queues and watchers", "Every action with its triggers and steps."),
        new(ProfileParts.Commands, "Chat commands", "Your !commands and their settings."),
        new(ProfileParts.Overlays, "Overlays and widgets", "Overlays, their widgets, pictures and sounds. OBS needs the new links after the switch."),
        new(ProfileParts.DecksAndHotkeys, "Decks, hotkeys and controllers", "Deck buttons, keyboard shortcuts, Stream Deck, MIDI and game controllers."),
        new(ProfileParts.Variables, "Global variables", "Remembered ~values~, like a death counter. Viewer variables stay behind."),
        new(ProfileParts.AppsAndDevices, "Stream apps and devices", "OBS, Streamlabs Desktop, lights and mixers. (The servers' settings, the phone pairing included, are shared by every profile anyway.)"),
        new(ProfileParts.Voices, "Voices and voice control", "Speech engines and keys, voice aliases and voice control."),
    ];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanSwitch), nameof(CanDelete))]
    private ProfileRowViewModel? _selected;

    [ObservableProperty]
    private string _editName = "";

    [ObservableProperty]
    private string _editColor = ProfileCatalog.Colors[0];

    [ObservableProperty]
    private string _newName = "";

    [ObservableProperty]
    private string _newColor;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCopying))]
    private ProfileSourceOption? _newSource;

    [ObservableProperty]
    private string _status = "";

    public bool HasSelection => Selected is not null;

    public bool IsCopying => NewSource?.Id is not null;

    public bool CanSwitch => Selected is { IsOpen: false };

    public bool CanDelete => Selected is { IsOpen: false };

    /// <summary>The id of the profile this Cuebot opened.</summary>
    public string? OpenId => _openId;

    public string OpenProfileName => _catalog?.Find(_openId)?.Name ?? "";

    public ConfirmClick DeleteConfirm { get; } = new("Delete profile", "Click again: deletes its sign-ins and settings for good");

    partial void OnSelectedChanged(ProfileRowViewModel? value)
    {
        DeleteConfirm.Reset();
        EditName = value?.Name ?? "";
        EditColor = value?.Color ?? Colors[0];
    }

    [RelayCommand]
    private void Create()
    {
        if (_catalog is null) return;
        var parts = IsCopying ? Parts.Where(p => p.IsChecked).Aggregate(ProfileParts.None, (all, p) => all | p.Part) : ProfileParts.None;
        try
        {
            var created = _catalog.Create(NewName, NewColor, NewSource?.Id, parts);
            Status = $"Made the profile {created.Name}. Switch to it to sign in to its accounts.";
            NewName = "";
            Refresh();
            Selected = Rows.FirstOrDefault(r => r.Id == created.Id);
            NewColor = NextFreeColor();
            ProfilesChanged?.Invoke();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void SaveChanges()
    {
        if (_catalog is null || Selected is not { } row) return;
        try
        {
            if (EditName.Trim() != row.Name) _catalog.Rename(row.Id, EditName);
            if (EditColor != row.Color) _catalog.SetColor(row.Id, EditColor);
            Status = $"Saved {EditName.Trim()}.";
            Refresh();
            Selected = Rows.FirstOrDefault(r => r.Id == row.Id);
            ProfilesChanged?.Invoke();
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void SwitchTo()
    {
        if (Selected is { IsOpen: false } row) _shell.SwitchProfile(row.Id);
    }

    [RelayCommand]
    private void Delete()
    {
        if (_catalog is null || Selected is not { IsOpen: false } row) return;
        if (!DeleteConfirm.Confirm(row.Id)) return;
        try
        {
            _catalog.Delete(row.Id);
            Status = $"Deleted the profile {row.Name}.";
            Refresh();
            ProfilesChanged?.Invoke();
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
        }
    }

    /// <summary>A profile was made, renamed, recoloured or deleted (the top bar and tray list them).</summary>
    public event Action? ProfilesChanged;

    private void Refresh()
    {
        Rows.Clear();
        Sources.Clear();
        Sources.Add(new ProfileSourceOption(null, "Nothing: start empty"));
        if (_catalog is null) return;
        foreach (var p in _catalog.Profiles)
        {
            Rows.Add(new ProfileRowViewModel(p, p.Id == _openId));
            Sources.Add(new ProfileSourceOption(p.Id, $"A copy of {p.Name}"));
        }
        NewSource = Sources[0];
        OnPropertyChanged(nameof(OpenProfileName));
    }

    private string NextFreeColor() =>
        Colors.FirstOrDefault(c => Rows.All(r => !r.Color.Equals(c, StringComparison.OrdinalIgnoreCase))) ?? Colors[Rows.Count % Colors.Count];
}
