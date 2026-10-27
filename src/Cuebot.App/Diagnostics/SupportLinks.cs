namespace Cuebot.App.Diagnostics;

/// <summary>Where problem reports and releases live. The repository slug is the single place to change it.</summary>
public static class SupportLinks
{
    /// <summary>GitHub "owner/name" of the Cuebot repository.</summary>
    public const string RepoSlug = "TechDaddyKB/cuebot";

    public const string RepoUrl = "https://github.com/" + RepoSlug;

    public const string NewIssueUrl = RepoUrl + "/issues/new";

    public const string ReleasesUrl = RepoUrl + "/releases";

    public const string ApiBase = "https://api.github.com/repos/" + RepoSlug;

    public const string LatestReleaseApi = ApiBase + "/releases/latest";

    /// <summary>All releases, newest first (includes pre-releases).</summary>
    public const string ReleasesApi = ApiBase + "/releases?per_page=30";
}
