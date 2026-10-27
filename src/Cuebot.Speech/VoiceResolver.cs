using System.Collections.Concurrent;

namespace Cuebot.Speech;

/// <summary>An engine voice ready to synthesize with.</summary>
/// <param name="AliasName">Alias that was used, or empty when the engine's default voice was picked.</param>
internal sealed record ResolvedVoice(ITtsEngine Engine, string VoiceId, int Rate, int Volume, string AliasName)
{
    public int Pitch { get; init; }
    public string Style { get; init; } = "";

    /// <summary>Output device; empty for the global default.</summary>
    public string DeviceId { get; init; } = "";

    /// <summary>"engineId|voiceId", as stored for sticky random voices and voice ignore lists.</summary>
    public string Key => VoiceResolver.KeyOf(Engine.Id, VoiceId);
}

/// <summary>Turns an alias name into an engine voice, falling back to a sensible default.</summary>
internal sealed class VoiceResolver(IReadOnlyList<ITtsEngine> engines, Random? random = null)
{
    private static readonly TimeSpan VoiceCacheLifetime = TimeSpan.FromMinutes(10);
    private readonly Random _random = random ?? Random.Shared;
    private readonly ConcurrentDictionary<string, int> _roundRobin = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, (DateTimeOffset Loaded, IReadOnlyList<VoiceInfo> Voices)> _voiceCache = new();

    public static string KeyOf(string engineId, string voiceId) => $"{engineId}|{voiceId}";

    /// <summary>
    /// Resolves <paramref name="aliasName"/> case-insensitively. A missing or unknown name uses
    /// <paramref name="defaultAlias"/>, then the first alias; with no aliases at all, the first available engine's
    /// first voice.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing can speak.</exception>
    public async Task<ResolvedVoice> ResolveAsync(string? aliasName, IReadOnlyList<VoiceAlias> aliases, CancellationToken ct,
        string? defaultAlias = null)
    {
        var alias = FindAlias(aliasName, aliases) ?? FindAlias(defaultAlias, aliases) ?? aliases.FirstOrDefault();
        if (alias is not null) return Resolve(alias);

        foreach (var engine in engines.Where(e => e.IsAvailable))
        {
            var voices = await engine.GetVoicesAsync(ct).ConfigureAwait(false);
            if (voices.Count > 0) return new ResolvedVoice(engine, voices[0].Id, Rate: 0, Volume: 100, AliasName: "");
        }
        throw new InvalidOperationException("No voice is available. Add a voice alias or install a speech engine.");
    }

    /// <summary>
    /// Resolves an explicit alias (e.g. one being edited and tested). Aliases with several voices pick one at random
    /// (by weight) or in turn; voices of unavailable engines are skipped when another voice can speak.
    /// </summary>
    /// <exception cref="InvalidOperationException">No voice of the alias can speak.</exception>
    public ResolvedVoice Resolve(VoiceAlias alias)
    {
        var voices = alias.EffectiveVoices();
        var usable = voices.Where(v => FindEngine(v.EngineId)?.IsAvailable == true).ToArray();
        if (usable.Length == 0)
        {
            var first = voices.FirstOrDefault() ?? new AliasVoice();
            var engine = FindEngine(first.EngineId)
                ?? throw new InvalidOperationException($"Voice '{alias.Name}' uses unknown engine '{first.EngineId}'.");
            throw new InvalidOperationException($"Voice '{alias.Name}' cannot speak: {engine.UnavailableReason}");
        }
        var voice = usable.Length == 1 ? usable[0] : Pick(alias, usable);
        return Build(alias, voice);
    }

    /// <summary>A random voice of any available engine, honouring the ignore profile; keeps <paramref name="sticky"/> when usable.</summary>
    public async Task<ResolvedVoice?> ResolveRandomAsync(VoiceIgnoreProfile? ignore, string? sticky, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(sticky) && sticky.Split('|', 2) is [var stickyEngine, var stickyVoice]
            && FindEngine(stickyEngine) is { IsAvailable: true } engineOfSticky)
            return new ResolvedVoice(engineOfSticky, stickyVoice, 0, 100, "");

        var candidates = new List<(ITtsEngine Engine, VoiceInfo Voice)>();
        foreach (var engine in engines.Where(e => e.IsAvailable))
        {
            IReadOnlyList<VoiceInfo> voices;
            try
            {
                voices = await GetVoicesCachedAsync(engine, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                continue; // an engine that cannot list voices just does not take part
            }
            candidates.AddRange(voices.Where(v => !IsIgnored(engine, v, ignore)).Select(v => (engine, v)));
        }
        if (candidates.Count == 0) return null;
        var (pickedEngine, pickedVoice) = candidates[_random.Next(candidates.Count)];
        return new ResolvedVoice(pickedEngine, pickedVoice.Id, 0, 100, "");
    }

    internal static bool IsIgnored(ITtsEngine engine, VoiceInfo voice, VoiceIgnoreProfile? ignore)
    {
        if (ignore is null) return false;
        if (ignore.Voices.Any(v => string.Equals(v, KeyOf(engine.Id, voice.Id), StringComparison.OrdinalIgnoreCase)
                                   || string.Equals(v, voice.Id, StringComparison.OrdinalIgnoreCase))) return true;
        return voice.Language is { } language
               && ignore.Locales.Any(l => !string.IsNullOrWhiteSpace(l)
                                          && (string.Equals(language, l.Trim(), StringComparison.OrdinalIgnoreCase)
                                              || language.StartsWith(l.Trim() + "-", StringComparison.OrdinalIgnoreCase)));
    }

    public static VoiceAlias? FindAlias(string? name, IEnumerable<VoiceAlias> aliases) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : aliases.FirstOrDefault(a => string.Equals(a.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));

    private async Task<IReadOnlyList<VoiceInfo>> GetVoicesCachedAsync(ITtsEngine engine, CancellationToken ct)
    {
        if (_voiceCache.TryGetValue(engine.Id, out var cached) && DateTimeOffset.UtcNow - cached.Loaded < VoiceCacheLifetime)
            return cached.Voices;
        var voices = await engine.GetVoicesAsync(ct).ConfigureAwait(false);
        _voiceCache[engine.Id] = (DateTimeOffset.UtcNow, voices);
        return voices;
    }

    private AliasVoice Pick(VoiceAlias alias, AliasVoice[] voices)
    {
        if (alias.Selection == VoiceSelection.RoundRobin)
        {
            var turn = _roundRobin.AddOrUpdate(alias.Name, 0, (_, previous) => previous + 1);
            return voices[turn % voices.Length];
        }
        var total = voices.Sum(v => Math.Max(1, v.Weight));
        var roll = _random.Next(total);
        foreach (var voice in voices)
        {
            roll -= Math.Max(1, voice.Weight);
            if (roll < 0) return voice;
        }
        return voices[^1];
    }

    private ResolvedVoice Build(VoiceAlias alias, AliasVoice voice)
    {
        var engine = FindEngine(voice.EngineId)!;
        var single = alias.Voices.Count == 0;
        return new ResolvedVoice(engine, voice.VoiceId,
            Rate: Math.Clamp(alias.Rate + (single ? 0 : voice.Rate), -10, 10),
            Volume: single ? alias.Volume : Math.Clamp(alias.Volume, 0, 100) * Math.Clamp(voice.Volume, 0, 100) / 100,
            AliasName: alias.Name)
        {
            Pitch = Math.Clamp(alias.Pitch + (single ? 0 : voice.Pitch), -10, 10),
            Style = voice.Style.Length > 0 ? voice.Style : alias.Style,
            DeviceId = alias.DeviceId,
        };
    }

    private ITtsEngine? FindEngine(string engineId) =>
        engines.FirstOrDefault(e => string.Equals(e.Id, engineId, StringComparison.OrdinalIgnoreCase));
}
