namespace Cuebot.Core.Chat.Emotes;

/// <summary>Names of the emote sources Cuebot knows about.</summary>
public static class EmoteProviders
{
    public const string Twitch = "Twitch";
    public const string YouTube = "YouTube";
    public const string Kick = "Kick";
    public const string Rumble = "Rumble";
    public const string SevenTv = "7TV";
    public const string Bttv = "BTTV";
    public const string Ffz = "FFZ";

    /// <summary>Third-party providers whose emotes are plain words in the message text.</summary>
    public static readonly IReadOnlyList<string> ThirdParty = [SevenTv, Bttv, Ffz];
}

/// <summary>One emote: the text that triggers it, where it comes from, and an image to draw.</summary>
/// <param name="Code">Text as typed in chat, case-sensitive (e.g. "KEKW").</param>
/// <param name="Id">Provider's id for the emote.</param>
/// <param name="Provider">One of <see cref="EmoteProviders"/>.</param>
/// <param name="ImageUrl">Small (1x) image; PNG, GIF or WebP.</param>
public sealed record Emote(string Code, string Id, string Provider, string ImageUrl)
{
    /// <summary>Drawn on top of the previous emote instead of beside it (7TV zero-width, FFZ modifiers, BTTV overlays).</summary>
    public bool IsZeroWidth { get; init; }

    public bool IsAnimated { get; init; }

    /// <summary>Platform the emote belongs to, or null for a global set usable everywhere the provider is.</summary>
    public string? Platform { get; init; }

    /// <summary>"global" or "channel" for third-party sets; "message" for emotes reported with a single message.</summary>
    public string Scope { get; init; } = "global";
}

/// <summary>An emote found in a message.</summary>
/// <param name="Start">UTF-16 index into the message text (what string APIs and UI text use).</param>
/// <param name="Length">UTF-16 length of the matched text.</param>
/// <param name="Text">The matched text itself (usually the emote code; for Kick, the whole "[emote:id:name]" token).</param>
public sealed record EmoteMatch(int Start, int Length, string Text, Emote Emote)
{
    public int End => Start + Length;
    public string Code => Emote.Code;
    public string Provider => Emote.Provider;
    public string ImageUrl => Emote.ImageUrl;
}

/// <summary>
/// An emote a platform module reports together with a message, for platforms that do not use the Twitch range
/// format (YouTube custom emoji, Rumble emotes...). Modules put a list of these in the event's "emoteList" argument.
/// </summary>
/// <param name="Start">UTF-16 index of the emote text in the message.</param>
/// <param name="Length">UTF-16 length of the emote text.</param>
public sealed record PlatformEmote(int Start, int Length, string Code, string Id, string ImageUrl)
{
    public bool IsAnimated { get; init; }
}

/// <summary>The channel whose emote sets should be loaded on a platform.</summary>
/// <param name="Platform">"Twitch", "YouTube", "Kick"...</param>
/// <param name="ChannelId">The platform's numeric/opaque user or channel id (not the login name).</param>
public sealed record EmoteChannel(string Platform, string ChannelId);

/// <summary>
/// Implemented by platform modules that know which channel they are connected to (the broadcaster's user id), so
/// channel emote sets load without manual setup. Optional: ids can also be entered in emotes.json or discovered from
/// event arguments (broadcasterUserId, broadcasterId, roomId, channelId).
/// </summary>
public interface IEmoteChannelSource
{
    string Platform { get; }

    /// <summary>The connected channel's id, or null while unknown.</summary>
    string? ChannelId { get; }
}

/// <summary>What changed when a provider's set was refreshed.</summary>
public sealed record EmoteSetChange(string Provider, string? Platform, string Scope,
    IReadOnlyList<Emote> Added, IReadOnlyList<Emote> Removed, int Count)
{
    /// <summary>True for the first load of a set (no previous copy in memory or on disk).</summary>
    public bool IsInitialLoad { get; init; }
}
