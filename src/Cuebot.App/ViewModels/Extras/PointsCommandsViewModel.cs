using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Commands;
using Cuebot.Core.Points;
using Cuebot.Core.Spending;
using Cuebot.Core.Users;

namespace Cuebot.App.ViewModels.Extras;

/// <summary>One editable reply of a built-in command ("win", "lose"...).</summary>
public sealed partial class PointsReplyViewModel(string key, string text, Action<string, string> save) : ObservableObject
{
    public string Key { get; } = key;

    [ObservableProperty] private string _text = text;

    partial void OnTextChanged(string value) => save(Key, value);
}

/// <summary>One built-in command row.</summary>
public sealed partial class PointsCommandRowViewModel : ObservableObject
{
    private readonly PointsChatCommands _commands;
    private bool _loading = true;

    public PointsCommandRowViewModel(PointsChatCommands commands, PointsCommand c)
    {
        _commands = commands;
        Kind = c.Kind;
        _enabled = c.Enabled;
        _words = c.Words;
        _minimumRole = c.MinimumRole;
        _cooldownSeconds = c.CooldownSeconds;
        _userCooldownSeconds = c.UserCooldownSeconds;
        foreach (var (key, text) in c.Replies)
            Replies.Add(new PointsReplyViewModel(key, text, (k, t) => _commands.UpdateCommand(Kind, x => x.Replies[k] = t)));
        _loading = false;
    }

    public PointsCommandKind Kind { get; }
    public string Name => PointsCommandsViewModel.NameOf(Kind);
    public string Description => PointsCommandsViewModel.DescriptionOf(Kind);
    public ObservableCollection<PointsReplyViewModel> Replies { get; } = [];
    public static IReadOnlyList<ChatPermission> Roles { get; } = Enum.GetValues<ChatPermission>();

    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _words;
    [ObservableProperty] private ChatPermission _minimumRole;
    [ObservableProperty] private decimal? _cooldownSeconds;
    [ObservableProperty] private decimal? _userCooldownSeconds;

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading) return;
        if (e.PropertyName is nameof(Enabled) or nameof(Words) or nameof(MinimumRole) or nameof(CooldownSeconds) or nameof(UserCooldownSeconds))
        {
            _commands.UpdateCommand(Kind, c =>
            {
                c.Enabled = Enabled;
                if (Words.Trim().Length > 0) c.Words = Words.Trim();
                c.MinimumRole = MinimumRole;
                c.CooldownSeconds = (int)Math.Max(0, CooldownSeconds ?? 0);
                c.UserCooldownSeconds = (int)Math.Max(0, UserCooldownSeconds ?? 0);
            });
        }
    }

    [RelayCommand]
    private void ResetReplies()
    {
        var defaults = PointsChatCommands.Default(Kind).Replies;
        _commands.UpdateCommand(Kind, c => c.Replies = new(defaults));
        foreach (var r in Replies)
            if (defaults.TryGetValue(r.Key, out var text)) r.Text = text;
    }
}

/// <summary>A link group: one viewer's accounts on several platforms.</summary>
public sealed class LinkGroupRowViewModel(AccountLinkGroup group, string balance)
{
    public AccountLinkGroup Group { get; } = group;
    public string Accounts => string.Join("  +  ", Group.Accounts.Select(a => $"{a.UserName} ({AccountLinks.DisplayName(a.Platform)})"));
    public string Balance { get; } = balance;
    public string LinkedAt => Group.LinkedAt == default ? "" : Group.LinkedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
}

/// <summary>
/// Chat > Games &amp; Commands: the points commands Cuebot answers itself, the games' rules and viewers' linked
/// accounts.
/// </summary>
public sealed partial class PointsCommandsViewModel : PageViewModel
{
    public const string PageTitle = "Games & Commands";

    private readonly PointsChatCommands _commands;
    private readonly PointsService _points;
    private readonly AccountLinks _links;
    private bool _loading;

    public PointsCommandsViewModel(PointsChatCommands commands, PointsService points, AccountLinks links) : base(PageTitle)
    {
        _commands = commands;
        _points = points;
        _links = links;
        Load();
        links.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(RefreshLinks);
        RefreshLinks();
    }

    public override IReadOnlyList<string> SubPages { get; } = ["Commands", "Games", "Linked accounts"];

    [ObservableProperty] private int _selectedTab;

    public override int SubPageIndex
    {
        get => SelectedTab;
        set
        {
            if (value >= 0) SelectedTab = value;
        }
    }

    partial void OnSelectedTabChanged(int value)
    {
        OnPropertyChanged(nameof(SubPageIndex));
        OnPropertyChanged(nameof(SubPageTitle));
        if (value == 2) RefreshLinks();
    }

    public ObservableCollection<PointsCommandRowViewModel> Commands { get; } = [];
    public IEnumerable<PointsCommandRowViewModel> GameCommands => Commands.Where(c => IsGame(c.Kind));
    public IEnumerable<PointsCommandRowViewModel> ChatCommands => Commands.Where(c => !IsGame(c.Kind));
    public ObservableCollection<LinkGroupRowViewModel> Links { get; } = [];
    public IReadOnlyList<string> CurrencyChoices => _points.CurrencyNames;
    public static IReadOnlyList<string> Periods => SpendingPeriods.Choices;
    public static IReadOnlyList<string> LinkPlatforms { get; } = ["twitch", "kick", "youtube", "rumble"];

    [ObservableProperty] private string _currency = "";
    [ObservableProperty] private decimal? _leaderboardSize;
    [ObservableProperty] private string _supportPeriod = SpendingPeriods.Choices[^1];
    [ObservableProperty] private decimal? _minimumBet;
    [ObservableProperty] private decimal? _maximumBet;
    [ObservableProperty] private decimal? _gambleWinPercent;
    [ObservableProperty] private decimal? _gambleJackpotPercent;
    [ObservableProperty] private string _slotSymbols = "";
    [ObservableProperty] private decimal? _slotsTripleMultiplier;
    [ObservableProperty] private decimal? _slotsPairMultiplier;
    [ObservableProperty] private decimal? _duelSeconds;
    [ObservableProperty] private decimal? _linkRequestMinutes;

    [ObservableProperty] private string _linkPlatformA = "twitch";
    [ObservableProperty] private string _linkNameA = "";
    [ObservableProperty] private string _linkPlatformB = "kick";
    [ObservableProperty] private string _linkNameB = "";
    [ObservableProperty] private string _status = "";

    public bool HasNoLinks => Links.Count == 0;

    /// <summary>What the slot machine and !gamble give back on average, so streamers can see the house edge.</summary>
    public string GameOdds
    {
        get
        {
            var win = (double)(GambleWinPercent ?? 0) / 100;
            var jackpot = Math.Min(win, (double)(GambleJackpotPercent ?? 0) / 100);
            var gamble = (win - jackpot) * 2 + jackpot * 3;
            var n = Math.Max(2, SlotSymbols.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length);
            var triple = 1.0 / (n * n);
            var pair = 3.0 * (n - 1) / (n * n);
            var slots = triple * (double)(SlotsTripleMultiplier ?? 0) + pair * (double)(SlotsPairMultiplier ?? 0);
            return $"On average, !gamble pays back {gamble:P0} of a bet and !slots {slots:P0}. Under 100% means viewers slowly lose points to the house.";
        }
    }

    private static readonly HashSet<string> NotSettings =
    [
        nameof(Status), nameof(SelectedTab), nameof(SubPageIndex), nameof(SubPageTitle), nameof(LinkPlatformA), nameof(LinkNameA),
        nameof(LinkPlatformB), nameof(LinkNameB), nameof(HasNoLinks), nameof(GameOdds), nameof(LinkRequestMinutes),
    ];

    internal static bool IsGame(PointsCommandKind kind) =>
        kind is PointsCommandKind.Gamble or PointsCommandKind.Slots or PointsCommandKind.Duel or PointsCommandKind.Accept;

    public static string NameOf(PointsCommandKind kind) => kind switch
    {
        PointsCommandKind.Points => "Points balance",
        PointsCommandKind.Leaderboard => "Leaderboard (all platforms)",
        PointsCommandKind.LeaderboardTwitch => "Leaderboard (Twitch)",
        PointsCommandKind.LeaderboardKick => "Leaderboard (Kick)",
        PointsCommandKind.LeaderboardYouTube => "Leaderboard (YouTube)",
        PointsCommandKind.LeaderboardRumble => "Leaderboard (Rumble)",
        PointsCommandKind.Give => "Give points",
        PointsCommandKind.LinkTwitch => "Link a Twitch account",
        PointsCommandKind.LinkKick => "Link a Kick account",
        PointsCommandKind.LinkYouTube => "Link a YouTube account",
        PointsCommandKind.LinkRumble => "Link a Rumble account",
        PointsCommandKind.Unlink => "Unlink an account",
        PointsCommandKind.Support => "Support total",
        PointsCommandKind.TopSupporters => "Top supporters",
        PointsCommandKind.Gamble => "Gamble",
        PointsCommandKind.Slots => "Slots",
        PointsCommandKind.Duel => "Duel",
        PointsCommandKind.Accept => "Accept a duel",
        _ => kind.ToString(),
    };

    public static string DescriptionOf(PointsCommandKind kind) => kind switch
    {
        PointsCommandKind.Points => "A viewer's balance and rank, with each linked platform's share. Add a name to look someone else up.",
        PointsCommandKind.Leaderboard => "The top viewers, with linked accounts counted together.",
        PointsCommandKind.LeaderboardTwitch or PointsCommandKind.LeaderboardKick or PointsCommandKind.LeaderboardYouTube
            or PointsCommandKind.LeaderboardRumble => "The top viewers on one platform, by what they earned there.",
        PointsCommandKind.Give => "\"!give name 50\" moves points to another viewer on the same platform.",
        PointsCommandKind.LinkTwitch or PointsCommandKind.LinkKick or PointsCommandKind.LinkYouTube or PointsCommandKind.LinkRumble =>
            "Asks to link the viewer's account on that platform. They finish from the other platform's chat, so nobody can link someone else's account.",
        PointsCommandKind.Unlink => "\"!unlink kick\" takes an account out of the viewer's linked accounts. \"!unlink\" alone takes out the one they type from.",
        PointsCommandKind.Support => "How much a viewer has given (bits, subs, gifted subs, Kicks, rants, Super Chats, tips), valued with the Top Spenders prices. Off until you turn it on.",
        PointsCommandKind.TopSupporters => "The viewers who have given the most, with linked accounts counted together. Off until you turn it on.",
        PointsCommandKind.Gamble => "\"!gamble 100\", \"!gamble all\" or \"!gamble 50%\": a roll from 1 to 100 doubles the bet, or triples it on a jackpot.",
        PointsCommandKind.Slots => "\"!slots 50\" spins three reels: three of a kind or a pair pays back a multiple of the bet.",
        PointsCommandKind.Duel => "\"!duel name 100\" challenges someone on the same platform. If they accept, a coin flip decides who takes both bets.",
        PointsCommandKind.Accept => "Accepts the duel someone challenged you to.",
        _ => "",
    };

    private void Load()
    {
        _loading = true;
        var s = _commands.Settings;
        Currency = s.Currency.Length == 0 ? _points.CurrencyName : _points.NameOf(s.Currency);
        LeaderboardSize = s.LeaderboardSize;
        SupportPeriod = SpendingPeriods.Label(s.SupportPeriod);
        MinimumBet = s.MinimumBet;
        MaximumBet = s.MaximumBet;
        GambleWinPercent = s.GambleWinPercent;
        GambleJackpotPercent = s.GambleJackpotPercent;
        SlotSymbols = s.SlotSymbols;
        SlotsTripleMultiplier = s.SlotsTripleMultiplier;
        SlotsPairMultiplier = s.SlotsPairMultiplier;
        DuelSeconds = s.DuelSeconds;
        LinkRequestMinutes = _links.RequestMinutes;
        Commands.Clear();
        foreach (var c in s.Commands) Commands.Add(new PointsCommandRowViewModel(_commands, c));
        _loading = false;
        OnPropertyChanged(nameof(GameCommands));
        OnPropertyChanged(nameof(ChatCommands));
        OnPropertyChanged(nameof(GameOdds));
    }

    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (_loading || e.PropertyName is null) return;
        if (e.PropertyName == nameof(LinkRequestMinutes))
        {
            _links.RequestMinutes = (int)Math.Max(1, LinkRequestMinutes ?? 10);
            return;
        }
        if (NotSettings.Contains(e.PropertyName) || e.PropertyName is nameof(Commands) or nameof(Links) or nameof(GameCommands) or nameof(ChatCommands))
            return;
        var main = _points.CurrencyName;
        _commands.UpdateSettings(s =>
        {
            s.Currency = string.Equals(Currency, main, StringComparison.OrdinalIgnoreCase) ? "" : Currency;
            s.LeaderboardSize = (int)(LeaderboardSize ?? 5);
            s.SupportPeriod = SpendingPeriods.Parse(SupportPeriod);
            s.MinimumBet = (long)(MinimumBet ?? 1);
            s.MaximumBet = (long)(MaximumBet ?? 0);
            s.GambleWinPercent = (int)(GambleWinPercent ?? 45);
            s.GambleJackpotPercent = (int)(GambleJackpotPercent ?? 0);
            s.SlotSymbols = SlotSymbols;
            s.SlotsTripleMultiplier = SlotsTripleMultiplier ?? 0;
            s.SlotsPairMultiplier = SlotsPairMultiplier ?? 0;
            s.DuelSeconds = (int)(DuelSeconds ?? 60);
        });
        OnPropertyChanged(nameof(GameOdds));
    }

    [RelayCommand]
    internal void RefreshLinks()
    {
        Links.Clear();
        foreach (var g in _links.Groups.OrderBy(g => g.Accounts.FirstOrDefault()?.UserName, StringComparer.OrdinalIgnoreCase))
        {
            var first = g.Accounts[0];
            Links.Add(new LinkGroupRowViewModel(g, _points.Format(_points.Get(first.Platform, first.UserId))));
        }
        OnPropertyChanged(nameof(HasNoLinks));
    }

    [RelayCommand]
    private void Unlink(LinkGroupRowViewModel? row)
    {
        if (row is null) return;
        foreach (var a in row.Group.Accounts) _links.Unlink(a.Platform, a.UserId);
        Status = $"Unlinked {row.Accounts}. Each account keeps what is in it.";
        RefreshLinks();
    }

    [RelayCommand]
    private void Link()
    {
        var a = Find(LinkPlatformA, LinkNameA);
        var b = Find(LinkPlatformB, LinkNameB);
        if (a is null || b is null)
        {
            Status = "Both viewers need to have chatted on their platform first. Check the names.";
            return;
        }
        var result = _links.Link(a, b);
        Status = result.Outcome switch
        {
            LinkOutcome.Linked => $"Linked {a.UserName} ({AccountLinks.DisplayName(a.Platform)}) and {b.UserName} ({AccountLinks.DisplayName(b.Platform)}).",
            LinkOutcome.AlreadyLinked => "Those accounts are already linked.",
            LinkOutcome.SamePlatform => "Pick two different platforms.",
            _ => "One of them is already linked to another account on the same platform. Unlink it first.",
        };
        if (result.Outcome == LinkOutcome.Linked) LinkNameA = LinkNameB = "";
        RefreshLinks();
    }

    private LinkedAccount? Find(string platform, string name)
    {
        name = name.Trim().TrimStart('@');
        if (name.Length == 0) return null;
        return _points.FindUserId(platform, name) is { } id ? new LinkedAccount(platform, id, name) : null;
    }

    [RelayCommand]
    private void ResetGames()
    {
        var d = new PointsCommandsSettings();
        _commands.UpdateSettings(s =>
        {
            s.MinimumBet = d.MinimumBet;
            s.MaximumBet = d.MaximumBet;
            s.GambleWinPercent = d.GambleWinPercent;
            s.GambleJackpotPercent = d.GambleJackpotPercent;
            s.SlotSymbols = d.SlotSymbols;
            s.SlotsTripleMultiplier = d.SlotsTripleMultiplier;
            s.SlotsPairMultiplier = d.SlotsPairMultiplier;
            s.DuelSeconds = d.DuelSeconds;
        });
        Load();
        Status = "The games' rules are back to how Cuebot ships them.";
    }
}
