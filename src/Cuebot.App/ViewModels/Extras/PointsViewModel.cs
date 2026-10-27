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

/// <summary>One currency in the picker. <see cref="Key"/> is its variable name (what the service finds it by while it is renamed).</summary>
public sealed partial class CurrencyOptionViewModel(string key, string name, bool isDefault) : ObservableObject
{
    public string Key { get; set; } = key;
    public bool IsDefault { get; } = isDefault;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Display))]
    private string _name = name;

    public string Display => IsDefault ? Name + " (default)" : Name;
}

/// <summary>Loyalty currencies: pick one, see its leaderboard, adjust balances and edit its earning rules and multipliers.</summary>
public sealed partial class PointsViewModel : PageViewModel
{
    public static readonly IReadOnlyList<string> PlatformChoices = ["All", "twitch", "youtube", "kick", "rumble"];

    private readonly PointsService _points;
    private bool _loading;
    private bool _resetArmed;
    private int _refreshQueued;
    private string? _saveError;

    public PointsViewModel(PointsService points) : base("Points")
    {
        _points = points;
        foreach (var c in points.Currencies) Currencies.Add(new CurrencyOptionViewModel(c.VariableName, c.Name, Currencies.Count == 0));
        _selectedCurrency = Currencies[0];
        Load();
        points.Changed += _ =>
        {
            // Payouts change many balances at once: refresh once per burst.
            if (Interlocked.Exchange(ref _refreshQueued, 1) == 0) Avalonia.Threading.Dispatcher.UIThread.Post(RefreshLeaderboard);
        };
        RefreshLeaderboard();
    }

    public ObservableCollection<PointsRowViewModel> Leaderboard { get; } = [];
    public ObservableCollection<CurrencyOptionViewModel> Currencies { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanRemoveCurrency))]
    private CurrencyOptionViewModel _selectedCurrency;

    [ObservableProperty] private string _newCurrencyName = "";

    public bool CanRemoveCurrency => SelectedCurrency is { IsDefault: false };

    /// <summary>The selected currency's name for service calls.</summary>
    private string Currency => SelectedCurrency?.Key ?? "";

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

    // ---- multipliers ----
    [ObservableProperty] private decimal? _subscriberMultiplier;
    [ObservableProperty] private decimal? _tier2Multiplier;
    [ObservableProperty] private decimal? _tier3Multiplier;
    [ObservableProperty] private decimal? _vipMultiplier;
    [ObservableProperty] private decimal? _moderatorMultiplier;
    [ObservableProperty] private string _groupMultipliers = "";
    [ObservableProperty] private string _groupMultipliersError = "";
    [ObservableProperty] private string _userMultipliersSummary = "";

    // ---- leaderboard / adjust ----
    [ObservableProperty] private string _platformFilter = "All";
    [ObservableProperty] private string _adjustPlatform = "twitch";
    [ObservableProperty] private string _adjustUser = "";
    [ObservableProperty] private decimal? _adjustAmount = 100;
    [ObservableProperty] private string _status = "";

    public IReadOnlyList<string> Platforms => PlatformChoices;
    public IReadOnlyList<string> AdjustPlatforms { get; } = PlatformChoices.Skip(1).ToArray();

    public string Preview => $"Example: {_points.Format(1250, Currency)} · {_points.Format(1, Currency)}";

    public string LiveStatus => _points.LivePlatforms is { Count: > 0 } live
        ? "Live on " + string.Join(", ", live)
        : OnlyWhileLive ? "Not live: presence and chat earn nothing until a platform reports the stream online" : "Earning all the time";

    public bool IsEmpty => Leaderboard.Count == 0;

    /// <summary>Properties that are not settings (changing them never saves).</summary>
    private static readonly HashSet<string> NotSettings =
    [
        nameof(Status), nameof(AdjustUser), nameof(AdjustAmount), nameof(AdjustPlatform), nameof(ThresholdsError), nameof(Preview),
        nameof(LiveStatus), nameof(IsEmpty), nameof(GroupMultipliersError), nameof(UserMultipliersSummary), nameof(NewCurrencyName),
        nameof(CanRemoveCurrency), nameof(PlatformFilter), nameof(SelectedCurrency),
    ];

    private void Load()
    {
        _loading = true;
        var s = _points.GetCurrency(Currency) ?? _points.Currencies[0];
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
        SubscriberMultiplier = s.SubscriberMultiplier;
        Tier2Multiplier = s.Tier2Multiplier;
        Tier3Multiplier = s.Tier3Multiplier;
        VipMultiplier = s.VipMultiplier;
        ModeratorMultiplier = s.ModeratorMultiplier;
        GroupMultipliers = string.Join(", ", s.GroupMultipliers.OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g => $"{g.Key}={g.Value.ToString(CultureInfo.InvariantCulture)}"));
        GroupMultipliersError = "";
        ThresholdsError = "";
        UserMultipliersSummary = s.UserMultipliers.Count == 0
            ? "No per-user multipliers (set them with the 'Set user points multiplier' sub-action)."
            : $"{s.UserMultipliers.Count} user(s) have their own multiplier (set with the 'Set user points multiplier' sub-action).";
        _loading = false;
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(LiveStatus));
    }

    partial void OnSelectedCurrencyChanged(CurrencyOptionViewModel value)
    {
        if (value is null) return;
        _resetArmed = false;
        Load();
        RefreshLeaderboard();
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null) return;
        if (e.PropertyName is nameof(PlatformFilter)) RefreshLeaderboard();
        else if (!NotSettings.Contains(e.PropertyName) && SelectedCurrency is not null) Save();
    }

    private static long L(decimal? value) => (long)Math.Max(0, value ?? 0);

    private static decimal M(decimal? value) => Math.Max(0, value ?? 1);

    /// <summary>"VIPs=2, Regulars=1.5" → group → multiplier (the error names the first bad entry).</summary>
    internal static Dictionary<string, decimal> ParseGroupMultipliers(string text, out string error)
    {
        error = "";
        var result = new Dictionary<string, decimal>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in text.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var at = part.LastIndexOfAny(['=', ':']);
            if (at > 0 && decimal.TryParse(part[(at + 1)..].Trim().TrimEnd('x', 'X'), NumberStyles.Float, CultureInfo.InvariantCulture, out var m) && m >= 0)
                result[part[..at].Trim()] = m;
            else if (error.Length == 0) error = $"'{part}' is not group=multiplier (e.g. VIPs=2)";
        }
        return result;
    }

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
        var groups = ParseGroupMultipliers(GroupMultipliers, out var groupError);
        GroupMultipliersError = groupError;
        var name = CurrencyName.Trim();
        var variable = VariableName.Trim();
        try
        {
            _points.UpdateCurrency(Currency, s =>
            {
                s.Enabled = Enabled;
                if (name.Length > 0) s.Name = name;
                s.SingularName = SingularName.Trim();
                s.Symbol = Symbol.Trim();
                if (variable.Length > 0 && !variable.Any(char.IsWhiteSpace)) s.VariableName = variable;
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
                s.SubscriberMultiplier = M(SubscriberMultiplier);
                s.Tier2Multiplier = M(Tier2Multiplier);
                s.Tier3Multiplier = M(Tier3Multiplier);
                s.VipMultiplier = M(VipMultiplier);
                s.ModeratorMultiplier = M(ModeratorMultiplier);
                if (groupError.Length == 0) s.GroupMultipliers = groups;
            });
        }
        catch (InvalidOperationException ex)
        {
            Status = _saveError = ex.Message;
            return;
        }
        if (_saveError is not null && Status == _saveError) Status = "";
        _saveError = null;
        // The currency may have been renamed: keep the picker pointing at it.
        if (name.Length > 0) SelectedCurrency.Name = name;
        if (variable.Length > 0 && !variable.Any(char.IsWhiteSpace)) SelectedCurrency.Key = variable;
        OnPropertyChanged(nameof(Preview));
        OnPropertyChanged(nameof(LiveStatus));
        RefreshLeaderboard();
    }

    [RelayCommand]
    private void AddCurrency()
    {
        var name = NewCurrencyName.Trim();
        if (name.Length == 0)
        {
            Status = "Type a name for the new currency first.";
            return;
        }
        try
        {
            var added = _points.AddCurrency(name);
            var option = new CurrencyOptionViewModel(added.VariableName, added.Name, false);
            Currencies.Add(option);
            NewCurrencyName = "";
            SelectedCurrency = option;
            Status = $"Added {added.Name} (balances in the user variable '{added.VariableName}').";
        }
        catch (InvalidOperationException ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void RemoveCurrency()
    {
        if (SelectedCurrency is not { IsDefault: false } option) return;
        if (!_points.RemoveCurrency(option.Key)) return;
        Currencies.Remove(option);
        SelectedCurrency = Currencies[0];
        Status = $"Removed {option.Name}. Balances stay in the user variable '{option.Key}'.";
    }

    [RelayCommand]
    internal void RefreshLeaderboard()
    {
        Interlocked.Exchange(ref _refreshQueued, 0);
        Leaderboard.Clear();
        if (!_points.CurrencyExists(Currency)) return;
        foreach (var entry in _points.Leaderboard(PlatformFilter == "All" ? null : PlatformFilter, 100, Currency))
            Leaderboard.Add(new PointsRowViewModel(entry, _points.Format(entry.Points, Currency)));
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
        var balance = _points.Add(AdjustPlatform, id, AdjustUser.Trim().TrimStart('@'), L(AdjustAmount), currency: Currency);
        Status = $"{AdjustUser.Trim().TrimStart('@')} now has {_points.Format(balance, Currency)}.";
    }

    [RelayCommand]
    private void Take()
    {
        if (!TryAdjustTarget(out var id)) return;
        var balance = _points.Remove(AdjustPlatform, id, AdjustUser.Trim().TrimStart('@'), L(AdjustAmount), Currency);
        Status = $"{AdjustUser.Trim().TrimStart('@')} now has {_points.Format(balance, Currency)}.";
    }

    [RelayCommand]
    private void ResetAll()
    {
        if (!_resetArmed)
        {
            _resetArmed = true;
            Status = $"Click 'Reset balances' again to delete every {(PlatformFilter == "All" ? "" : PlatformFilter + " ")}{SelectedCurrency.Name} balance.";
            return;
        }
        _resetArmed = false;
        var removed = _points.ResetAll(PlatformFilter == "All" ? null : PlatformFilter, Currency);
        Status = $"Reset {removed} balance(s).";
        RefreshLeaderboard();
    }
}
