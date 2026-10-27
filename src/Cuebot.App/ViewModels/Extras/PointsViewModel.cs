using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Points;

namespace Cuebot.App.ViewModels.Extras;

public sealed class PointsRowViewModel(PointsEntry entry, string formatted)
{
    public string Rank => entry.Rank.ToString(CultureInfo.InvariantCulture);
    public string UserName => entry.UserName;
    public string Platform => entry.Platform;
    public string Points => formatted;
    public long Value => entry.Points;
}

/// <summary>Loyalty currency: leaderboard, manual adjustments and the earning settings.</summary>
public sealed partial class PointsViewModel : PageViewModel
{
    public static readonly IReadOnlyList<string> PlatformChoices = ["All", "twitch", "youtube", "kick", "rumble"];

    private readonly PointsService _points;
    private bool _loading;
    private bool _resetArmed;
    private int _refreshQueued;

    public PointsViewModel(PointsService points) : base("Points")
    {
        _points = points;
        Load();
        points.Changed += _ =>
        {
            // Payouts change many balances at once: refresh once per burst.
            if (Interlocked.Exchange(ref _refreshQueued, 1) == 0) Avalonia.Threading.Dispatcher.UIThread.Post(RefreshLeaderboard);
        };
        RefreshLeaderboard();
    }

    public ObservableCollection<PointsRowViewModel> Leaderboard { get; } = [];

    // ---- settings ----
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _currencyName = "";
    [ObservableProperty] private string _singularName = "";
    [ObservableProperty] private string _symbol = "";
    [ObservableProperty] private string _variableName = "";
    [ObservableProperty] private bool _onlyWhileLive;
    [ObservableProperty] private decimal? _presentAmount;
    [ObservableProperty] private decimal? _presentIntervalMinutes;
    [ObservableProperty] private decimal? _messageAmount;
    [ObservableProperty] private decimal? _messageCooldownSeconds;
    [ObservableProperty] private decimal? _followBonus;
    [ObservableProperty] private decimal? _subscriptionBonus;
    [ObservableProperty] private decimal? _giftBonusPerSub;
    [ObservableProperty] private decimal? _raidBonus;
    [ObservableProperty] private decimal? _maxBalance;
    [ObservableProperty] private string _thresholds = "";
    [ObservableProperty] private string _ignoredUsers = "";
    [ObservableProperty] private string _thresholdsError = "";

    // ---- leaderboard / adjust ----
    [ObservableProperty] private string _platformFilter = "All";
    [ObservableProperty] private string _adjustPlatform = "twitch";
    [ObservableProperty] private string _adjustUser = "";
    [ObservableProperty] private decimal? _adjustAmount = 100;
    [ObservableProperty] private string _status = "";

    public IReadOnlyList<string> Platforms => PlatformChoices;
    public IReadOnlyList<string> AdjustPlatforms { get; } = PlatformChoices.Skip(1).ToArray();

    public string Preview => $"Example: {_points.Format(1250)} · {_points.Format(1)}";

    public string LiveStatus => _points.LivePlatforms is { Count: > 0 } live
        ? "Live on " + string.Join(", ", live)
        : OnlyWhileLive ? "Not live: presence and chat earn nothing until a platform reports the stream online" : "Earning all the time";

    public bool IsEmpty => Leaderboard.Count == 0;

    private void Load()
    {
        _loading = true;
        var s = _points.Settings;
        Enabled = s.Enabled;
        CurrencyName = s.Name;
        SingularName = s.SingularName;
        Symbol = s.Symbol;
        VariableName = s.VariableName;
        OnlyWhileLive = s.OnlyWhileLive;
        PresentAmount = s.PresentAmount;
        PresentIntervalMinutes = s.PresentIntervalMinutes;
        MessageAmount = s.MessageAmount;
        MessageCooldownSeconds = s.MessageCooldownSeconds;
        FollowBonus = s.FollowBonus;
        SubscriptionBonus = s.SubscriptionBonus;
        GiftBonusPerSub = s.GiftBonusPerSub;
        RaidBonus = s.RaidBonus;
        MaxBalance = s.MaxBalance;
        Thresholds = string.Join(", ", s.Thresholds);
        IgnoredUsers = string.Join(", ", s.IgnoredUsers);
        _loading = false;
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null) return;
        if (e.PropertyName is nameof(PlatformFilter)) RefreshLeaderboard();
        else if (e.PropertyName is not (nameof(Status) or nameof(AdjustUser) or nameof(AdjustAmount) or nameof(AdjustPlatform)
                 or nameof(ThresholdsError) or nameof(Preview) or nameof(LiveStatus) or nameof(IsEmpty))) Save();
    }

    private static long L(decimal? value) => (long)Math.Max(0, value ?? 0);

    private void Save()
    {
        var thresholds = new List<long>();
        var error = "";
        foreach (var part in Thresholds.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
        {
            if (long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var t) && t > 0) thresholds.Add(t);
            else error = $"'{part}' is not a positive whole number";
        }
        ThresholdsError = error;
        _points.UpdateSettings(s =>
        {
            s.Enabled = Enabled;
            s.Name = CurrencyName.Trim();
            s.SingularName = SingularName.Trim();
            s.Symbol = Symbol.Trim();
            if (VariableName.Trim().Length > 0 && !VariableName.Trim().Any(char.IsWhiteSpace)) s.VariableName = VariableName.Trim();
            s.OnlyWhileLive = OnlyWhileLive;
            s.PresentAmount = L(PresentAmount);
            s.PresentIntervalMinutes = (int)Math.Max(1, PresentIntervalMinutes ?? 1);
            s.MessageAmount = L(MessageAmount);
            s.MessageCooldownSeconds = (int)L(MessageCooldownSeconds);
            s.FollowBonus = L(FollowBonus);
            s.SubscriptionBonus = L(SubscriptionBonus);
            s.GiftBonusPerSub = L(GiftBonusPerSub);
            s.RaidBonus = L(RaidBonus);
            s.MaxBalance = L(MaxBalance);
            s.Thresholds = thresholds.Distinct().Order().ToList();
            s.IgnoredUsers = IgnoredUsers.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(u => u.TrimStart('@')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        });
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(LiveStatus));
        RefreshLeaderboard();
    }

    [RelayCommand]
    internal void RefreshLeaderboard()
    {
        Interlocked.Exchange(ref _refreshQueued, 0);
        Leaderboard.Clear();
        foreach (var entry in _points.Leaderboard(PlatformFilter == "All" ? null : PlatformFilter, 100))
            Leaderboard.Add(new PointsRowViewModel(entry, _points.Format(entry.Points)));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(LiveStatus));
    }

    private bool TryAdjustTarget(out string userId)
    {
        userId = _points.FindUserId(AdjustPlatform, AdjustUser) ?? "";
        if (userId.Length > 0) return true;
        Status = AdjustUser.Trim().Length == 0 ? "Type a user name first." : $"Unknown user '{AdjustUser.Trim()}' on {AdjustPlatform}: they need to chat first.";
        return false;
    }

    [RelayCommand]
    private void Give()
    {
        if (!TryAdjustTarget(out var id)) return;
        var balance = _points.Add(AdjustPlatform, id, AdjustUser.Trim().TrimStart('@'), L(AdjustAmount));
        Status = $"{AdjustUser.Trim().TrimStart('@')} now has {_points.Format(balance)}.";
    }

    [RelayCommand]
    private void Take()
    {
        if (!TryAdjustTarget(out var id)) return;
        var balance = _points.Remove(AdjustPlatform, id, AdjustUser.Trim().TrimStart('@'), L(AdjustAmount));
        Status = $"{AdjustUser.Trim().TrimStart('@')} now has {_points.Format(balance)}.";
    }

    [RelayCommand]
    private void ResetAll()
    {
        if (!_resetArmed)
        {
            _resetArmed = true;
            Status = "Click 'Reset balances' again to delete " + (PlatformFilter == "All" ? "every balance." : $"every {PlatformFilter} balance.");
            return;
        }
        _resetArmed = false;
        var removed = _points.ResetAll(PlatformFilter == "All" ? null : PlatformFilter);
        Status = $"Reset {removed} balance(s).";
        RefreshLeaderboard();
    }
}
