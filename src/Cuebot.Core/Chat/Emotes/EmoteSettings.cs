namespace Cuebot.Core.Chat.Emotes;

/// <summary>emotes.json: which third-party providers to load, cache lifetime, and manual channel ids.</summary>
public sealed class EmoteSettings
{
    public bool SevenTv { get; set; } = true;
    public bool Bttv { get; set; } = true;
    public bool Ffz { get; set; } = true;

    /// <summary>How long a downloaded set stays fresh before it is fetched again.</summary>
    public double CacheHours { get; set; } = 6;

    /// <summary>Fetch every set again when a stream goes live.</summary>
    public bool RefreshOnStreamStart { get; set; } = true;

    /// <summary>
    /// Channel ids per platform ("Twitch" → broadcaster user id, "YouTube" → channel id, "Kick" → user id). Used when a
    /// module does not report its channel itself.
    /// </summary>
    public Dictionary<string, string> Channels { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool IsEnabled(string provider) => provider switch
    {
        EmoteProviders.SevenTv => SevenTv,
        EmoteProviders.Bttv => Bttv,
        EmoteProviders.Ffz => Ffz,
        _ => true,
    };
}
