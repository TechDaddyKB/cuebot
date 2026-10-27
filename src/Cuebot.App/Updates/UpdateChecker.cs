using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Cuebot.App.Diagnostics;

namespace Cuebot.App.Updates;

public enum UpdateChannel { Stable, Prerelease }

public enum UpdateCheckStatus { UpToDate, UpdateAvailable, NoPublicReleases, RateLimited, Failed }

public sealed record ReleaseAsset(string Name, string DownloadUrl, long Size);

public sealed record ReleaseInfo(
    SemVersion Version,
    string Tag,
    string Name,
    string Notes,
    string HtmlUrl,
    bool Prerelease,
    DateTimeOffset? Published,
    IReadOnlyList<ReleaseAsset> Assets)
{
    public ReleaseAsset? FindAsset(string? name) =>
        name is null ? null : Assets.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public sealed record UpdateCheckResult(UpdateCheckStatus Status, ReleaseInfo? Release, string Message, DateTimeOffset CheckedAt);

/// <summary>
/// Asks the GitHub Releases API for the newest release (stable: <c>/releases/latest</c>; pre-release channel: the
/// newest non-draft entry of <c>/releases</c>) and compares it with this build. Read-only: nothing is downloaded or
/// installed. Never throws for network or API problems; they come back as <see cref="UpdateCheckStatus.Failed"/>.
/// </summary>
public sealed class UpdateChecker
{
    private readonly HttpClient _http;
    private readonly SemVersion _current;
    private readonly Func<DateTimeOffset> _clock;

    public UpdateChecker(HttpClient http, SemVersion current, Func<DateTimeOffset>? clock = null)
    {
        _http = http;
        _current = current;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public SemVersion Current => _current;

    /// <summary>An HttpClient set up for the GitHub API (user agent, accept header, timeout).</summary>
    public static HttpClient CreateHttpClient(HttpMessageHandler? handler = null)
    {
        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.Timeout = TimeSpan.FromSeconds(15);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Cuebot", SemVersion.Current.ToString()));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
        return http;
    }

    public async Task<UpdateCheckResult> CheckAsync(UpdateChannel channel, CancellationToken ct = default)
    {
        var now = _clock();
        try
        {
            var url = channel == UpdateChannel.Prerelease ? SupportLinks.ReleasesApi : SupportLinks.LatestReleaseApi;
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return new(UpdateCheckStatus.NoPublicReleases, null, "No public releases yet.", now);
            if (IsRateLimited(response, out var reset))
                return new(UpdateCheckStatus.RateLimited, null,
                    reset is { } r ? $"GitHub's rate limit was reached; try again after {r.ToLocalTime():HH:mm}." : "GitHub's rate limit was reached; try again later.", now);
            if (!response.IsSuccessStatusCode)
                return new(UpdateCheckStatus.Failed, null, $"GitHub answered {(int)response.StatusCode} {response.ReasonPhrase}.", now);

            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var release = channel == UpdateChannel.Prerelease ? Newest(doc.RootElement) : ReadRelease(doc.RootElement);
            if (release is null) return new(UpdateCheckStatus.NoPublicReleases, null, "No public releases yet.", now);
            return release.Version > _current
                ? new(UpdateCheckStatus.UpdateAvailable, release, $"Update available: v{release.Version}", now)
                : new(UpdateCheckStatus.UpToDate, release, $"Cuebot is up to date (newest release: v{release.Version}).", now);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException or InvalidOperationException)
        {
            if (ex is TaskCanceledException && ct.IsCancellationRequested) throw;
            return new(UpdateCheckStatus.Failed, null, $"Could not check for updates: {ex.Message}", now);
        }
    }

    private static bool IsRateLimited(HttpResponseMessage response, out DateTimeOffset? reset)
    {
        reset = null;
        if (response.StatusCode is not (HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)) return false;
        if (response.Headers.TryGetValues("x-ratelimit-reset", out var values)
            && long.TryParse(values.FirstOrDefault(), NumberStyles.None, CultureInfo.InvariantCulture, out var epoch))
            reset = DateTimeOffset.FromUnixTimeSeconds(epoch);
        if (response.StatusCode == HttpStatusCode.TooManyRequests) return true;
        return response.Headers.TryGetValues("x-ratelimit-remaining", out var remaining) && remaining.FirstOrDefault() == "0";
    }

    private static ReleaseInfo? Newest(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Array) return null;
        ReleaseInfo? best = null;
        foreach (var item in root.EnumerateArray())
            if (ReadRelease(item) is { } r && (best is null || r.Version > best.Version)) best = r;
        return best;
    }

    /// <summary>Reads one release object; null for drafts and tags that are not versions.</summary>
    public static ReleaseInfo? ReadRelease(JsonElement e)
    {
        if (e.ValueKind != JsonValueKind.Object) return null;
        if (Bool(e, "draft")) return null;
        var tag = Str(e, "tag_name");
        if (!SemVersion.TryParse(tag, out var version)) return null;
        var assets = new List<ReleaseAsset>();
        if (e.TryGetProperty("assets", out var a) && a.ValueKind == JsonValueKind.Array)
            foreach (var asset in a.EnumerateArray())
                assets.Add(new ReleaseAsset(Str(asset, "name"), Str(asset, "browser_download_url"),
                    asset.TryGetProperty("size", out var size) && size.TryGetInt64(out var n) ? n : 0));
        DateTimeOffset? published = DateTimeOffset.TryParse(Str(e, "published_at"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var p) ? p : null;
        var html = Str(e, "html_url");
        if (!html.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) html = $"{SupportLinks.ReleasesUrl}/tag/{Uri.EscapeDataString(tag)}";
        return new ReleaseInfo(version, tag, Str(e, "name") is { Length: > 0 } name ? name : tag, Str(e, "body"), html,
            Bool(e, "prerelease") || version.IsPrerelease, published, assets);
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}
