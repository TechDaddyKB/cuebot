using Cuebot.Core.Actions;
using Cuebot.Core.Chat;
using Cuebot.Core.Commands;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Core.Credits;

/// <summary>
/// Collects per-stream credits from the platform-neutral bus events, so every platform module contributes without
/// knowing about credits: "chat.activity" (kind Follow, Subscription, GiftSubscription, Tip, Raid, Redemption,
/// StreamOnline) and "chat.message" (moderators, VIPs, subscribers and everyone who chatted). Tips whose amount is in
/// bits count as cheers. Persisted in credits.json. Resets on stream online (configurable), after a configurable
/// number of idle hours (checked by <see cref="Tick"/>), or on request. Thread-safe.
/// </summary>
public sealed class CreditsCollector : ICreditsSource, IPeriodicService, IDisposable
{
    private readonly JsonFile<CreditsFile>? _file;
    private readonly CreditsFile _data;
    private readonly TimeProvider _time;
    private readonly AppLog _log;
    private readonly IDisposable _subscription;
    private readonly object _gate = new();

    public CreditsCollector(AppPaths? paths, EventBus bus, AppLog log, TimeProvider? time = null)
    {
        _time = time ?? TimeProvider.System;
        _log = log;
        if (paths is not null)
        {
            _file = new JsonFile<CreditsFile>(paths.File("credits.json"), log);
            _data = _file.Value;
        }
        else
        {
            _data = new CreditsFile();
        }
        _subscription = bus.Subscribe(Handle);
    }

    public event Action? Changed;

    public CreditsSettings Settings
    {
        get { lock (_gate) return Json.Clone(_data.Settings); }
    }

    public void UpdateSettings(Action<CreditsSettings> change)
    {
        lock (_gate)
        {
            change(_data.Settings);
            Save();
        }
    }

    public CreditsSnapshot Snapshot()
    {
        lock (_gate) return Ordered(Json.Clone(_data.Current));
    }

    public void Reset()
    {
        lock (_gate)
        {
            _data.Current = new CreditsSnapshot();
            Save();
        }
        _log.Info("Credits", "Credits reset");
        Changed?.Invoke();
    }

    public CreditsSnapshot Test() => Ordered(SampleData(_time.GetUtcNow()));

    /// <summary>Adds an entry to any section (built-in id or a custom one, e.g. "Special thanks").</summary>
    public void AddCustom(string section, string user, string platform = "", double amount = 0, string detail = "")
    {
        section = section.Trim();
        user = user.Trim().TrimStart('@');
        if (section.Length == 0) throw new ArgumentException("No credits section given");
        if (user.Length == 0) throw new ArgumentException("No user given");
        if (Record(section, platform, user, amount, detail, force: true)) Changed?.Invoke();
    }

    /// <summary>Processes one bus event (subscribed automatically; public for tests).</summary>
    public void Handle(BotEvent e)
    {
        bool changed;
        if (e.Type == ChatHub.MessageEvent) changed = OnMessage(e.Args);
        else if (e.Type == ChatHub.ActivityEvent) changed = OnActivity(e.Args);
        else return;
        if (changed) Changed?.Invoke();
    }

    void IPeriodicService.Tick(DateTimeOffset now) => Tick(now);

    /// <summary>Auto reset after the configured idle hours.</summary>
    public void Tick(DateTimeOffset now)
    {
        bool reset;
        lock (_gate)
        {
            var hours = _data.Settings.AutoResetHours;
            reset = hours > 0 && _data.Current.UpdatedAt is { } last && now - last >= TimeSpan.FromHours(hours);
        }
        if (reset) Reset();
    }

    private bool OnActivity(IReadOnlyDictionary<string, object?> args)
    {
        var kind = Enum.TryParse<FeedKind>(args.GetString("kind"), ignoreCase: true, out var k) ? k : FeedKind.Other;
        var platform = args.GetString("platform");
        var user = args.GetString("user").Trim();
        var title = args.GetString("title");
        var amountText = args.GetString("amount").Trim();
        switch (kind)
        {
            case FeedKind.StreamOnline:
                bool reset;
                lock (_gate) reset = _data.Settings.ResetOnStreamOnline && _data.Current.Sections.Count > 0;
                if (reset) Reset();
                return false;
            case FeedKind.Follow:
                return Record(CreditsSections.Follows, platform, user, 0, "");
            case FeedKind.Subscription:
                return Record(CreditsSections.Subscriptions, platform, user, 0, amountText);
            case FeedKind.GiftSubscription:
                var gifted = ExtrasText.FirstNumber(title) ?? 1;
                if (user.Length == 0) user = "Anonymous";
                return Record(CreditsSections.GiftSubscriptions, platform, user, gifted, "");
            case FeedKind.Tip:
                var isBits = amountText.EndsWith("bits", StringComparison.OrdinalIgnoreCase) || amountText.EndsWith("bit", StringComparison.OrdinalIgnoreCase);
                var amount = ExtrasText.FirstNumber(amountText.Length > 0 ? amountText : title) ?? 0;
                return Record(isBits ? CreditsSections.Cheers : CreditsSections.Tips, platform, user, amount, amountText);
            case FeedKind.Raid:
                return Record(CreditsSections.Raids, platform, user, ExtrasText.FirstNumber(title) ?? 0, "");
            case FeedKind.Redemption:
                var marker = title.IndexOf(" redeemed ", StringComparison.OrdinalIgnoreCase);
                var reward = marker >= 0 ? title[(marker + " redeemed ".Length)..].Trim() : title;
                return Record(CreditsSections.Redemptions, platform, user, 0, reward);
            default:
                return false;
        }
    }

    private bool OnMessage(IReadOnlyDictionary<string, object?> args)
    {
        if (args.GetBool("isInternal")) return false;
        var platform = args.GetString("platform");
        var user = args.GetString("user").Trim();
        if (user.Length == 0) user = args.GetString("userName").Trim();
        if (user.Length == 0) return false;
        bool ignoreBroadcaster;
        lock (_gate) ignoreBroadcaster = _data.Settings.IgnoreBroadcaster;
        if (ignoreBroadcaster && args.GetBool("isBroadcaster")) return false;

        var changed = false;
        if (args.GetBool("isModerator")) changed |= Record(CreditsSections.Moderators, platform, user, 0, "", countRepeat: false);
        if (args.GetBool("isVip")) changed |= Record(CreditsSections.Vips, platform, user, 0, "", countRepeat: false);
        if (args.GetBool("isSubscriber")) changed |= Record(CreditsSections.Subscribers, platform, user, 0, "", countRepeat: false);
        changed |= Record(CreditsSections.Chatters, platform, user, 0, "", countRepeat: false);
        return changed;
    }

    /// <summary>Adds or updates an entry. Returns true when something visible changed.</summary>
    /// <param name="countRepeat">False for presence sections: a second chat message changes nothing (no save).</param>
    private bool Record(string sectionId, string platform, string user, double amount, string detail, bool force = false,
        bool countRepeat = true)
    {
        if (user.Length == 0) return false;
        var now = _time.GetUtcNow();
        lock (_gate)
        {
            var settings = _data.Settings;
            if (!force)
            {
                if (settings.DisabledSections.Contains(sectionId, StringComparer.OrdinalIgnoreCase)) return false;
                if (settings.IgnoredUsers.Contains(user.TrimStart('@'), StringComparer.OrdinalIgnoreCase)) return false;
            }
            var current = _data.Current;
            var section = current.Section(sectionId);
            if (section is null)
            {
                section = new CreditsSection { Id = sectionId, Title = CreditsSections.Title(sectionId) };
                current.Sections.Add(section);
            }
            var entry = section.Entries.FirstOrDefault(x =>
                string.Equals(x.User, user, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.Platform, platform, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                section.Entries.Add(new CreditsEntry { User = user, Platform = platform, Amount = amount, Detail = detail });
            }
            else
            {
                if (!countRepeat) return false;
                entry.Count++;
                entry.Amount += amount;
                if (detail.Length > 0) entry.Detail = detail;
                entry.User = user;
            }
            current.StartedAt ??= now;
            current.UpdatedAt = now;
            Save();
        }
        return true;
    }

    /// <summary>Built-in sections in their fixed order, then custom ones; ranked sections sorted by amount.</summary>
    private static CreditsSnapshot Ordered(CreditsSnapshot snapshot)
    {
        snapshot.Sections = snapshot.Sections
            .Where(s => s.Entries.Count > 0)
            .OrderBy(s => Array.IndexOf(CreditsSections.Order, s.Id) is var i and >= 0 ? i : int.MaxValue)
            .ToList();
        foreach (var section in snapshot.Sections.Where(s => CreditsSections.RankedByAmount.Contains(s.Id)))
        {
            section.Entries = section.Entries.OrderByDescending(e => e.Amount).ToList();   // stable: ties keep arrival order
        }
        return snapshot;
    }

    /// <summary>Deterministic sample credits for overlays and the page's test button.</summary>
    public static CreditsSnapshot SampleData(DateTimeOffset now)
    {
        CreditsSection Section(string id, params CreditsEntry[] entries) =>
            new() { Id = id, Title = CreditsSections.Title(id), Entries = [.. entries] };
        CreditsEntry E(string user, string platform, double amount = 0, string detail = "", int count = 1) =>
            new() { User = user, Platform = platform, Amount = amount, Detail = detail, Count = count };
        return new CreditsSnapshot
        {
            IsTest = true,
            StartedAt = now.AddHours(-3),
            UpdatedAt = now,
            Sections =
            [
                Section(CreditsSections.Follows, E("PixelPanda", "twitch"), E("RetroRaccoon", "youtube"), E("NeonNarwhal", "kick")),
                Section(CreditsSections.Subscriptions, E("CaptainCoffee", "twitch", detail: "Tier 1"), E("MapleMoose", "youtube")),
                Section(CreditsSections.GiftSubscriptions, E("GenerousGiraffe", "twitch", 10), E("KindKoala", "kick", 5)),
                Section(CreditsSections.Cheers, E("BitBaron", "twitch", 1000, "1000 bits"), E("SparkleSloth", "twitch", 100, "100 bits")),
                Section(CreditsSections.Tips, E("TipTopTiger", "rumble", 25, "$25.00"), E("DonutDuke", "youtube", 5, "$5.00")),
                Section(CreditsSections.Raids, E("FriendlyStreamer", "twitch", 42)),
                Section(CreditsSections.Redemptions, E("HydrateHero", "twitch", detail: "Hydrate!", count: 3)),
                Section(CreditsSections.Moderators, E("ModMarvel", "twitch"), E("BanHammerBetty", "youtube")),
                Section(CreditsSections.Vips, E("VeryImportantVal", "twitch")),
                Section(CreditsSections.Subscribers, E("CaptainCoffee", "twitch"), E("MapleMoose", "youtube")),
                Section(CreditsSections.Chatters, E("PixelPanda", "twitch"), E("LurkingLlama", "kick"), E("ChattyCheetah", "rumble")),
            ],
        };
    }

    private void Save() => _file?.ScheduleSave();

    public void Flush() => _file?.Flush();

    public void Dispose()
    {
        _subscription.Dispose();
        _file?.Dispose();
    }
}
