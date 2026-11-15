using Cuebot.Core.Games;
using Cuebot.YouTube.Api;

namespace Cuebot.YouTube;

/// <summary>
/// YouTube for the category switcher. YouTube only has broad video categories, so a game sets "Gaming"; a name that is
/// one of YouTube's categories ("Entertainment", "People &amp; Blogs") sets that one. Changes the live broadcast, else
/// the next upcoming one.
/// </summary>
internal sealed class YouTubeCategoryTarget(YouTubeService youtube) : IStreamCategoryTarget
{
    /// <summary>YouTube's standard video categories (YouTube Data API ids).</summary>
    internal static readonly Dictionary<string, string> Categories = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Film & Animation"] = "1", ["Autos & Vehicles"] = "2", ["Music"] = "10", ["Pets & Animals"] = "15", ["Sports"] = "17",
        ["Travel & Events"] = "19", ["Gaming"] = "20", ["People & Blogs"] = "22", ["Comedy"] = "23", ["Entertainment"] = "24",
        ["News & Politics"] = "25", ["Howto & Style"] = "26", ["Education"] = "27", ["Science & Technology"] = "28",
        ["Nonprofits & Activism"] = "29",
    };

    public string Platform => "YouTube";

    public bool CanSet => youtube.IsConnected && (youtube.LatestBroadcast is not null || youtube.UpcomingBroadcasts.Count > 0);

    /// <summary>Not known without another API call (quota): always proposed.</summary>
    public Task<string?> CurrentAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

    public Task<string?> FindAsync(string game, CancellationToken ct = default) =>
        Task.FromResult<string?>(Categories.Keys.FirstOrDefault(k => string.Equals(k, game.Trim(), StringComparison.OrdinalIgnoreCase)) ?? "Gaming");

    public async Task<string> SetAsync(string category, CancellationToken ct = default)
    {
        if (!Categories.TryGetValue(category.Trim(), out var id)) throw new ArgumentException($"'{category}' is not a YouTube category.");
        await youtube.UpdateBroadcastAsync(new VideoChange(CategoryId: id), null, ct).ConfigureAwait(false);
        return Categories.First(c => c.Value == id).Key;
    }
}
