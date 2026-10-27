using System.Net;
using System.Text.Json;

namespace Cuebot.Core.Chat.Emotes;

/// <summary>A third-party emote source with global and per-channel sets fetched over public HTTP APIs.</summary>
public interface IEmoteProvider
{
    /// <summary>One of <see cref="EmoteProviders"/>.</summary>
    string Name { get; }

    /// <summary>Whether the provider has emotes for a platform's channels ("Twitch", "YouTube", "Kick").</summary>
    bool SupportsPlatform(string platform);

    Task<IReadOnlyList<Emote>> FetchGlobalAsync(CancellationToken ct);

    /// <summary>A channel's emotes; empty when the channel has no account with the provider.</summary>
    Task<IReadOnlyList<Emote>> FetchChannelAsync(EmoteChannel channel, CancellationToken ct);
}

/// <summary>Shared HTTP/JSON plumbing for providers.</summary>
public abstract class JsonEmoteProvider(HttpClient http) : IEmoteProvider
{
    public abstract string Name { get; }
    public abstract bool SupportsPlatform(string platform);
    public abstract Task<IReadOnlyList<Emote>> FetchGlobalAsync(CancellationToken ct);
    public abstract Task<IReadOnlyList<Emote>> FetchChannelAsync(EmoteChannel channel, CancellationToken ct);

    /// <summary>GETs JSON; returns null on 404 (no such user/room), throws on other failures.</summary>
    protected async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "Cuebot/0.1");
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{Name} returned {(int)response.StatusCode} {response.ReasonPhrase}", null, response.StatusCode);
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    protected static string Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Number => v.GetRawText(),
                _ => "",
            }
            : "";

    protected static bool Bool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    protected static long Long(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;

    protected static JsonElement Child(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v : default;

    protected static IEnumerable<JsonElement> Items(JsonElement e) =>
        e.ValueKind == JsonValueKind.Array ? e.EnumerateArray() : [];

    /// <summary>Turns protocol-relative "//cdn..." URLs into https ones.</summary>
    protected static string Https(string url) => url.StartsWith("//", StringComparison.Ordinal) ? "https:" + url : url;

    protected static string PlatformKey(string platform) => platform.ToLowerInvariant();
}

/// <summary>
/// 7TV (https://7tv.io/v3): global set at /emote-sets/global, a channel's active set at /users/{platform}/{id}
/// (platform "twitch", "youtube" or "kick"). Zero-width emotes are flagged on the active emote (bit 0) or its data (bit 8).
/// </summary>
public sealed class SevenTvEmoteProvider(HttpClient http, string baseUrl = "https://7tv.io/v3/") : JsonEmoteProvider(http)
{
    public override string Name => EmoteProviders.SevenTv;

    public override bool SupportsPlatform(string platform) =>
        platform.Equals("Twitch", StringComparison.OrdinalIgnoreCase)
        || platform.Equals("YouTube", StringComparison.OrdinalIgnoreCase)
        || platform.Equals("Kick", StringComparison.OrdinalIgnoreCase);

    public override async Task<IReadOnlyList<Emote>> FetchGlobalAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync(baseUrl + "emote-sets/global", ct).ConfigureAwait(false);
        return doc is null ? [] : ParseSet(doc.RootElement, null, "global");
    }

    public override async Task<IReadOnlyList<Emote>> FetchChannelAsync(EmoteChannel channel, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{baseUrl}users/{PlatformKey(channel.Platform)}/{Uri.EscapeDataString(channel.ChannelId)}", ct)
            .ConfigureAwait(false);
        return doc is null ? [] : ParseSet(Child(doc.RootElement, "emote_set"), channel.Platform, "channel");
    }

    internal static IReadOnlyList<Emote> ParseSet(JsonElement set, string? platform, string scope)
    {
        var emotes = new List<Emote>();
        foreach (var active in Items(Child(set, "emotes")))
        {
            var id = Str(active, "id");
            var name = Str(active, "name");
            if (id.Length == 0 || name.Length == 0) continue;
            var data = Child(active, "data");
            var zeroWidth = (Long(active, "flags") & 1) != 0 || (Long(data, "flags") & 256) != 0;
            emotes.Add(new Emote(name, id, EmoteProviders.SevenTv, ImageUrl(id, data))
            {
                IsZeroWidth = zeroWidth, IsAnimated = Bool(data, "animated"), Platform = platform, Scope = scope,
            });
        }
        return emotes;
    }

    /// <summary>Picks the 1x file, preferring GIF/PNG (decodable everywhere) over WebP/AVIF.</summary>
    private static string ImageUrl(string id, JsonElement data)
    {
        var host = Child(data, "host");
        var baseUrl = Https(Str(host, "url"));
        if (baseUrl.Length == 0) baseUrl = $"https://cdn.7tv.app/emote/{id}";
        var files = Items(Child(host, "files")).Select(f => Str(f, "name")).Where(n => n.StartsWith("1x.", StringComparison.Ordinal)).ToArray();
        var pick = files.FirstOrDefault(n => n.EndsWith(".gif", StringComparison.OrdinalIgnoreCase))
                   ?? files.FirstOrDefault(n => n.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                   ?? files.FirstOrDefault(n => n.EndsWith(".webp", StringComparison.OrdinalIgnoreCase))
                   ?? "1x.webp";
        return $"{baseUrl}/{pick}";
    }
}

/// <summary>
/// BetterTTV (https://api.betterttv.net/3): global emotes at /cached/emotes/global, a channel's own and shared emotes at
/// /cached/users/{twitch|youtube}/{id}. Images at cdn.betterttv.net/emote/{id}/1x.
/// </summary>
public sealed class BttvEmoteProvider(HttpClient http, string baseUrl = "https://api.betterttv.net/3/") : JsonEmoteProvider(http)
{
    /// <summary>BTTV's global overlay emotes, drawn over the previous emote.</summary>
    public static readonly IReadOnlySet<string> ZeroWidthCodes = new HashSet<string>(StringComparer.Ordinal)
    {
        "SoSnowy", "IceCold", "SantaHat", "TopHat", "ReinDeer", "CandyCane", "cvMask", "cvHazmat",
    };

    public override string Name => EmoteProviders.Bttv;

    public override bool SupportsPlatform(string platform) =>
        platform.Equals("Twitch", StringComparison.OrdinalIgnoreCase) || platform.Equals("YouTube", StringComparison.OrdinalIgnoreCase);

    public override async Task<IReadOnlyList<Emote>> FetchGlobalAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync(baseUrl + "cached/emotes/global", ct).ConfigureAwait(false);
        return doc is null ? [] : Items(doc.RootElement).Select(e => Parse(e, null, "global")).OfType<Emote>().ToArray();
    }

    public override async Task<IReadOnlyList<Emote>> FetchChannelAsync(EmoteChannel channel, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{baseUrl}cached/users/{PlatformKey(channel.Platform)}/{Uri.EscapeDataString(channel.ChannelId)}", ct)
            .ConfigureAwait(false);
        if (doc is null) return [];
        return Items(Child(doc.RootElement, "channelEmotes")).Concat(Items(Child(doc.RootElement, "sharedEmotes")))
            .Select(e => Parse(e, channel.Platform, "channel")).OfType<Emote>().ToArray();
    }

    private static Emote? Parse(JsonElement e, string? platform, string scope)
    {
        var id = Str(e, "id");
        var code = Str(e, "code");
        if (id.Length == 0 || code.Length == 0) return null;
        return new Emote(code, id, EmoteProviders.Bttv, $"https://cdn.betterttv.net/emote/{id}/1x")
        {
            IsAnimated = Bool(e, "animated") || Str(e, "imageType") == "gif",
            IsZeroWidth = ZeroWidthCodes.Contains(code),
            Platform = platform,
            Scope = scope,
        };
    }
}

/// <summary>
/// FrankerFaceZ (https://api.frankerfacez.com/v1, Twitch only): global sets at /set/global (only "default_sets" are
/// active for everyone), a channel's set at /room/id/{twitchUserId}. Modifier emotes count as zero-width.
/// </summary>
public sealed class FfzEmoteProvider(HttpClient http, string baseUrl = "https://api.frankerfacez.com/v1/") : JsonEmoteProvider(http)
{
    public override string Name => EmoteProviders.Ffz;

    public override bool SupportsPlatform(string platform) => platform.Equals("Twitch", StringComparison.OrdinalIgnoreCase);

    public override async Task<IReadOnlyList<Emote>> FetchGlobalAsync(CancellationToken ct)
    {
        using var doc = await GetJsonAsync(baseUrl + "set/global", ct).ConfigureAwait(false);
        if (doc is null) return [];
        var root = doc.RootElement;
        var defaults = Items(Child(root, "default_sets")).Select(s => s.GetRawText()).ToHashSet(StringComparer.Ordinal);
        var sets = Child(root, "sets");
        if (sets.ValueKind != JsonValueKind.Object) return [];
        return sets.EnumerateObject()
            .Where(s => defaults.Count == 0 || defaults.Contains(s.Name))
            .SelectMany(s => ParseSet(s.Value, null, "global"))
            .ToArray();
    }

    public override async Task<IReadOnlyList<Emote>> FetchChannelAsync(EmoteChannel channel, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{baseUrl}room/id/{Uri.EscapeDataString(channel.ChannelId)}", ct).ConfigureAwait(false);
        if (doc is null) return [];
        var sets = Child(doc.RootElement, "sets");
        if (sets.ValueKind != JsonValueKind.Object) return [];
        return sets.EnumerateObject().SelectMany(s => ParseSet(s.Value, channel.Platform, "channel")).ToArray();
    }

    private static IEnumerable<Emote> ParseSet(JsonElement set, string? platform, string scope)
    {
        foreach (var e in Items(Child(set, "emoticons")))
        {
            var id = Str(e, "id");
            var name = Str(e, "name");
            if (id.Length == 0 || name.Length == 0) continue;
            var animated = Child(e, "animated");
            var animatedUrl = animated.ValueKind == JsonValueKind.Object ? Str(animated, "1") : "";
            var url = animatedUrl.Length > 0 ? animatedUrl : Str(Child(e, "urls"), "1");
            if (url.Length == 0) url = $"https://cdn.frankerfacez.com/emote/{id}/1";
            yield return new Emote(name, id, EmoteProviders.Ffz, Https(url))
            {
                IsAnimated = animatedUrl.Length > 0,
                IsZeroWidth = Bool(e, "modifier"),
                Platform = platform,
                Scope = scope,
            };
        }
    }
}
