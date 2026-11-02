using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Spending;

namespace Cuebot.App.ViewModels.Extras;

public sealed class TopSpenderRowViewModel(TopSpender spender, string currency)
{
    public TopSpender Spender => spender;
    public string Rank => spender.Rank.ToString(CultureInfo.InvariantCulture);
    public string User => spender.User;
    public string Platform => spender.Platform;
    public string Total => SpendingText.Money(spender.Total, currency);
    public string Count => spender.Count == 1 ? "1 purchase" : $"{spender.Count} purchases";

    public string Breakdown => string.Join(", ", spender.Breakdown.OrderByDescending(b => b.Value)
        .Select(b => $"{b.Key} {SpendingText.Money(b.Value, currency)}"));
}

/// <summary>Top spenders per stream, day, week, month, year or all time, per platform, and the spending settings.</summary>
public sealed partial class TopSpendersViewModel : PageViewModel
{
    public const string PageTitle = "Top Spenders";
    public const string AllPlatforms = "All platforms";
    public static IReadOnlyList<string> Periods => SpendingPeriods.Choices;
    public static IReadOnlyList<DayOfWeek> WeekDays { get; } = [DayOfWeek.Monday, DayOfWeek.Sunday, DayOfWeek.Saturday];

    private readonly SpendingLedger _ledger;
    private bool _loading;

    public TopSpendersViewModel(SpendingLedger ledger) : base(PageTitle)
    {
        _ledger = ledger;
        var s = ledger.Settings;
        _loading = true;
        Currency = s.Currency;
        BitUsd = (decimal)s.BitUsd;
        KickUsd = (decimal)s.KickUsd;
        JewelUsd = (decimal)s.JewelUsd;
        TwitchTier1Usd = (decimal)s.TwitchTier1Usd;
        TwitchTier2Usd = (decimal)s.TwitchTier2Usd;
        TwitchTier3Usd = (decimal)s.TwitchTier3Usd;
        KickSubscriptionUsd = (decimal)s.KickSubscriptionUsd;
        YouTubeMembershipUsd = (decimal)s.YouTubeMembershipUsd;
        OtherSubscriptionUsd = (decimal)s.OtherSubscriptionUsd;
        IncludeCharity = s.IncludeCharity;
        IgnoredUsers = string.Join(", ", s.IgnoredUsers);
        WeekStart = s.WeekStart;
        _loading = false;
        ledger.Changed += () => OnUi(Refresh);
        Refresh();
    }

    public ObservableCollection<TopSpenderRowViewModel> Rows { get; } = [];
    public ObservableCollection<string> Platforms { get; } = [AllPlatforms];

    [ObservableProperty] private string _period = SpendingPeriods.Choices[0];
    [ObservableProperty] private string _platform = AllPlatforms;
    [ObservableProperty] private TopSpenderRowViewModel? _selected;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _ratesText = "";
    [ObservableProperty] private string _currencyError = "";
    [ObservableProperty] private bool _confirmClear;

    [ObservableProperty] private string _currency = "USD";
    [ObservableProperty] private decimal? _bitUsd;
    [ObservableProperty] private decimal? _kickUsd;
    [ObservableProperty] private decimal? _jewelUsd;
    [ObservableProperty] private decimal? _twitchTier1Usd;
    [ObservableProperty] private decimal? _twitchTier2Usd;
    [ObservableProperty] private decimal? _twitchTier3Usd;
    [ObservableProperty] private decimal? _kickSubscriptionUsd;
    [ObservableProperty] private decimal? _youTubeMembershipUsd;
    [ObservableProperty] private decimal? _otherSubscriptionUsd;
    [ObservableProperty] private bool _includeCharity;
    [ObservableProperty] private string _ignoredUsers = "";
    [ObservableProperty] private DayOfWeek _weekStart = DayOfWeek.Monday;

    public bool IsEmpty => Rows.Count == 0;
    public bool HasSelection => Selected is not null;

    partial void OnPeriodChanged(string value) => Refresh();
    partial void OnPlatformChanged(string value) => Refresh();
    partial void OnSelectedChanged(TopSpenderRowViewModel? value) => OnPropertyChanged(nameof(HasSelection));

    partial void OnCurrencyChanged(string value)
    {
        var code = value.Trim().ToUpperInvariant();
        CurrencyError = code.Length == 3 && code.All(char.IsAsciiLetterUpper) ? "" : "Use a three-letter code, like USD, EUR or GBP.";
        if (CurrencyError.Length == 0) Save();
    }

    partial void OnBitUsdChanged(decimal? value) => Save();
    partial void OnKickUsdChanged(decimal? value) => Save();
    partial void OnJewelUsdChanged(decimal? value) => Save();
    partial void OnTwitchTier1UsdChanged(decimal? value) => Save();
    partial void OnTwitchTier2UsdChanged(decimal? value) => Save();
    partial void OnTwitchTier3UsdChanged(decimal? value) => Save();
    partial void OnKickSubscriptionUsdChanged(decimal? value) => Save();
    partial void OnYouTubeMembershipUsdChanged(decimal? value) => Save();
    partial void OnOtherSubscriptionUsdChanged(decimal? value) => Save();
    partial void OnIncludeCharityChanged(bool value) => Save();
    partial void OnIgnoredUsersChanged(string value) => Save();
    partial void OnWeekStartChanged(DayOfWeek value) => Save();

    private void Save()
    {
        if (_loading) return;
        static double Price(decimal? value, double fallback) => value is { } v && v >= 0 ? (double)v : fallback;
        _ledger.UpdateSettings(s =>
        {
            if (CurrencyError.Length == 0) s.Currency = Currency.Trim().ToUpperInvariant();
            s.BitUsd = Price(BitUsd, s.BitUsd);
            s.KickUsd = Price(KickUsd, s.KickUsd);
            s.JewelUsd = Price(JewelUsd, s.JewelUsd);
            s.TwitchTier1Usd = Price(TwitchTier1Usd, s.TwitchTier1Usd);
            s.TwitchTier2Usd = Price(TwitchTier2Usd, s.TwitchTier2Usd);
            s.TwitchTier3Usd = Price(TwitchTier3Usd, s.TwitchTier3Usd);
            s.KickSubscriptionUsd = Price(KickSubscriptionUsd, s.KickSubscriptionUsd);
            s.YouTubeMembershipUsd = Price(YouTubeMembershipUsd, s.YouTubeMembershipUsd);
            s.OtherSubscriptionUsd = Price(OtherSubscriptionUsd, s.OtherSubscriptionUsd);
            s.IncludeCharity = IncludeCharity;
            s.WeekStart = WeekStart;
            s.IgnoredUsers = IgnoredUsers.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(u => u.TrimStart('@')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        });
    }

    internal void Refresh()
    {
        foreach (var p in _ledger.Platforms())
            if (!Platforms.Contains(p, StringComparer.OrdinalIgnoreCase)) Platforms.Add(p);
        var period = SpendingPeriods.Parse(Period);
        var list = _ledger.Top(period, Platform == AllPlatforms ? "" : Platform, 50);
        Rows.Clear();
        foreach (var s in list.Spenders) Rows.Add(new TopSpenderRowViewModel(s, list.Currency));
        Summary = list.Spenders.Count == 0
            ? $"Nobody has spent anything {Describe(period)} yet."
            : $"{list.Spenders.Count} spender(s) {Describe(period)}, {SpendingText.Money(list.Total, list.Currency)} in all."
              + (list.Pending > 0 ? $" {list.Pending} purchase(s) are waiting for exchange rates." : "");
        var stream = _ledger.CurrentSession;
        RatesText = stream is null
            ? "No stream yet. The exchange rates are fetched and locked when your next stream starts."
            : stream.Rates.Count == 0
                ? $"This stream started {Local(stream.StartedAt)}. Waiting for exchange rates; purchases in other currencies are counted when they arrive."
                : $"This stream started {Local(stream.StartedAt)}. Its exchange rates are locked: {stream.RatesSource}, {stream.RatesDate}.";
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static string Describe(SpendingPeriod period) => period switch
    {
        SpendingPeriod.Stream => "this stream",
        SpendingPeriod.Day => "today",
        SpendingPeriod.Week => "this week",
        SpendingPeriod.Month => "this month",
        SpendingPeriod.Year => "this year",
        _ => "so far",
    };

    private static string Local(DateTimeOffset time) => time.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);

    [RelayCommand]
    private void NewStream() => _ledger.StartSession();

    [RelayCommand]
    private void RemoveSelected()
    {
        if (Selected is not { } row) return;
        _ledger.RemoveUser(row.Platform, row.User, SpendingPeriods.Parse(Period));
        Selected = null;
    }

    [RelayCommand]
    private void ClearAll()
    {
        if (!ConfirmClear)
        {
            ConfirmClear = true;   // the button asks to be clicked again
            return;
        }
        ConfirmClear = false;
        _ledger.ClearAll();
    }
}
