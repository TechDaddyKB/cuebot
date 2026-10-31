namespace Cuebot.App.Diagnostics;

/// <summary>Where problem reports and releases live. The repository slug is the single place to change it.</summary>
public static class SupportLinks
{
    /// <summary>GitHub "owner/name" of the Cuebot repository.</summary>
    public const string RepoSlug = "TechDaddyKB/cuebot";

    public const string RepoUrl = "https://github.com/" + RepoSlug;

    public const string NewIssueUrl = RepoUrl + "/issues/new";

    /// <summary>
    /// Cuebot's report intake (report/ in the repository): a Cloudflare Worker that files reports as issues through the
    /// "Cuebot Reports" GitHub App, so streamers need no GitHub account.
    /// </summary>
    public const string ReportEndpoint = "https://cuebot-report-intake.kris-barrantes.workers.dev/v1/reports";

    public const string ReleasesUrl = RepoUrl + "/releases";

    public const string ApiBase = "https://api.github.com/repos/" + RepoSlug;

    public const string LatestReleaseApi = ApiBase + "/releases/latest";

    /// <summary>All releases, newest first (includes pre-releases).</summary>
    public const string ReleasesApi = ApiBase + "/releases?per_page=30";
}
