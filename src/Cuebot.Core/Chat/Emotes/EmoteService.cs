using System.Collections.Concurrent;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;

namespace Cuebot.Core.Chat.Emotes;

/// <summary>
/// Emotes for the combined chat, speech and scripts. Loads 7TV/BTTV/FFZ global and channel sets (memory + disk cache
/// with expiry, refreshed when a stream goes live or on demand), and finds emotes in messages: platform-reported Twitch
/// ranges ("emotes" argument), Kick inline tokens, structured "emoteList" arguments, then third-party words.
/// </summary>
public sealed class EmoteService : IDisposable
{
    /// <summary>Published after a refresh changed a set (args: provider, platform, scope, added, removed, count).</summary>
    public const string SetsChangedEvent = "chat.emotesChanged";

    /// <summary>Published per emote that appeared in / vanished from a set that was already loaded (args: provider, platform, emote, emoteId, imageUrl).</summary>
    public const string EmoteAddedEvent = "chat.emoteAdded";
    public const string EmoteRemovedEvent = "chat.emoteRemoved";

    private const string Global = "global";
    private const int IndexSize = 1000;
    private const int SeenLimit = 2000;
    private static readonly string[] ChannelKeys = ["broadcasterUserId", "broadcasterId", "roomId", "channelId"];
    private static readonly string[] KnownPlatforms = [EmoteProviders.Twitch, EmoteProviders.YouTube, EmoteProviders.Kick, EmoteProviders.Rumble];

    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly EmoteDiskCache _disk;
    private readonly Func<EmoteSettings> _settings;
    private readonly IReadOnlyList<IEmoteProvider> _providers;
    private readonly IReadOnlyList<IEmoteChannelSource> _channelSources;
    private readonly TimeProvider _time;
    private readonly IDisposable _subscription;
    private readonly ConcurrentDictionary<(string Provider, string Scope), CachedEmoteSet> _sets = new();
    private readonly ConcurrentDictionary<string, string> _discoveredChannels = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, Emote>> _merged = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Emote>> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _indexGate = new();
    private readonly Dictionary<string, IReadOnlyDictionary<string, object?>> _index = new(StringComparer.Ordinal);
    private readonly Queue<string> _indexOrder = new();
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private int _started;

    public EmoteService(EventBus bus, AppLog log, EmoteDiskCache disk, Func<EmoteSettings> settings,
        IEnumerable<IEmoteProvider> providers, IEnumerable<IEmoteChannelSource>? channelSources = null, TimeProvider? time = null)
    {
        _bus = bus;
        _log = log;
        _disk = disk;
        _settings = settings;
        _providers = providers.ToArray();
        _channelSources = channelSources?.ToArray() ?? [];
        _time = time ?? TimeProvider.System;
        _subscription = bus.Subscribe(OnEvent);
    }

    /// <summary>Raised after any set loads or changes (on a background thread).</summary>
    public event Action<EmoteSetChange>? EmotesChanged;

    public IReadOnlyList<string> ProviderNames => _providers.Select(p => p.Name).ToArray();

    public DateTimeOffset? LastRefresh { get; private set; }

    /// <summary>Loads sets once in the background (cache first), if not already started. Safe to call repeatedly.</summary>
    public void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _ = RefreshInBackground(force: false);
    }

    /// <summary>The channel used for a platform: settings, then a module's channel source, then what events reported.</summary>
    public EmoteChannel? ChannelFor(string platform)
    {
        var configured = _settings().Channels.FirstOrDefault(kv => string.Equals(kv.Key, platform, StringComparison.OrdinalIgnoreCase)).Value;
        if (!string.IsNullOrWhiteSpace(configured)) return new EmoteChannel(Canonical(platform), configured.Trim());
        foreach (var source in _channelSources)
            if (string.Equals(source.Platform, platform, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(source.ChannelId))
                return new EmoteChannel(Canonical(platform), source.ChannelId);
        return _discoveredChannels.TryGetValue(platform, out var id) ? new EmoteChannel(Canonical(platform), id) : null;
    }

    public IReadOnlyList<EmoteChannel> Channels() =>
        KnownPlatforms.Concat(_settings().Channels.Keys).Concat(_channelSources.Select(s => s.Platform)).Concat(_discoveredChannels.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(ChannelFor).OfType<EmoteChannel>().ToArray();

    /// <summary>Remembers a platform's channel (e.g. from a module that just connected) and loads its sets.</summary>
    public void SetChannel(string platform, string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId)) return;
        if (_discoveredChannels.TryGetValue(platform, out var old) && old == channelId) return;
        _discoveredChannels[platform] = channelId;
        _ = RefreshInBackground(force: false);
    }

    /// <summary>
    /// Loads every enabled provider's global set and each known channel's set. Without <paramref name="force"/>, fresh
    /// memory or disk copies are used; failures keep the last good copy. Returns the number of emotes now known.
    /// </summary>
    public async Task<int> RefreshAsync(bool force = false, CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _started, 1);
        await _refreshGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var settings = _settings();
            var channels = Channels();
            foreach (var provider in _providers)
            {
                if (!settings.IsEnabled(provider.Name)) continue;
                await LoadAsync(provider, Global, null, force, settings, ct).ConfigureAwait(false);
                foreach (var channel in channels.Where(c => provider.SupportsPlatform(c.Platform)))
                    await LoadAsync(provider, Scope(channel), channel, force, settings, ct).ConfigureAwait(false);
            }
            LastRefresh = _time.GetUtcNow();
            return _sets.Values.Sum(s => s.Emotes.Count);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private async Task RefreshInBackground(bool force)
    {
        try { await RefreshAsync(force).ConfigureAwait(false); }
        catch (Exception ex) { _log.Warn("Emotes", $"Emote refresh failed: {ex.Message}"); }
    }

    private async Task LoadAsync(IEmoteProvider provider, string scope, EmoteChannel? channel, bool force, EmoteSettings settings, CancellationToken ct)
    {
        var key = (provider.Name, scope);
        var maxAge = TimeSpan.FromHours(Math.Max(0, settings.CacheHours));
        var now = _time.GetUtcNow();
        _sets.TryGetValue(key, out var previous);
        if (!force && previous is not null && now - previous.FetchedAt < maxAge) return;

        var onDisk = previous is null ? _disk.Load(provider.Name, scope) : null;
        if (!force && onDisk is not null && now - onDisk.FetchedAt < maxAge)
        {
            Store(provider.Name, scope, channel?.Platform, null, onDisk);
            return;
        }

        try
        {
            var emotes = channel is null
                ? await provider.FetchGlobalAsync(ct).ConfigureAwait(false)
                : await provider.FetchChannelAsync(channel, ct).ConfigureAwait(false);
            var fresh = new CachedEmoteSet(now, emotes);
            _disk.Save(provider.Name, scope, fresh);
            Store(provider.Name, scope, channel?.Platform, previous ?? onDisk, fresh);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _log.Warn("Emotes", $"Could not load {provider.Name} {scope} emotes: {ex.Message}");
            if (previous is null && onDisk is not null) Store(provider.Name, scope, channel?.Platform, null, onDisk);   // stale beats nothing
        }
    }

    private void Store(string provider, string scope, string? platform, CachedEmoteSet? previous, CachedEmoteSet set)
    {
        _sets[(provider, scope)] = set;
        _merged.Clear();
        var oldCodes = previous is null ? new Dictionary<string, Emote>(StringComparer.Ordinal) : ByCode(previous.Emotes);
        var newCodes = ByCode(set.Emotes);
        var added = previous is null ? set.Emotes : newCodes.Values.Where(e => !oldCodes.ContainsKey(e.Code)).ToArray();
        var removed = oldCodes.Values.Where(e => !newCodes.ContainsKey(e.Code)).ToArray();
        if (previous is not null && added.Count == 0 && removed.Length == 0) return;

        var change = new EmoteSetChange(provider, platform, scope, added, removed, set.Emotes.Count) { IsInitialLoad = previous is null };
        try { EmotesChanged?.Invoke(change); }
        catch (Exception ex) { _log.Error("Emotes", "EmotesChanged handler failed", ex); }
        if (previous is null) return;   // first loads are not "emote added" news

        _bus.Publish(new BotEvent(SetsChangedEvent, new Dictionary<string, object?>
        {
            ["provider"] = provider,
            ["platform"] = platform ?? "",
            ["scope"] = scope,
            ["added"] = string.Join(", ", added.Select(e => e.Code)),
            ["removed"] = string.Join(", ", removed.Select(e => e.Code)),
            ["addedCount"] = added.Count,
            ["removedCount"] = removed.Length,
            ["count"] = set.Emotes.Count,
        }));
        foreach (var e in added) _bus.Publish(EmoteEvent(EmoteAddedEvent, e, platform));
        foreach (var e in removed) _bus.Publish(EmoteEvent(EmoteRemovedEvent, e, platform));
    }

    private static Dictionary<string, Emote> ByCode(IEnumerable<Emote> emotes) =>
        emotes.GroupBy(e => e.Code, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

    private static BotEvent EmoteEvent(string type, Emote e, string? platform) => new(type, new Dictionary<string, object?>
    {
        ["provider"] = e.Provider,
        ["platform"] = platform ?? "",
        ["emote"] = e.Code,
        ["emoteId"] = e.Id,
        ["imageUrl"] = e.ImageUrl,
    });

    /// <summary>
    /// Third-party emotes usable on a platform: provider globals overlaid by the platform channel's sets (channel wins
    /// on the same code; order 7TV over FFZ over BTTV within a scope, like most chat clients).
    /// </summary>
    public IReadOnlyDictionary<string, Emote> GetThirdPartyEmotes(string platform) => _merged.GetOrAdd(platform, p =>
    {
        var settings = _settings();
        var channel = ChannelFor(p);
        var result = new Dictionary<string, Emote>(StringComparer.Ordinal);
        var order = _providers.Where(x => settings.IsEnabled(x.Name) && x.SupportsPlatform(p))
            .OrderBy(x => x.Name switch { EmoteProviders.Bttv => 0, EmoteProviders.Ffz => 1, EmoteProviders.SevenTv => 2, _ => 3 })
            .ToArray();
        foreach (var scope in channel is null ? [Global] : new[] { Global, Scope(channel) })
            foreach (var provider in order)
                if (_sets.TryGetValue((provider.Name, scope), out var set))
                    foreach (var e in set.Emotes) result[e.Code] = e;
        return result;
    });

    /// <summary>Every emote known for a platform (third-party plus native emotes seen in chat), for autocomplete.</summary>
    public IReadOnlyList<Emote> GetEmotes(string platform)
    {
        var all = new Dictionary<string, Emote>(GetThirdPartyEmotes(platform), StringComparer.Ordinal);
        if (_seen.TryGetValue(platform, out var seen))
            foreach (var e in seen.Values) all.TryAdd(e.Code, e);
        return all.Values.OrderBy(e => e.Code, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>Emotes whose code starts with (then contains) <paramref name="prefix"/>, case-insensitively.</summary>
    /// <param name="platform">A platform, or <see cref="ChatHub.AllPlatforms"/>/null for every platform.</param>
    public IReadOnlyList<Emote> Search(string? platform, string prefix, int limit = 20)
    {
        var platforms = string.IsNullOrEmpty(platform) || platform == ChatHub.AllPlatforms
            ? KnownPlatforms.Concat(_seen.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            : [platform];
        var pool = platforms.SelectMany(GetEmotes).DistinctBy(e => e.Code).ToArray();
        return pool.Where(e => e.Code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.Code.Length).ThenBy(e => e.Code, StringComparer.Ordinal)
            .Concat(pool.Where(e => !e.Code.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                                    && e.Code.Contains(prefix, StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e.Code, StringComparer.Ordinal))
            .Take(limit).ToArray();
    }

    /// <summary>Finds emotes in a message from a platform, using the event's emote arguments when supplied.</summary>
    /// <param name="args">The platform event's arguments: "emotes" (Twitch ranges) and/or "emoteList" (<see cref="PlatformEmote"/>s).</param>
    public IReadOnlyList<EmoteMatch> FindEmotes(string platform, string text, IReadOnlyDictionary<string, object?>? args = null)
    {
        if (string.IsNullOrEmpty(text)) return [];
        var native = new List<EmoteMatch>();
        if (args is not null)
        {
            if (args.TryGetValue("emotes", out var ranges) && ranges is string r) native.AddRange(EmoteParser.ParseTwitchRanges(text, r));
            if (args.TryGetValue("emoteList", out var list) && list is IEnumerable<PlatformEmote> structured)
                native.AddRange(EmoteParser.FromPlatformList(Canonical(platform), text, structured));
        }
        if (string.Equals(platform, EmoteProviders.Kick, StringComparison.OrdinalIgnoreCase)) native.AddRange(EmoteParser.ParseKickTokens(text));
        var platformEmotes = EmoteParser.Merge(native);
        Remember(platform, platformEmotes);
        var words = EmoteParser.FindWords(text, GetThirdPartyEmotes(platform), platformEmotes);
        return EmoteParser.Merge(platformEmotes, words);
    }

    /// <summary>Finds emotes in a combined-chat message, using the arguments of the platform event it came from.</summary>
    public IReadOnlyList<EmoteMatch> FindEmotes(ChatMessage message) =>
        FindEmotes(message.Platform, message.Text, ArgsFor(message.Id));

    /// <summary>The original platform event arguments for a recent message id (null when unknown or too old).</summary>
    public IReadOnlyDictionary<string, object?>? ArgsFor(string messageId)
    {
        if (string.IsNullOrEmpty(messageId)) return null;
        lock (_indexGate) return _index.GetValueOrDefault(messageId);
    }

    /// <summary>Native emotes seen in chat feed autocomplete for that platform.</summary>
    private void Remember(string platform, IReadOnlyList<EmoteMatch> matches)
    {
        if (matches.Count == 0) return;
        var seen = _seen.GetOrAdd(platform, _ => new ConcurrentDictionary<string, Emote>(StringComparer.Ordinal));
        foreach (var m in matches)
            if (seen.Count < SeenLimit) seen.TryAdd(m.Emote.Code, m.Emote);
    }

    private void OnEvent(BotEvent e)
    {
        if (e.Type == ChatHub.ActivityEvent)
        {
            if (string.Equals(e.Args.GetValueOrDefault("kind") as string, nameof(FeedKind.StreamOnline), StringComparison.Ordinal)
                && _settings().RefreshOnStreamStart)
                _ = RefreshInBackground(force: true);
            return;
        }
        if (e.Type is ChatHub.MessageEvent or SetsChangedEvent or EmoteAddedEvent or EmoteRemovedEvent) return;

        var platform = PlatformOf(e.Type);
        if (platform is not null)
            foreach (var key in ChannelKeys)
                if (e.Args.GetValueOrDefault(key) is { } value && Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) is { Length: > 0 } id)
                {
                    SetChannel(platform, id);
                    break;
                }

        if (e.Args.GetValueOrDefault("messageId") is string messageId && messageId.Length > 0)
        {
            lock (_indexGate)
            {
                if (_index.TryAdd(messageId, e.Args)) _indexOrder.Enqueue(messageId);
                while (_indexOrder.Count > IndexSize) _index.Remove(_indexOrder.Dequeue());
            }
            if (platform is not null) EnsureStarted();
        }
    }

    private static string? PlatformOf(string eventType)
    {
        var dot = eventType.IndexOf('.');
        if (dot <= 0) return null;
        var prefix = eventType[..dot];
        return KnownPlatforms.FirstOrDefault(p => p.Equals(prefix, StringComparison.OrdinalIgnoreCase));
    }

    private static string Canonical(string platform) =>
        KnownPlatforms.FirstOrDefault(p => p.Equals(platform, StringComparison.OrdinalIgnoreCase)) ?? platform;

    private static string Scope(EmoteChannel channel) => $"{channel.Platform.ToLowerInvariant()}-{channel.ChannelId}";

    /// <summary>Snapshot of loaded sets (provider, scope, count, fetched), for status displays.</summary>
    public IReadOnlyList<(string Provider, string Scope, int Count, DateTimeOffset FetchedAt)> LoadedSets() =>
        _sets.Select(kv => (kv.Key.Provider, kv.Key.Scope, kv.Value.Emotes.Count, kv.Value.FetchedAt))
            .OrderBy(s => s.Provider).ThenBy(s => s.Scope).ToArray();

    public void Dispose()
    {
        _subscription.Dispose();
        _refreshGate.Dispose();
    }
}
