using System.Globalization;
using System.Text;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Spending;
using Cuebot.Core.Storage;
using Cuebot.Core.Users;
using Cuebot.Core.Variables;

namespace Cuebot.Core.Points;

/// <summary>The chat commands Cuebot answers itself.</summary>
public enum PointsCommandKind
{
    Points,
    Leaderboard,
    LeaderboardTwitch,
    LeaderboardKick,
    LeaderboardYouTube,
    LeaderboardRumble,
    Give,
    LinkTwitch,
    LinkKick,
    LinkYouTube,
    LinkRumble,
    Unlink,
    Support,
    TopSupporters,
    Gamble,
    Slots,
    Duel,
    Accept,
}

/// <summary>One built-in command: its words, who may use it, cooldowns and replies.</summary>
public sealed class PointsCommand
{
    public PointsCommandKind Kind { get; set; }
    public bool Enabled { get; set; } = true;

    /// <summary>The words that run it, separated by commas or spaces ("!top, !leaderboard").</summary>
    public string Words { get; set; } = "";

    public ChatPermission MinimumRole { get; set; } = ChatPermission.Everyone;

    /// <summary>Seconds before anyone can use it again (0 = no wait).</summary>
    public int CooldownSeconds { get; set; }

    /// <summary>Seconds before the same viewer can use it again.</summary>
    public int UserCooldownSeconds { get; set; }

    /// <summary>What it says, by outcome ("win", "lose"...). Words in braces are filled in ({user}, {balance}...).</summary>
    public Dictionary<string, string> Replies { get; set; } = [];

    public IReadOnlyList<string> WordList() =>
        Words.Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}

/// <summary>points-commands.json: the built-in points commands and the games' rules.</summary>
public sealed class PointsCommandsSettings
{
    /// <summary>Currency the commands and games use; empty = the main one.</summary>
    public string Currency { get; set; } = "";

    /// <summary>How many people the leaderboard commands list.</summary>
    public int LeaderboardSize { get; set; } = 5;

    /// <summary>The period !topsupporters and !support count.</summary>
    public SpendingPeriod SupportPeriod { get; set; } = SpendingPeriod.AllTime;

    /// <summary>Smallest bet for the games.</summary>
    public long MinimumBet { get; set; } = 10;

    /// <summary>Largest bet for the games (0 = no limit).</summary>
    public long MaximumBet { get; set; }

    /// <summary>Chance in percent that !gamble doubles the bet.</summary>
    public int GambleWinPercent { get; set; } = 45;

    /// <summary>Chance in percent (part of the win chance) that !gamble triples the bet instead.</summary>
    public int GambleJackpotPercent { get; set; } = 2;

    /// <summary>The slot machine's symbols, separated by spaces.</summary>
    public string SlotSymbols { get; set; } = "🍒 🍋 🔔 ⭐ 💎 7️⃣";

    /// <summary>What three of a kind pays back, as a multiple of the bet.</summary>
    public decimal SlotsTripleMultiplier { get; set; } = 10;

    /// <summary>What two of a kind pays back, as a multiple of the bet.</summary>
    public decimal SlotsPairMultiplier { get; set; } = 1.5m;

    /// <summary>Seconds a duel waits for the other viewer to accept.</summary>
    public int DuelSeconds { get; set; } = 60;

    public List<PointsCommand> Commands { get; set; } = [];
}

/// <summary>
/// Cuebot's own points commands, on every platform: !points, the leaderboards (all platforms and one each),
/// !give, account linking (!linktwitch, !linkkick, !linkyt, !linkrumble, !unlink), support totals and the games
/// (!gamble, !slots, !duel / !accept). Each can be renamed, limited, slowed down or turned off; replies are editable.
/// A command the streamer made with the same word wins: Cuebot stays quiet for it.
/// </summary>
public sealed class PointsChatCommands : IDisposable
{
    private readonly PointsService _points;
    private readonly AccountLinks? _links;
    private readonly SpendingLedger? _spending;
    private readonly CommandStore? _commands;
    private readonly Actions.ProfileService? _profile;
    private readonly JsonFile<PointsCommandsSettings>? _file;
    private readonly PointsCommandsSettings _settings;
    private readonly AppLog _log;
    private readonly Func<string, string, Task> _send;
    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly IDisposable _subscription;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _cooldowns = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Duel> _duels = [];

    private sealed record Duel(string Platform, string FromId, string FromName, string ToId, string ToName, long Amount, DateTimeOffset Expires);

    public PointsChatCommands(PointsService points, EventBus bus, AppLog log, Func<string, string, Task> send,
        JsonFile<PointsCommandsSettings>? settings = null, AccountLinks? links = null, SpendingLedger? spending = null,
        CommandStore? commands = null, TimeProvider? time = null, Random? random = null, Actions.ProfileService? profile = null)
    {
        _profile = profile;
        _points = points;
        _links = links;
        _spending = spending;
        _commands = commands;
        _file = settings;
        _settings = settings?.Value ?? new PointsCommandsSettings();
        _log = log;
        _send = send;
        _time = time ?? TimeProvider.System;
        _random = random ?? Random.Shared;
        lock (_gate) Normalize(_settings);
        _subscription = bus.Subscribe(OnEvent);
    }

    /// <summary>Raised after the settings change.</summary>
    public event Action? Changed;

    /// <summary>A copy of the settings.</summary>
    public PointsCommandsSettings Settings
    {
        get { lock (_gate) return Json.Clone(_settings); }
    }

    public void UpdateSettings(Action<PointsCommandsSettings> change)
    {
        lock (_gate)
        {
            change(_settings);
            Normalize(_settings);
            _file?.ScheduleSave();
        }
        Changed?.Invoke();
    }

    public void UpdateCommand(PointsCommandKind kind, Action<PointsCommand> change) =>
        UpdateSettings(s => change(s.Commands.First(c => c.Kind == kind)));

    // ---- defaults ----

    /// <summary>The command as Cuebot ships it.</summary>
    public static PointsCommand Default(PointsCommandKind kind)
    {
        var c = new PointsCommand { Kind = kind };
        switch (kind)
        {
            case PointsCommandKind.Points:
                c.Words = "!points";
                c.UserCooldownSeconds = 5;
                c.Replies = new()
                {
                    ["self"] = "{user} has {balance} (rank #{rank}){shares}",
                    ["other"] = "{target} has {balance} (rank #{rank}){shares}",
                    ["unknown"] = "{user}, I don't know {target} yet.",
                };
                break;
            case PointsCommandKind.Leaderboard:
            case PointsCommandKind.LeaderboardTwitch:
            case PointsCommandKind.LeaderboardKick:
            case PointsCommandKind.LeaderboardYouTube:
            case PointsCommandKind.LeaderboardRumble:
                c.Words = kind switch
                {
                    PointsCommandKind.Leaderboard => "!top, !leaderboard",
                    PointsCommandKind.LeaderboardTwitch => "!toptwitch",
                    PointsCommandKind.LeaderboardKick => "!topkick",
                    PointsCommandKind.LeaderboardYouTube => "!topyt, !topyoutube",
                    _ => "!toprumble",
                };
                c.CooldownSeconds = 10;
                c.Replies = new()
                {
                    ["list"] = "Top {title}: {list}",
                    ["empty"] = "Nobody has any {currency} yet.",
                };
                break;
            case PointsCommandKind.Give:
                c.Words = "!give";
                c.UserCooldownSeconds = 5;
                c.Replies = new()
                {
                    ["done"] = "{user} gave {amount} to {target}. {user} now has {balance}.",
                    ["usage"] = "{user}, use {command} name amount.",
                    ["notEnough"] = "{user}, you only have {balance}.",
                    ["unknown"] = "{user}, I don't know {target} yet.",
                    ["self"] = "{user}, you can't give {currency} to yourself.",
                };
                break;
            case PointsCommandKind.LinkTwitch:
            case PointsCommandKind.LinkKick:
            case PointsCommandKind.LinkYouTube:
            case PointsCommandKind.LinkRumble:
                c.Words = kind switch
                {
                    PointsCommandKind.LinkTwitch => "!linktwitch",
                    PointsCommandKind.LinkKick => "!linkkick",
                    PointsCommandKind.LinkYouTube => "!linkyt, !linkyoutube",
                    _ => "!linkrumble",
                };
                c.UserCooldownSeconds = 5;
                c.Replies = new()
                {
                    ["pending"] = "{user}, to finish: as {target} in {platform} chat, type {confirm} {user} within {minutes} minutes.",
                    ["linked"] = "Linked! {accounts} now share {balance}.",
                    ["already"] = "{user}, that account is already linked.",
                    ["taken"] = "{user}, your {platform} account is already linked to {other}. Type {unlink} {platformWord} first.",
                    ["conflict"] = "{user}, both sides are already linked to other accounts on the same platform. Unlink one first.",
                    ["same"] = "{user}, you're already on {platform}.",
                    ["usage"] = "{user}, use {command} followed by your {platform} name.",
                };
                break;
            case PointsCommandKind.Unlink:
                c.Words = "!unlink";
                c.UserCooldownSeconds = 5;
                c.Replies = new()
                {
                    ["done"] = "{user}, your {platform} account is unlinked. Each account keeps what is in it.",
                    ["notLinked"] = "{user}, that account isn't linked.",
                    ["usage"] = "{user}, use {command} twitch, kick, youtube or rumble.",
                };
                break;
            case PointsCommandKind.Support:
                c.Words = "!support";
                c.Enabled = false;
                c.UserCooldownSeconds = 10;
                c.Replies = new()
                {
                    ["self"] = "{user} has supported the stream with {total}: {items}. Thank you!",
                    ["other"] = "{target} has supported the stream with {total}: {items}.",
                    ["none"] = "No support recorded for {target} yet.",
                };
                break;
            case PointsCommandKind.TopSupporters:
                c.Words = "!topsupporters";
                c.Enabled = false;
                c.CooldownSeconds = 30;
                c.Replies = new()
                {
                    ["list"] = "Top supporters: {list}",
                    ["empty"] = "No support recorded yet.",
                };
                break;
            case PointsCommandKind.Gamble:
                c.Words = "!gamble";
                c.UserCooldownSeconds = 30;
                c.Replies = new()
                {
                    ["win"] = "{user} rolled {roll} and won {winnings}! Now {balance}.",
                    ["jackpot"] = "{user} rolled {roll}, JACKPOT! Won {winnings}. Now {balance}.",
                    ["lose"] = "{user} rolled {roll} and lost {amount}. Now {balance}.",
                    ["usage"] = "{user}, use {command} amount, all or a percent like 50%.",
                    ["notEnough"] = "{user}, you only have {balance}.",
                    ["limits"] = "{user}, bet from {minimum} to {maximum}.",
                };
                break;
            case PointsCommandKind.Slots:
                c.Words = "!slots";
                c.UserCooldownSeconds = 30;
                c.Replies = new()
                {
                    ["triple"] = "[ {reels} ] {user} hit three of a kind and won {winnings}! Now {balance}.",
                    ["pair"] = "[ {reels} ] {user} got a pair and won {winnings}. Now {balance}.",
                    ["lose"] = "[ {reels} ] {user} lost {amount}. Now {balance}.",
                    ["usage"] = "{user}, use {command} amount, all or a percent like 50%.",
                    ["notEnough"] = "{user}, you only have {balance}.",
                    ["limits"] = "{user}, bet from {minimum} to {maximum}.",
                };
                break;
            case PointsCommandKind.Duel:
                c.Words = "!duel";
                c.UserCooldownSeconds = 30;
                c.Replies = new()
                {
                    ["challenge"] = "{target}, {user} challenges you to a duel for {amount}! Type {accept} within {seconds} seconds.",
                    ["usage"] = "{user}, use {command} name amount.",
                    ["notEnough"] = "{user}, you only have {balance}.",
                    ["unknown"] = "{user}, I don't know {target} yet.",
                    ["self"] = "{user}, you can't duel yourself.",
                    ["busy"] = "{user}, {target} already has a duel waiting.",
                    ["limits"] = "{user}, bet from {minimum} to {maximum}.",
                };
                break;
            case PointsCommandKind.Accept:
                c.Words = "!accept";
                c.Replies = new()
                {
                    ["won"] = "{winner} beat {loser} in a duel and won {amount}!",
                    ["notEnough"] = "{user}, you need {amount} to accept the duel.",
                    ["challengerShort"] = "{target} doesn't have {amount} any more, so the duel is off.",
                };
                break;
        }
        return c;
    }

    private static void Normalize(PointsCommandsSettings s)
    {
        s.Commands.RemoveAll(c => !Enum.IsDefined(c.Kind));
        s.Commands = s.Commands.GroupBy(c => c.Kind).Select(g => g.First()).ToList();
        foreach (var kind in Enum.GetValues<PointsCommandKind>())
        {
            var defaults = Default(kind);
            if (s.Commands.FirstOrDefault(c => c.Kind == kind) is not { } c)
            {
                s.Commands.Add(defaults);
                continue;
            }
            c.Replies ??= [];
            foreach (var (key, text) in defaults.Replies) c.Replies.TryAdd(key, text);
            c.CooldownSeconds = Math.Max(0, c.CooldownSeconds);
            c.UserCooldownSeconds = Math.Max(0, c.UserCooldownSeconds);
        }
        s.Commands = s.Commands.OrderBy(c => c.Kind).ToList();
        s.LeaderboardSize = Math.Clamp(s.LeaderboardSize, 1, 25);
        s.MinimumBet = Math.Max(1, s.MinimumBet);
        s.MaximumBet = Math.Max(0, s.MaximumBet);
        s.GambleWinPercent = Math.Clamp(s.GambleWinPercent, 0, 100);
        s.GambleJackpotPercent = Math.Clamp(s.GambleJackpotPercent, 0, s.GambleWinPercent);
        s.SlotsTripleMultiplier = Math.Max(0, s.SlotsTripleMultiplier);
        s.SlotsPairMultiplier = Math.Max(0, s.SlotsPairMultiplier);
        s.DuelSeconds = Math.Clamp(s.DuelSeconds, 10, 600);
        if (string.IsNullOrWhiteSpace(s.SlotSymbols)) s.SlotSymbols = new PointsCommandsSettings().SlotSymbols;
    }

    // ---- chat ----

    private sealed record Ctx(PointsCommand Command, string Word, string Platform, string UserId, string User, string MessageId,
        string[] Args, PointsCommandsSettings Settings);

    /// <summary>Handles one bus event (subscribed automatically; public for tests).</summary>
    public void OnEvent(BotEvent e)
    {
        if (e.Type != ChatHub.MessageEvent || e.Args.GetBool("isInternal")) return;
        var platform = e.Args.GetString("platform");
        var userId = e.Args.GetString("userId");
        var user = e.Args.GetString("user");
        var text = e.Args.GetString("message").Trim();
        if (platform.Length == 0 || userId.Length == 0 || text.Length == 0) return;
        _links?.Rename(platform, userId, user);   // keeps linked names current (they change; ids do not)
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var word = parts[0];
        Ctx ctx;
        lock (_gate)
        {
            var command = _settings.Commands.FirstOrDefault(c => c.Enabled && c.WordList().Contains(word, StringComparer.OrdinalIgnoreCase));
            if (command is null) return;
            if (OwnCommandUses(word, platform)) return;
            if (!Allowed(command.MinimumRole, e.Args)) return;
            var now = _time.GetUtcNow();
            var globalKey = command.Kind.ToString();
            var userKey = globalKey + "|" + UserVariables.NormalizePlatform(platform) + ":" + userId;
            if (_cooldowns.TryGetValue(globalKey, out var g) && now < g) return;
            if (_cooldowns.TryGetValue(userKey, out var u) && now < u) return;
            if (command.CooldownSeconds > 0) _cooldowns[globalKey] = now.AddSeconds(command.CooldownSeconds);
            if (command.UserCooldownSeconds > 0) _cooldowns[userKey] = now.AddSeconds(command.UserCooldownSeconds);
            ctx = new Ctx(Json.Clone(command), word, platform, userId, user, e.Args.GetString("messageId"), parts[1..], Json.Clone(_settings));
        }
        try
        {
            Run(ctx);
        }
        catch (Exception ex)
        {
            _log.Warn("Points", $"{word} failed: {ex.Message}");
        }
    }

    /// <summary>
    /// True when the streamer made a command with this word: one on the Commands page, or (in Twitch chat) an action with a
    /// "Twitch command" trigger.
    /// </summary>
    private bool OwnCommandUses(string word, string platform)
    {
        static bool Same(string? text, string word) => (text ?? "").Split(['\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(line => string.Equals(line.Split(' ')[0], word, StringComparison.OrdinalIgnoreCase));
        if (_commands is not null && _commands.Snapshot.Any(c => c.Enabled && c.Commands.Any(w => Same(w, word)))) return true;
        if (_profile is null || !string.Equals(platform, "twitch", StringComparison.OrdinalIgnoreCase)) return false;
        return _profile.Snapshot.Actions.Any(a => a.Enabled && a.Triggers.Any(t => t.Enabled && t.Type == "twitch.command"
            && Same(t.Settings.GetValueOrDefault("command"), word)));
    }

    private static bool Allowed(ChatPermission minimum, IReadOnlyDictionary<string, object?> args) => minimum switch
    {
        ChatPermission.Everyone => true,
        ChatPermission.Subscriber => args.GetBool("isSubscriber") || args.GetBool("isVip") || args.GetBool("isModerator") || args.GetBool("isBroadcaster"),
        ChatPermission.Vip => args.GetBool("isVip") || args.GetBool("isModerator") || args.GetBool("isBroadcaster"),
        ChatPermission.Moderator => args.GetBool("isModerator") || args.GetBool("isBroadcaster"),
        _ => args.GetBool("isBroadcaster"),
    };

    private void Run(Ctx x)
    {
        switch (x.Command.Kind)
        {
            case PointsCommandKind.Points: PointsCmd(x); break;
            case PointsCommandKind.Leaderboard: Leaderboard(x, null); break;
            case PointsCommandKind.LeaderboardTwitch: Leaderboard(x, "twitch"); break;
            case PointsCommandKind.LeaderboardKick: Leaderboard(x, "kick"); break;
            case PointsCommandKind.LeaderboardYouTube: Leaderboard(x, "youtube"); break;
            case PointsCommandKind.LeaderboardRumble: Leaderboard(x, "rumble"); break;
            case PointsCommandKind.Give: Give(x); break;
            case PointsCommandKind.LinkTwitch: Link(x, "twitch"); break;
            case PointsCommandKind.LinkKick: Link(x, "kick"); break;
            case PointsCommandKind.LinkYouTube: Link(x, "youtube"); break;
            case PointsCommandKind.LinkRumble: Link(x, "rumble"); break;
            case PointsCommandKind.Unlink: Unlink(x); break;
            case PointsCommandKind.Support: Support(x); break;
            case PointsCommandKind.TopSupporters: TopSupporters(x); break;
            case PointsCommandKind.Gamble: Gamble(x); break;
            case PointsCommandKind.Slots: Slots(x); break;
            case PointsCommandKind.Duel: DuelCmd(x); break;
            case PointsCommandKind.Accept: Accept(x); break;
        }
    }

    // ---- commands ----

    private void PointsCmd(Ctx x)
    {
        var (id, name, self) = (x.UserId, x.User, true);
        if (x.Args.Length > 0 && !SameName(x.Args[0], x.User))
        {
            name = Clean(x.Args[0]);
            id = _points.FindUserId(x.Platform, name) ?? "";
            self = false;
            if (id.Length == 0)
            {
                Say(x, "unknown", new() { ["target"] = name });
                return;
            }
        }
        var c = x.Settings.Currency;
        var shares = _points.Shares(x.Platform, id, c);
        var sharesText = shares.Count > 1
            ? " · " + string.Join(" · ", shares.Select(s => $"{AccountLinks.DisplayName(s.Platform)} {Number(s.Points)}"))
            : "";
        Say(x, self ? "self" : "other", new()
        {
            ["target"] = name,
            ["balance"] = _points.Format(_points.Get(x.Platform, id, c), c),
            ["points"] = Number(_points.Get(x.Platform, id, c)),
            ["rank"] = Number(_points.Rank(x.Platform, id, c)),
            ["shares"] = sharesText,
        });
    }

    private void Leaderboard(Ctx x, string? platform)
    {
        var c = x.Settings.Currency;
        var rows = _points.Leaderboard(platform, x.Settings.LeaderboardSize, c).Where(r => r.Points > 0).ToList();
        var currency = _points.NameOf(c);
        if (rows.Count == 0)
        {
            Say(x, "empty", new() { ["platform"] = platform is null ? "" : AccountLinks.DisplayName(platform) });
            return;
        }
        var list = string.Join(" · ", rows.Select(r => $"{r.Rank}. {r.UserName} {Number(r.Points)}"));
        Say(x, "list", new()
        {
            ["title"] = platform is null ? currency : $"{AccountLinks.DisplayName(platform)} {currency}",
            ["platform"] = platform is null ? "" : AccountLinks.DisplayName(platform),
            ["list"] = list,
        });
    }

    private void Give(Ctx x)
    {
        var c = x.Settings.Currency;
        if (x.Args.Length < 2)
        {
            Say(x, "usage");
            return;
        }
        var target = Clean(x.Args[0]);
        var id = _points.FindUserId(x.Platform, target);
        if (id is null)
        {
            Say(x, "unknown", new() { ["target"] = target });
            return;
        }
        if (_points.SameWallet(x.Platform, x.UserId, x.Platform, id))
        {
            Say(x, "self", new() { ["target"] = target });
            return;
        }
        var balance = _points.Get(x.Platform, x.UserId, c);
        var amount = Amount(x.Args[1], balance);
        if (amount <= 0)
        {
            Say(x, "usage");
            return;
        }
        if (amount > balance)
        {
            Say(x, "notEnough", new() { ["balance"] = _points.Format(balance, c), ["amount"] = _points.Format(amount, c) });
            return;
        }
        var (from, _) = _points.Transfer(x.Platform, x.UserId, x.User, x.Platform, id, target, amount, c);
        Say(x, "done", new() { ["target"] = target, ["amount"] = _points.Format(amount, c), ["balance"] = _points.Format(from, c) });
    }

    private void Link(Ctx x, string platform)
    {
        var display = AccountLinks.DisplayName(platform);
        if (_links is null) return;
        if (x.Args.Length < 1)
        {
            Say(x, "usage", new() { ["platform"] = display });
            return;
        }
        var target = Clean(x.Args[0]);
        var result = _links.Request(new LinkedAccount(x.Platform, x.UserId, x.User), platform, target);
        var c = x.Settings.Currency;
        var here = AccountLinks.PlatformOf(x.Platform) ?? UserVariables.NormalizePlatform(x.Platform);
        var values = new Dictionary<string, string>
        {
            ["target"] = target,
            ["platform"] = display,
            ["platformWord"] = platform,
            ["minutes"] = Number(_links.RequestMinutes),
            ["confirm"] = FirstWord(LinkKind(here)),
            ["unlink"] = FirstWord(PointsCommandKind.Unlink),
            ["other"] = result.Other?.UserName ?? "",
        };
        switch (result.Outcome)
        {
            case LinkOutcome.Pending: Say(x, "pending", values); break;
            case LinkOutcome.Linked:
                values["accounts"] = string.Join(" + ", result.Group!.Accounts.Select(a => $"{a.UserName} ({AccountLinks.DisplayName(a.Platform)})"));
                values["balance"] = _points.Format(_points.Get(x.Platform, x.UserId, c), c);
                Say(x, "linked", values);
                // Tell the other chat too, so the first account sees it worked.
                if (result.Other is { } other) Send(AccountLinks.DisplayName(other.Platform), Fill(x.Command.Replies["linked"], x with { User = other.UserName }, values));
                break;
            case LinkOutcome.AlreadyLinked: Say(x, "already", values); break;
            case LinkOutcome.PlatformTaken: Say(x, "taken", values); break;
            case LinkOutcome.Conflict: Say(x, "conflict", values); break;
            case LinkOutcome.SamePlatform: Say(x, "same", values); break;
            default: Say(x, "usage", values); break;
        }
    }

    private void Unlink(Ctx x)
    {
        if (_links is null) return;
        string platform;
        string userId;
        if (x.Args.Length == 0)
        {
            platform = UserVariables.NormalizePlatform(x.Platform);
            userId = x.UserId;
        }
        else
        {
            if (AccountLinks.PlatformOf(x.Args[0]) is not { } p)
            {
                Say(x, "usage");
                return;
            }
            platform = p;
            var account = _links.LinkedTo(x.Platform, x.UserId).FirstOrDefault(a => a.Platform == p);
            if (account is null || !_links.IsLinked(x.Platform, x.UserId))
            {
                Say(x, "notLinked", new() { ["platform"] = AccountLinks.DisplayName(p) });
                return;
            }
            userId = account.UserId;
        }
        Say(x, _links.Unlink(platform, userId) ? "done" : "notLinked", new() { ["platform"] = AccountLinks.DisplayName(platform) });
    }

    private void Support(Ctx x)
    {
        if (_spending is null) return;
        var name = x.Args.Length > 0 ? Clean(x.Args[0]) : x.User;
        var self = SameName(name, x.User);
        var tally = _spending.Tally(x.Platform, self ? x.User : name, x.Settings.SupportPeriod);
        if (tally.Items.Count == 0)
        {
            Say(x, "none", new() { ["target"] = name });
            return;
        }
        var items = string.Join(", ", SupportItems(tally));
        Say(x, self ? "self" : "other", new()
        {
            ["target"] = name,
            ["total"] = SpendingText.Money(tally.Total, tally.Currency),
            ["items"] = items,
        });
    }

    /// <summary>"1,200 bits", "3 gifted subs", "$12.50 in Rant": units by kind, money by what it was.</summary>
    internal static IEnumerable<string> SupportItems(SupportTally tally)
    {
        foreach (var g in tally.Items.Where(i => i.Unit != SpendUnit.Money).GroupBy(i => i.Unit).OrderByDescending(g => g.Sum(i => i.Value)))
        {
            var n = (long)Math.Round(g.Sum(i => i.Quantity));
            yield return g.Key switch
            {
                SpendUnit.Bits => $"{Number(n)} bits",
                SpendUnit.Kicks => $"{Number(n)} Kicks",
                SpendUnit.Jewels => $"{Number(n)} Jewels",
                SpendUnit.GiftSubscription => n == 1 ? "1 gifted sub" : $"{Number(n)} gifted subs",
                SpendUnit.Subscription => n == 1 ? "1 sub" : $"{Number(n)} subs",
                _ => $"{Number(n)} {g.First().Label}",
            };
        }
        foreach (var i in tally.Items.Where(i => i.Unit == SpendUnit.Money))
            yield return $"{SpendingText.Money(i.Value, tally.Currency)} in {i.Label}";
    }

    private void TopSupporters(Ctx x)
    {
        if (_spending is null) return;
        var list = _spending.Top(x.Settings.SupportPeriod, "", x.Settings.LeaderboardSize, combineLinked: true);
        if (list.Spenders.Count == 0)
        {
            Say(x, "empty");
            return;
        }
        Say(x, "list", new()
        {
            ["list"] = string.Join(" · ", list.Spenders.Select(s => $"{s.Rank}. {s.User} {SpendingText.Money(s.Total, list.Currency)}")),
        });
    }

    private bool Bet(Ctx x, out long amount, out long balance)
    {
        var c = x.Settings.Currency;
        balance = _points.Get(x.Platform, x.UserId, c);
        amount = x.Args.Length > 0 ? Amount(x.Args[0], balance) : 0;
        if (amount <= 0)
        {
            Say(x, x.Args.Length > 0 && balance <= 0 ? "notEnough" : "usage", new() { ["balance"] = _points.Format(balance, c) });
            return false;
        }
        var s = x.Settings;
        if (amount < s.MinimumBet || s.MaximumBet > 0 && amount > s.MaximumBet)
        {
            Say(x, "limits", Limits(s, c));
            return false;
        }
        if (amount > balance)
        {
            Say(x, "notEnough", new() { ["balance"] = _points.Format(balance, c), ["amount"] = _points.Format(amount, c) });
            return false;
        }
        return true;
    }

    private Dictionary<string, string> Limits(PointsCommandsSettings s, string c) => new()
    {
        ["minimum"] = _points.Format(s.MinimumBet, c),
        ["maximum"] = s.MaximumBet > 0 ? _points.Format(s.MaximumBet, c) : "any amount",
    };

    private void Gamble(Ctx x)
    {
        if (!Bet(x, out var amount, out _)) return;
        var c = x.Settings.Currency;
        var s = x.Settings;
        if (!_points.TrySpend(x.Platform, x.UserId, x.User, amount, out _, c)) return;
        var roll = _random.Next(1, 101);
        var jackpot = roll > 100 - s.GambleJackpotPercent;
        var win = roll > 100 - s.GambleWinPercent;
        var back = jackpot ? amount * 3 : win ? amount * 2 : 0;
        var balance = back > 0 ? _points.Add(x.Platform, x.UserId, x.User, back, "gamble", c) : _points.Get(x.Platform, x.UserId, c);
        Say(x, jackpot ? "jackpot" : win ? "win" : "lose", new()
        {
            ["roll"] = roll.ToString(CultureInfo.InvariantCulture),
            ["amount"] = _points.Format(amount, c),
            ["winnings"] = _points.Format(Math.Max(0, back - amount), c),
            ["balance"] = _points.Format(balance, c),
        });
    }

    private void Slots(Ctx x)
    {
        if (!Bet(x, out var amount, out _)) return;
        var c = x.Settings.Currency;
        var s = x.Settings;
        var symbols = s.SlotSymbols.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (symbols.Length < 2) symbols = new PointsCommandsSettings().SlotSymbols.Split(' ');
        if (!_points.TrySpend(x.Platform, x.UserId, x.User, amount, out _, c)) return;
        var reels = Enumerable.Range(0, 3).Select(_ => symbols[_random.Next(symbols.Length)]).ToArray();
        var distinct = reels.Distinct().Count();
        var multiplier = distinct == 1 ? s.SlotsTripleMultiplier : distinct == 2 ? s.SlotsPairMultiplier : 0;
        var back = (long)decimal.Floor(amount * multiplier);
        var balance = back > 0 ? _points.Add(x.Platform, x.UserId, x.User, back, "slots", c) : _points.Get(x.Platform, x.UserId, c);
        Say(x, distinct == 1 ? "triple" : distinct == 2 && back > 0 ? "pair" : "lose", new()
        {
            ["reels"] = string.Join(" ", reels),
            ["amount"] = _points.Format(amount, c),
            ["winnings"] = _points.Format(Math.Max(0, back - amount), c),
            ["balance"] = _points.Format(balance, c),
        });
    }

    private void DuelCmd(Ctx x)
    {
        var c = x.Settings.Currency;
        if (x.Args.Length < 2)
        {
            Say(x, "usage");
            return;
        }
        var target = Clean(x.Args[0]);
        var id = _points.FindUserId(x.Platform, target);
        if (id is null)
        {
            Say(x, "unknown", new() { ["target"] = target });
            return;
        }
        if (_points.SameWallet(x.Platform, x.UserId, x.Platform, id))
        {
            Say(x, "self");
            return;
        }
        if (!Bet(x with { Args = x.Args[1..] }, out var amount, out _)) return;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _duels.RemoveAll(d => d.Expires <= now);
            if (_duels.Any(d => d.Platform == x.Platform && d.ToId == id))
            {
                Say(x, "busy", new() { ["target"] = target });
                return;
            }
            _duels.Add(new Duel(x.Platform, x.UserId, x.User, id, target, amount, now.AddSeconds(x.Settings.DuelSeconds)));
        }
        Say(x, "challenge", new()
        {
            ["target"] = target,
            ["amount"] = _points.Format(amount, c),
            ["accept"] = FirstWord(PointsCommandKind.Accept),
            ["seconds"] = Number(x.Settings.DuelSeconds),
        });
    }

    private void Accept(Ctx x)
    {
        var c = x.Settings.Currency;
        Duel? duel;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            _duels.RemoveAll(d => d.Expires <= now);
            duel = _duels.LastOrDefault(d => d.Platform == x.Platform && d.ToId == x.UserId);
            if (duel is null) return;
            _duels.Remove(duel);
        }
        var values = new Dictionary<string, string> { ["target"] = duel.FromName, ["amount"] = _points.Format(duel.Amount, c) };
        if (!_points.TrySpend(x.Platform, x.UserId, x.User, duel.Amount, out _, c))
        {
            Say(x, "notEnough", values);
            return;
        }
        if (!_points.TrySpend(duel.Platform, duel.FromId, duel.FromName, duel.Amount, out _, c))
        {
            _points.Add(x.Platform, x.UserId, x.User, duel.Amount, "duel refund", c);
            Say(x, "challengerShort", values);
            return;
        }
        var challengerWins = _random.Next(2) == 0;
        var (winnerId, winner, loser) = challengerWins ? (duel.FromId, duel.FromName, x.User) : (x.UserId, x.User, duel.FromName);
        _points.Add(x.Platform, winnerId, winner, duel.Amount * 2, "duel", c);
        values["winner"] = winner;
        values["loser"] = loser;
        Say(x, "won", values);
    }

    // ---- helpers ----

    private string FirstWord(PointsCommandKind kind)
    {
        lock (_gate) return _settings.Commands.FirstOrDefault(c => c.Kind == kind)?.WordList().FirstOrDefault() ?? "";
    }

    private static PointsCommandKind LinkKind(string platform) => platform switch
    {
        "kick" => PointsCommandKind.LinkKick,
        "youtube" => PointsCommandKind.LinkYouTube,
        "rumble" => PointsCommandKind.LinkRumble,
        _ => PointsCommandKind.LinkTwitch,
    };

    /// <summary>"250", "all", "50%" (of the balance), "1k"; 0 when not an amount.</summary>
    public static long Amount(string text, long balance)
    {
        text = text.Trim().Replace(",", "");
        if (text.Equals("all", StringComparison.OrdinalIgnoreCase)) return balance;
        if (text.EndsWith('%') && decimal.TryParse(text[..^1], NumberStyles.Number, CultureInfo.InvariantCulture, out var percent))
            return percent is > 0 and <= 100 ? (long)decimal.Floor(balance * percent / 100) : 0;
        var factor = 1L;
        if (text.EndsWith('k') || text.EndsWith('K')) (factor, text) = (1000, text[..^1]);
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var n) && n > 0
            ? (long)decimal.Floor(Math.Min(n * factor, long.MaxValue))
            : 0;
    }

    private static string Clean(string name) => name.Trim().TrimStart('@');

    private static bool SameName(string a, string b) => string.Equals(Clean(a), Clean(b), StringComparison.OrdinalIgnoreCase);

    private static string Number(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    private void Say(Ctx x, string reply, Dictionary<string, string>? values = null)
    {
        if (!x.Command.Replies.TryGetValue(reply, out var template) || string.IsNullOrWhiteSpace(template)) return;
        Send(x.Platform, Fill(template, x, values));
    }

    private string Fill(string template, Ctx x, Dictionary<string, string>? values)
    {
        var all = new Dictionary<string, string>(values ?? [], StringComparer.OrdinalIgnoreCase)
        {
            ["user"] = x.User,
            ["command"] = x.Word,
        };
        all.TryAdd("currency", _points.NameOf(x.Settings.Currency));
        var sb = new StringBuilder(template);
        foreach (var (key, value) in all) sb.Replace("{" + key + "}", value);
        return sb.ToString().Trim();
    }

    private void Send(string platform, string text)
    {
        if (text.Length == 0) return;
        _ = SendAsync(platform, text);
    }

    private async Task SendAsync(string platform, string text)
    {
        try
        {
            await _send(platform, text).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Warn("Points", "Could not answer in chat: " + ex.Message);
        }
    }

    public void Dispose() => _subscription.Dispose();
}
