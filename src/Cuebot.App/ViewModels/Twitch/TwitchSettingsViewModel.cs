using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Twitch;

namespace Cuebot.App.ViewModels.Twitch;

/// <summary>A scope checkbox on the Settings tab.</summary>
public sealed partial class TwitchScopeItem(TwitchScopeInfo info, bool requested, bool granted, Action changed) : ObservableObject
{
    public string Scope => info.Scope;
    public string Group => info.Group;
    public string Description => info.Description;
    public bool IsRequired => info.Scope is TwitchScopes.ReadChat or TwitchScopes.WriteChat;
    public bool CanChange => !IsRequired;

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(State))] private bool _isRequested = requested;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(State))] private bool _isGranted = granted;

    public string State => (IsRequested, IsGranted) switch
    {
        (true, true) => "granted",
        (true, false) => "sign in again to grant",
        (false, true) => "granted, dropped at next sign-in",
        _ => "",
    };

    partial void OnIsRequestedChanged(bool value) => changed();
}

/// <summary>Settings tab: which scopes to request, polling intervals and what is unavailable.</summary>
public sealed partial class TwitchSettingsViewModel : TwitchTabViewModel
{
    private bool _loading;

    public TwitchSettingsViewModel(TwitchService twitch) : base(twitch)
    {
        _loading = true;
        var requested = TwitchScopes.ForBroadcaster(twitch.Settings.RequestedScopes).ToHashSet(StringComparer.Ordinal);
        var granted = twitch.Settings.Scopes.ToHashSet(StringComparer.Ordinal);
        Scopes = TwitchScopes.Catalog
            .Select(s => new TwitchScopeItem(s, requested.Contains(s.Scope), granted.Contains(s.Scope), SaveScopes))
            .ToArray();
        _loading = false;
    }

    public IReadOnlyList<TwitchScopeItem> Scopes { get; }

    public decimal ViewerCountInterval
    {
        get => Twitch.Settings.ViewerCountIntervalSeconds;
        set { Twitch.Settings.ViewerCountIntervalSeconds = Math.Max(0, (int)value); Twitch.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal PresentViewersInterval
    {
        get => Twitch.Settings.PresentViewersIntervalSeconds;
        set { Twitch.Settings.PresentViewersIntervalSeconds = Math.Max(0, (int)value); Twitch.SaveSettings(); OnPropertyChanged(); }
    }

    public bool UpcomingAdWarnings
    {
        get => Twitch.Settings.UpcomingAdWarnings;
        set { Twitch.Settings.UpcomingAdWarnings = value; Twitch.SaveSettings(); OnPropertyChanged(); }
    }

    // ---------------------------------------------------------------- sub counter

    public decimal SubCount
    {
        get => Twitch.Api.Counters.SubCounter.Count;
        set { Twitch.Api.Counters.SetSubCount((int)value); OnPropertyChanged(); }
    }

    public decimal SubRollover
    {
        get => Twitch.Settings.SubCounter.Rollover;
        set { Twitch.Settings.SubCounter.Rollover = Math.Max(0, (int)value); Twitch.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal SubRolloverCount
    {
        get => Twitch.Api.Counters.SubCounter.RolloverCount;
        set { Twitch.Api.Counters.SetSubCount(Twitch.Api.Counters.SubCounter.Count, (int)value); OnPropertyChanged(); }
    }

    public bool SubCountResubs
    {
        get => Twitch.Settings.SubCounter.CountResubs;
        set { Twitch.Settings.SubCounter.CountResubs = value; Twitch.SaveSettings(); OnPropertyChanged(); }
    }

    public bool SubCountGifts
    {
        get => Twitch.Settings.SubCounter.CountGiftedSubs;
        set { Twitch.Settings.SubCounter.CountGiftedSubs = value; Twitch.SaveSettings(); OnPropertyChanged(); }
    }

    [RelayCommand]
    private void ResetSubCounter()
    {
        Twitch.Api.Counters.SetSubCount(0, 0);
        OnPropertyChanged(nameof(SubCount));
        OnPropertyChanged(nameof(SubRolloverCount));
    }

    public string RequestedSummary => $"{Scopes.Count(s => s.IsRequested)} of {Scopes.Count} scopes requested, "
                                      + $"{Scopes.Count(s => s.IsGranted)} granted to the current sign-in.";

    public string UnavailableEvents => Twitch.UnavailableEvents.Count == 0
        ? "Every event type is available."
        : "Unavailable with the current sign-in: " + string.Join(", ", Twitch.UnavailableEvents);

    public bool NeedsSignIn => Scopes.Any(s => s.IsRequested && !s.IsGranted) && Twitch.IsAuthorized;

    public override void Refresh()
    {
        base.Refresh();
        var granted = Twitch.Settings.Scopes.ToHashSet(StringComparer.Ordinal);
        foreach (var scope in Scopes) scope.IsGranted = granted.Contains(scope.Scope);
        OnPropertyChanged(nameof(RequestedSummary));
        OnPropertyChanged(nameof(UnavailableEvents));
        OnPropertyChanged(nameof(NeedsSignIn));
        OnPropertyChanged(nameof(SubCount));
        OnPropertyChanged(nameof(SubRolloverCount));
    }

    [RelayCommand]
    private void RequestAll() => SetAll(_ => true);

    [RelayCommand]
    private void RequestMinimal() => SetAll(s => TwitchScopes.Minimal.Contains(s.Scope));

    private void SetAll(Func<TwitchScopeItem, bool> requested)
    {
        _loading = true;
        foreach (var scope in Scopes) scope.IsRequested = scope.IsRequired || requested(scope);
        _loading = false;
        SaveScopes();
    }

    private void SaveScopes()
    {
        if (_loading) return;
        var chosen = Scopes.Where(s => s.IsRequested || s.IsRequired).Select(s => s.Scope).ToList();
        Twitch.Settings.RequestedScopes = chosen.Count == Scopes.Count ? [] : chosen;
        Twitch.SaveSettings();
        OnPropertyChanged(nameof(RequestedSummary));
        OnPropertyChanged(nameof(NeedsSignIn));
    }
}
