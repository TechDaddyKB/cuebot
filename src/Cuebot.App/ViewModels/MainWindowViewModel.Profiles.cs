using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Profiles;

namespace Cuebot.App.ViewModels;

/// <summary>An entry of the top bar's profile menu: a profile to switch to, or "Manage profiles…".</summary>
public sealed record ProfileMenuItem(string Name, string Color, bool IsOpen, ICommand Command);

/// <summary>The open streamer profile in the top bar and the window title, its menu, and profiles in the jump box.</summary>
public sealed partial class MainWindowViewModel
{
    /// <summary>The jump box lists other profiles as "Profile: name"; choosing one switches to it.</summary>
    public const string JumpProfilePrefix = "Profile: ";

    public bool HasProfiles => _shell.Profiles is not null;

    private ProfileEntry? OpenProfile => _shell.Profiles?.Find(Settings.Profiles.OpenId);

    public string ProfileName => OpenProfile?.Name ?? "";

    public string ProfileColor => OpenProfile?.Color ?? ProfileCatalog.Colors[0];

    public string ProfileTip => $"You are in the profile {ProfileName}. Click to switch to another persona or to manage profiles.";

    /// <summary>"Cuebot", or "Cuebot – Kitsune" once there is more than one profile.</summary>
    public string WindowTitle => _shell.Profiles is { Profiles.Count: > 1 } ? $"Cuebot – {ProfileName}" : "Cuebot";

    public IReadOnlyList<ProfileMenuItem> ProfileMenu =>
    [
        .. (_shell.Profiles?.Profiles ?? []).Select(p => new ProfileMenuItem(p.Id == Settings.Profiles.OpenId ? $"{p.Name} (open)" : p.Name,
            p.Color, p.Id == Settings.Profiles.OpenId, new RelayCommand(() => _shell.SwitchProfile(p.Id)))),
        new ProfileMenuItem("Manage profiles…", "", false, ManageProfilesCommand),
    ];

    private IEnumerable<string> ProfileJumpTargets =>
        (_shell.Profiles?.Profiles ?? []).Where(p => p.Id != Settings.Profiles.OpenId).Select(p => JumpProfilePrefix + p.Name);

    /// <summary>"Profile: name" from the jump box: switches to that profile. False when it is not one.</summary>
    private bool JumpToProfile(string text)
    {
        if (!text.StartsWith(JumpProfilePrefix, StringComparison.OrdinalIgnoreCase) || _shell.Profiles is null) return false;
        if (_shell.Profiles.Find(text[JumpProfilePrefix.Length..]) is not { } profile) return false;
        _shell.SwitchProfile(profile.Id);
        return true;
    }

    [RelayCommand]
    private void ManageProfiles()
    {
        SelectedPage = Settings;
        Settings.ShowProfiles();
    }

    private void OnProfilesChanged()
    {
        OnPropertyChanged(nameof(ProfileName));
        OnPropertyChanged(nameof(ProfileColor));
        OnPropertyChanged(nameof(ProfileTip));
        OnPropertyChanged(nameof(WindowTitle));
        OnPropertyChanged(nameof(ProfileMenu));
        OnPropertyChanged(nameof(JumpTargets));
    }
}
