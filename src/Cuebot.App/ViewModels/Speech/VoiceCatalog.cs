using Cuebot.Speech;

namespace Cuebot.App.ViewModels.Speech;

/// <summary>A voice as the pickers show it.</summary>
public sealed record VoiceOption(string Id, string Name, string Detail, IReadOnlyList<string> Styles)
{
    public static VoiceOption From(VoiceInfo voice) =>
        new(voice.Id, voice.Name.Length > 0 ? voice.Name : voice.Id,
            string.Join(" · ", new[] { voice.Language, voice.Gender, voice.Tier }.Where(s => !string.IsNullOrWhiteSpace(s))),
            voice.Styles);

    public override string ToString() => Name;
}

/// <summary>
/// Loads and caches each engine's voice list so many pickers do not each call a cloud API. Failed loads are not
/// cached; <see cref="Invalidate"/> drops a list after credentials change.
/// </summary>
public sealed class VoiceCatalog(IReadOnlyList<ITtsEngine> engines)
{
    private static readonly TimeSpan LoadTimeout = TimeSpan.FromSeconds(30);
    private readonly Dictionary<string, Task<IReadOnlyList<VoiceOption>>> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    /// <summary>Raised (on the caller's thread) with the engine id whose list was dropped.</summary>
    public event Action<string>? Invalidated;

    public ITtsEngine? Engine(string? engineId) =>
        engines.FirstOrDefault(e => string.Equals(e.Id, engineId, StringComparison.OrdinalIgnoreCase));

    /// <summary>The engine's voices; empty for an unknown or unavailable engine. Throws when the engine fails to list them.</summary>
    public Task<IReadOnlyList<VoiceOption>> GetAsync(string? engineId)
    {
        var engine = Engine(engineId);
        if (engine is null || !engine.IsAvailable) return Task.FromResult<IReadOnlyList<VoiceOption>>([]);
        lock (_gate)
        {
            if (_cache.TryGetValue(engine.Id, out var cached) && !cached.IsFaulted && !cached.IsCanceled) return cached;
            var task = LoadAsync(engine);
            _cache[engine.Id] = task;
            return task;
        }
    }

    public void Invalidate(string engineId)
    {
        lock (_gate) _cache.Remove(engineId);
        Invalidated?.Invoke(engineId);
    }

    private static async Task<IReadOnlyList<VoiceOption>> LoadAsync(ITtsEngine engine)
    {
        using var cts = new CancellationTokenSource(LoadTimeout);
        var voices = await engine.GetVoicesAsync(cts.Token).ConfigureAwait(false);
        return voices.Select(VoiceOption.From).OrderBy(v => v.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}
