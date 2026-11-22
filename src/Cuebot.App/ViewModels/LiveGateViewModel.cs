using CommunityToolkit.Mvvm.ComponentModel;

namespace Cuebot.App.ViewModels;

/// <summary>The "Only while live" option and its platform choice, shared by the timer, command and trigger editors.</summary>
public sealed class LiveGateViewModel(Func<bool> isOn, Func<List<string>?> platforms, Action<bool, List<string>?> apply) : ObservableObject
{
    public bool OnlyWhileLive
    {
        get => isOn();
        set
        {
            if (isOn() == value) return;
            apply(value, platforms());
            OnPropertyChanged();
        }
    }

    public bool Twitch { get => Has("Twitch"); set => Choose("Twitch", value); }
    public bool YouTube { get => Has("YouTube"); set => Choose("YouTube", value); }
    public bool Kick { get => Has("Kick"); set => Choose("Kick", value); }
    public bool Rumble { get => Has("Rumble"); set => Choose("Rumble", value); }

    private bool Has(string platform) => platforms()?.Contains(platform, StringComparer.OrdinalIgnoreCase) == true;

    private void Choose(string platform, bool chosen, [System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        if (Has(platform) == chosen) return;
        var list = platforms()?.Where(p => !string.Equals(p, platform, StringComparison.OrdinalIgnoreCase)).ToList() ?? [];
        if (chosen) list.Add(platform);
        apply(isOn(), list.Count == 0 ? null : list); // none chosen means any platform, and is not saved
        OnPropertyChanged(property);
    }
}
