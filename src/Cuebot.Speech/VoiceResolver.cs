namespace Cuebot.Speech;

/// <summary>An engine voice ready to synthesize with.</summary>
/// <param name="AliasName">Alias that was used, or empty when the engine's default voice was picked.</param>
internal sealed record ResolvedVoice(ITtsEngine Engine, string VoiceId, int Rate, int Volume, string AliasName);

/// <summary>Turns an alias name into an engine voice, falling back to a sensible default.</summary>
internal sealed class VoiceResolver(IReadOnlyList<ITtsEngine> engines)
{
    /// <summary>
    /// Resolves <paramref name="aliasName"/> case-insensitively. A missing or empty name uses the first alias;
    /// with no aliases at all, the first available engine's first voice.
    /// </summary>
    /// <exception cref="InvalidOperationException">Nothing can speak.</exception>
    public async Task<ResolvedVoice> ResolveAsync(string? aliasName, IReadOnlyList<VoiceAlias> aliases, CancellationToken ct)
    {
        var alias = FindAlias(aliasName, aliases) ?? aliases.FirstOrDefault();
        if (alias is not null) return Resolve(alias);

        foreach (var engine in engines.Where(e => e.IsAvailable))
        {
            var voices = await engine.GetVoicesAsync(ct).ConfigureAwait(false);
            if (voices.Count > 0) return new ResolvedVoice(engine, voices[0].Id, Rate: 0, Volume: 100, AliasName: "");
        }
        throw new InvalidOperationException("No voice is available. Add a voice alias or install a speech engine.");
    }

    /// <summary>Resolves an explicit alias (e.g. one being edited and tested).</summary>
    /// <exception cref="InvalidOperationException">The alias' engine is unknown or unavailable.</exception>
    public ResolvedVoice Resolve(VoiceAlias alias)
    {
        var engine = engines.FirstOrDefault(e => string.Equals(e.Id, alias.EngineId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Voice '{alias.Name}' uses unknown engine '{alias.EngineId}'.");
        if (!engine.IsAvailable)
            throw new InvalidOperationException($"Voice '{alias.Name}' cannot speak: {engine.UnavailableReason}");
        return new ResolvedVoice(engine, alias.VoiceId, alias.Rate, alias.Volume, alias.Name);
    }

    private static VoiceAlias? FindAlias(string? name, IEnumerable<VoiceAlias> aliases) =>
        string.IsNullOrWhiteSpace(name)
            ? null
            : aliases.FirstOrDefault(a => string.Equals(a.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
}
