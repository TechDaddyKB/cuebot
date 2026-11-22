using Cuebot.Core.Actions;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Chat;

/// <summary>An account a platform lists as permanently banned from the channel.</summary>
public sealed record BannedAccount(string UserId, string UserName, DateTimeOffset? BannedAt = null);

/// <summary>A platform that can list the channel's bans, so bans made before Cuebot was installed also count.</summary>
public interface IBanListSource
{
    string Platform { get; }

    /// <summary>Signed in with a scope that allows reading the ban list.</summary>
    bool CanRead { get; }

    Task<IReadOnlyList<BannedAccount>> GetBannedAsync(CancellationToken ct);
}

/// <summary>
/// Answers "is this account banned or a known bot?" for follows, event speech, alerts, credits and command groups.
/// Bans come from the mod log, live platform events and (Twitch) a daily read of the ban list. A chat message from an
/// account is proof it is no longer banned, which covers an unban Cuebot did not see. Timeouts never count. Thread-safe.
/// </summary>
public sealed class AccountStandings : IPeriodicService, IDisposable
{
    /// <summary>Common chat bots: the union of the speech and points defaults.</summary>
    public static readonly IReadOnlyList<string> BuiltInBots =
    [
        "nightbot", "streamelements", "streamlabs", "moobot", "fossabot", "wizebot", "sery_bot", "soundalerts",
        "pokemoncommunitygame", "kofistreambot", "botrix", "botrixoficial", "commanderroot", "streamerbot", "kicklet",
        "streamstickers",
    ];

    private static readonly HashSet<string> BuiltIn = new(BuiltInBots, StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan SyncEvery = TimeSpan.FromHours(24);
    private static readonly TimeSpan SyncRetry = TimeSpan.FromHours(1);

    /// <summary>A message this soon after a ban may have been sent before it, so it proves nothing.</summary>
    private static readonly TimeSpan InTransit = TimeSpan.FromSeconds(30);

    private const int MaxChatters = 20_000;

    private readonly Func<ModerationSettings> _settings;
    private readonly ModLog? _log;
    private readonly IReadOnlyList<IBanListSource> _sources;
    private readonly AppLog _appLog;
    private readonly TimeProvider _time;
    private readonly IDisposable _subscription;
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _banned = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _lastChat = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _synced = new(StringComparer.OrdinalIgnoreCase);
    private readonly CancellationTokenSource _stopping = new();
    private DateTimeOffset _nextSync = DateTimeOffset.MinValue;
    private int _syncing;

    public AccountStandings(EventBus bus, AppLog log, Func<ModerationSettings> settings, ModLog? modLog = null,
        IEnumerable<IBanListSource>? sources = null, TimeProvider? time = null)
    {
        _appLog = log;
        _settings = settings;
        _log = modLog;
        _sources = sources?.ToArray() ?? [];
        _time = time ?? TimeProvider.System;
        _subscription = bus.Subscribe(OnEvent);
    }

    /// <summary>True when the account has a ban in force that Cuebot knows of.</summary>
    public bool IsBanned(string platform, string userId, string userName)
    {
        var keys = Keys(platform, userId, userName);
        lock (_gate)
        {
            foreach (var key in keys)
            {
                if (_banned.TryGetValue(key, out var at) && !ChattedSince(keys, at)) return true;
                if (_synced.TryGetValue(platform, out var list) && list.Contains(key) && !ChattedSince(keys, DateTimeOffset.MinValue)) return true;
            }
        }
        if (_log is null || userId.Length == 0) return false;
        var entry = _log.FindInForce(platform, userId);
        if (entry is not { Action: "ban" }) return false;
        lock (_gate) return !ChattedSince(keys, entry.At);
    }

    /// <summary>True for a built-in bot name or one on the streamer's own list.</summary>
    public bool IsKnownBot(string userName)
    {
        var name = userName.Trim().TrimStart('@');
        if (name.Length == 0) return false;
        return BuiltIn.Contains(name) || _settings().ExtraBots.Any(b => string.Equals(b.Trim().TrimStart('@'), name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Whether a feed event from this account is dropped. Paid events and test events never are.</summary>
    public bool HidesActivity(FeedKind kind, bool paid, string platform, string userId, string user, bool isTest = false)
    {
        if (isTest || paid || user.Length == 0 && userId.Length == 0) return false;
        var settings = _settings();
        var applies = kind switch
        {
            FeedKind.Follow => settings.IgnoreBannedFollows,
            FeedKind.Redemption => settings.IgnoreBannedRedemptions,
            _ => false,
        };
        return applies && IsExcluded(settings, platform, userId, user);
    }

    /// <summary>Whether this account stays out of the credits.</summary>
    public bool HidesFromCredits(string platform, string userId, string user)
    {
        var settings = _settings();
        return settings.KeepBannedOutOfCredits && IsExcluded(settings, platform, userId, user);
    }

    /// <summary>Whether a chatter stays out of the credits: their message proves they are not banned, so only bots count.</summary>
    public bool HidesChatterFromCredits(string user)
    {
        var settings = _settings();
        return settings.KeepBannedOutOfCredits && settings.TreatKnownBotsAsBanned && IsKnownBot(user);
    }

    /// <summary>Whether chat from this account is not read aloud (only with "treat known bots as banned").</summary>
    public bool SilencesChatter(string user) => _settings().TreatKnownBotsAsBanned && IsKnownBot(user);

    private bool IsExcluded(ModerationSettings settings, string platform, string userId, string user) =>
        (settings.TreatKnownBotsAsBanned && IsKnownBot(user)) || IsBanned(platform, userId, user);

    private bool ChattedSince(IEnumerable<string> keys, DateTimeOffset bannedAt) =>
        keys.Any(k => _lastChat.TryGetValue(k, out var chat) && chat > bannedAt + InTransit);

    private static string[] Keys(string platform, string userId, string userName)
    {
        var p = platform.ToLowerInvariant();
        var name = userName.Trim().TrimStart('@').ToLowerInvariant();
        if (userId.Length > 0 && name.Length > 0) return [$"{p}|{userId}", $"{p}|n:{name}"];
        return userId.Length > 0 ? [$"{p}|{userId}"] : name.Length > 0 ? [$"{p}|n:{name}"] : [];
    }

    private void OnEvent(BotEvent e)
    {
        switch (e.Type)
        {
            case ChatHub.MessageEvent:
                if (e.Args.GetBool("isSharedChat") || e.Args.GetBool("isInternal")) return;
                Remember(e);
                return;
            case ChatHub.UserBannedEvent:
                Ban(e);
                return;
            case ChatHub.ModerationActionEvent:
                // Another channel's action in a shared chat says nothing about this channel.
                if (e.Args.GetString("channelId").Length > 0) return;
                var action = e.Args.GetString("action");
                if (action.Equals("ban", StringComparison.OrdinalIgnoreCase)) Ban(e);
                else if (action.Equals("unban", StringComparison.OrdinalIgnoreCase)) Unban(e);
                return;
        }
    }

    private void Remember(BotEvent e)
    {
        var now = _time.GetUtcNow();
        var keys = Keys(e.Args.GetString("platform"), e.Args.GetString("userId"), UserOf(e.Args));
        lock (_gate)
        {
            if (_lastChat.Count >= MaxChatters) _lastChat.Clear();
            foreach (var key in keys) _lastChat[key] = now;
        }
    }

    private void Ban(BotEvent e)
    {
        var now = _time.GetUtcNow();
        var keys = Keys(e.Args.GetString("platform"), e.Args.GetString("userId"), UserOf(e.Args));
        lock (_gate)
            foreach (var key in keys)
            {
                _banned[key] = now;
                _lastChat.Remove(key);
            }
    }

    private void Unban(BotEvent e)
    {
        var now = _time.GetUtcNow();
        var platform = e.Args.GetString("platform");
        var keys = Keys(platform, e.Args.GetString("userId"), UserOf(e.Args));
        lock (_gate)
            foreach (var key in keys)
            {
                _banned.Remove(key);
                if (_synced.TryGetValue(platform, out var list)) list.Remove(key);
                // Outranks a mod log row whose undo Cuebot did not record.
                _lastChat[key] = now + InTransit + TimeSpan.FromSeconds(1);
            }
    }

    private static string UserOf(IReadOnlyDictionary<string, object?> args) =>
        args.GetString("userName") is { Length: > 0 } name ? name : args.GetString("user");

    void IPeriodicService.Tick(DateTimeOffset now)
    {
        if (_sources.Count == 0 || now < _nextSync) return;
        var settings = _settings();
        if (!settings.SyncBanList || !(settings.IgnoreBannedFollows || settings.IgnoreBannedRedemptions || settings.KeepBannedOutOfCredits)) return;
        if (!_sources.Any(s => s.CanRead)) return;
        if (Interlocked.Exchange(ref _syncing, 1) == 1) return;
        _nextSync = now + SyncEvery;
        _ = Task.Run(() => SyncAsync(_stopping.Token));
    }

    /// <summary>Reads every available ban list once (public for tests).</summary>
    public async Task SyncAsync(CancellationToken ct = default)
    {
        try
        {
            foreach (var source in _sources.Where(s => s.CanRead))
            {
                try
                {
                    var accounts = await source.GetBannedAsync(ct).ConfigureAwait(false);
                    var keys = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var account in accounts) keys.UnionWith(Keys(source.Platform, account.UserId, account.UserName));
                    lock (_gate)
                    {
                        _synced[source.Platform] = keys;
                        // Listed now, so a message from before this read no longer proves they are not banned.
                        foreach (var key in keys) _lastChat.Remove(key);
                    }
                    _appLog.Info("Chat", $"Read {accounts.Count} banned accounts from {source.Platform}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _nextSync = _time.GetUtcNow() + SyncRetry;
                    _appLog.Warn("Chat", $"Could not read the {source.Platform} ban list: {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
        finally
        {
            Interlocked.Exchange(ref _syncing, 0);
        }
    }

    public void Dispose()
    {
        _subscription.Dispose();
        _stopping.Cancel();
        _stopping.Dispose();
    }
}

/// <summary>The "Skip banned accounts and known bots" option of the platform Follow triggers.</summary>
public static class FollowTriggerFilter
{
    public const string SettingKey = "skipFiltered";

    public static ParameterDescriptor Parameter() => new(SettingKey, "Skip banned accounts and known bots", ParameterKind.Boolean)
    {
        DefaultValue = "true",
        Help = "Follows the 'Banned accounts and bots' options hide do not run this action. Untick to run it anyway; %isBanned% and %isKnownBot% tell them apart.",
    };

    /// <summary>Adds %isBanned% and %isKnownBot% and returns false when the follow must not run the action.</summary>
    public static bool Allows(AccountStandings? standings, string platform, BotEvent e, ParameterValues settings, IDictionary<string, object?> args)
    {
        if (standings is null) return true;
        var userId = e.Args.GetString("userId");
        var user = e.Args.GetString("userName") is { Length: > 0 } name ? name : e.Args.GetString("user");
        args["isBanned"] = standings.IsBanned(platform, userId, user);
        args["isKnownBot"] = standings.IsKnownBot(user);
        return !settings.GetBool(SettingKey)
               || !standings.HidesActivity(FeedKind.Follow, false, platform, userId, user, e.Args.GetBool("isTest"));
    }
}
