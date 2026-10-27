using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using Cuebot.App.Updates;

namespace Cuebot.UiTests.Support;

/// <summary>Serves canned GitHub API answers; records every request. No network.</summary>
internal sealed class FakeGitHub : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];
    public Func<HttpRequestMessage, HttpResponseMessage> Respond { get; set; } = _ => new HttpResponseMessage(HttpStatusCode.NotFound);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return Task.FromResult(Respond(request));
    }

    public static HttpResponseMessage Json(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    public static string Release(string tag, bool prerelease = false, bool draft = false, string body = "Fixes and features") => $$"""
        {
          "tag_name": "{{tag}}", "name": "Cuebot {{tag}}", "body": "{{body}}", "draft": {{(draft ? "true" : "false")}},
          "prerelease": {{(prerelease ? "true" : "false")}}, "html_url": "https://github.com/TechDaddyKB/cuebot/releases/tag/{{tag}}",
          "published_at": "2026-10-20T10:00:00Z",
          "assets": [
            { "name": "Cuebot-win-x64.zip", "browser_download_url": "https://github.com/TechDaddyKB/cuebot/releases/download/{{tag}}/Cuebot-win-x64.zip", "size": 1000 },
            { "name": "Cuebot-x86_64.AppImage", "browser_download_url": "https://example.invalid/{{tag}}/Cuebot-x86_64.AppImage", "size": 2000 },
            { "name": "Cuebot-{{tag.TrimStart('v')}}-linux-arm64.tar.gz", "browser_download_url": "https://example.invalid/arm.tar.gz", "size": 3000 }
          ]
        }
        """;
}

public sealed class SemVersionTests
{
    [Theory]
    [InlineData("1.0.0", "0.9.9")]
    [InlineData("0.2.0", "0.1.9")]
    [InlineData("0.1.10", "0.1.9")]
    [InlineData("1.0.0", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.2", "1.0.0-rc.1")]
    [InlineData("1.0.0-rc.10", "1.0.0-rc.2")]
    [InlineData("1.0.0-beta", "1.0.0-alpha")]
    [InlineData("1.0.0-alpha.beta", "1.0.0-alpha.1")]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha")]
    [InlineData("v2.0", "1.99.99")]
    public void Newer_is_greater(string newer, string older)
    {
        Assert.True(SemVersion.Parse(newer) > SemVersion.Parse(older));
        Assert.True(SemVersion.Parse(older) < SemVersion.Parse(newer));
    }

    [Theory]
    [InlineData("0.1.0+abc123", "0.1.0")]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("0.1", "0.1.0")]
    public void Build_metadata_and_prefix_are_ignored(string a, string b) => Assert.Equal(SemVersion.Parse(a), SemVersion.Parse(b));

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1")]
    [InlineData("1.2.3.4")]
    [InlineData("1.2.x")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3-a..b")]
    public void Invalid_versions_do_not_parse(string text) => Assert.False(SemVersion.TryParse(text, out _));

    [Fact]
    public void Current_comes_from_the_app_assembly()
    {
        Assert.Equal(SemVersion.FromAssembly(typeof(Cuebot.App.AppHost).Assembly), SemVersion.Current);
        Assert.Equal("1.2.3-rc.1", SemVersion.Parse("v1.2.3-rc.1+sha").ToString());
    }
}

public sealed class PackagingTests
{
    private static PackagingEnvironment Linux(Dictionary<string, string>? env = null, string[]? files = null, string dir = "/opt/cuebot/") =>
        new(false, true, n => env is not null && env.TryGetValue(n, out var v) ? v : null, f => files?.Contains(f) == true, dir, Architecture.X64);

    [Fact]
    public void Detects_each_packaging()
    {
        Assert.Equal(PackageKind.WindowsZip, PackagingDetector.Detect(new(true, false, _ => null, _ => false, @"C:\Cuebot\", Architecture.X64)));
        Assert.Equal(PackageKind.Flatpak, PackagingDetector.Detect(Linux(files: ["/.flatpak-info"], dir: "/app/lib/cuebot/")));
        Assert.Equal(PackageKind.Flatpak, PackagingDetector.Detect(Linux(new() { ["FLATPAK_ID"] = "io.github.cuebot.Cuebot" })));
        Assert.Equal(PackageKind.AppImage, PackagingDetector.Detect(Linux(new() { ["APPIMAGE"] = "/home/u/Cuebot.AppImage" }, dir: "/tmp/.mount_x/usr/lib/cuebot/")));
        Assert.Equal(PackageKind.SystemPackage, PackagingDetector.Detect(Linux(dir: "/usr/lib/cuebot/")));
        Assert.Equal(PackageKind.LinuxTarball, PackagingDetector.Detect(Linux(dir: "/home/u/Cuebot-0.1.0/lib/")));
        Assert.Equal(PackageKind.LinuxTarball, PackagingDetector.Detect(Linux(dir: "/usr/lib/cuebotx/")));
        Assert.Equal(PackageKind.Other, PackagingDetector.Detect(new(false, false, _ => null, _ => false, "/Applications/", Architecture.Arm64)));
    }

    [Fact]
    public void Assets_and_instructions_per_packaging()
    {
        Assert.Equal("Cuebot-win-x64.zip", PackagingDetector.AssetName(PackageKind.WindowsZip, "0.2.0", Architecture.X64));
        Assert.Equal("Cuebot-x86_64.AppImage", PackagingDetector.AssetName(PackageKind.AppImage, "0.2.0", Architecture.X64));
        Assert.Equal("Cuebot-0.2.0-linux-arm64.tar.gz", PackagingDetector.AssetName(PackageKind.LinuxTarball, "0.2.0", Architecture.Arm64));
        Assert.Null(PackagingDetector.AssetName(PackageKind.Flatpak, "0.2.0", Architecture.X64));
        Assert.Null(PackagingDetector.AssetName(PackageKind.SystemPackage, "0.2.0", Architecture.X64));
        Assert.Contains("flatpak update", PackagingDetector.Instructions(PackageKind.Flatpak));
        Assert.Contains("package manager", PackagingDetector.Instructions(PackageKind.SystemPackage));
    }
}

public sealed class UpdateCheckerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 12, 0, 0, TimeSpan.Zero);

    private static (UpdateChecker Checker, FakeGitHub Fake, HttpClient Http) Make(string current = "0.1.0")
    {
        var fake = new FakeGitHub();
        var http = UpdateChecker.CreateHttpClient(fake);
        return (new UpdateChecker(http, SemVersion.Parse(current), () => Now), fake, http);
    }

    [Fact]
    public async Task Latest_release_newer_than_current_is_an_update()
    {
        var (checker, fake, http) = Make();
        using var _ = http;
        fake.Respond = _ => FakeGitHub.Json(FakeGitHub.Release("v0.2.0"));
        var result = await checker.CheckAsync(UpdateChannel.Stable, TestContext.Current.CancellationToken);

        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("Update available: v0.2.0", result.Message);
        Assert.Equal(Now, result.CheckedAt);
        var release = result.Release!;
        Assert.Equal("0.2.0", release.Version.ToString());
        Assert.Equal("Fixes and features", release.Notes);
        Assert.Equal("https://github.com/TechDaddyKB/cuebot/releases/tag/v0.2.0", release.HtmlUrl);
        Assert.NotNull(release.FindAsset("cuebot-win-x64.zip"));
        var request = Assert.Single(fake.Requests);
        Assert.Equal("https://api.github.com/repos/TechDaddyKB/cuebot/releases/latest", request.RequestUri!.AbsoluteUri);
        Assert.Contains("Cuebot", request.Headers.UserAgent.ToString());
        Assert.Contains("application/vnd.github+json", request.Headers.Accept.ToString());
    }

    [Fact]
    public async Task Same_or_older_release_is_up_to_date()
    {
        var (checker, fake, http) = Make("0.2.0");
        using var _ = http;
        fake.Respond = _ => FakeGitHub.Json(FakeGitHub.Release("v0.2.0"));
        Assert.Equal(UpdateCheckStatus.UpToDate, (await checker.CheckAsync(UpdateChannel.Stable, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Prerelease_channel_picks_the_newest_non_draft_release()
    {
        var (checker, fake, http) = Make("0.2.0");
        using var _ = http;
        fake.Respond = _ => FakeGitHub.Json($"[{FakeGitHub.Release("v0.4.0", draft: true)},{FakeGitHub.Release("v0.3.0-rc.2", prerelease: true)},"
                                            + $"{FakeGitHub.Release("v0.3.0-rc.10", prerelease: true)},{FakeGitHub.Release("v0.2.0")},{FakeGitHub.Release("nightly")}]");
        var result = await checker.CheckAsync(UpdateChannel.Prerelease, TestContext.Current.CancellationToken);
        Assert.Equal(UpdateCheckStatus.UpdateAvailable, result.Status);
        Assert.Equal("0.3.0-rc.10", result.Release!.Version.ToString());
        Assert.True(result.Release.Prerelease);
        Assert.StartsWith("https://api.github.com/repos/TechDaddyKB/cuebot/releases?", fake.Requests[0].RequestUri!.AbsoluteUri);
    }

    [Fact]
    public async Task Private_repo_or_no_releases_is_reported_as_such()
    {
        var (checker, fake, http) = Make();
        using var _ = http;
        fake.Respond = _ => new HttpResponseMessage(HttpStatusCode.NotFound);
        var result = await checker.CheckAsync(UpdateChannel.Stable, TestContext.Current.CancellationToken);
        Assert.Equal(UpdateCheckStatus.NoPublicReleases, result.Status);
        Assert.Equal("No public releases yet.", result.Message);

        fake.Respond = _ => FakeGitHub.Json("[]");
        Assert.Equal(UpdateCheckStatus.NoPublicReleases, (await checker.CheckAsync(UpdateChannel.Prerelease, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Rate_limit_is_recognised()
    {
        var (checker, fake, http) = Make();
        using var _ = http;
        fake.Respond = _ =>
        {
            var r = new HttpResponseMessage(HttpStatusCode.Forbidden);
            r.Headers.Add("x-ratelimit-remaining", "0");
            r.Headers.Add("x-ratelimit-reset", "1793102400");
            return r;
        };
        var result = await checker.CheckAsync(UpdateChannel.Stable, TestContext.Current.CancellationToken);
        Assert.Equal(UpdateCheckStatus.RateLimited, result.Status);
        Assert.Contains("rate limit", result.Message);

        fake.Respond = _ => new HttpResponseMessage(HttpStatusCode.TooManyRequests);
        Assert.Equal(UpdateCheckStatus.RateLimited, (await checker.CheckAsync(UpdateChannel.Stable, TestContext.Current.CancellationToken)).Status);

        fake.Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden);
        Assert.Equal(UpdateCheckStatus.Failed, (await checker.CheckAsync(UpdateChannel.Stable, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public async Task Network_errors_and_bad_json_fail_without_throwing()
    {
        var (checker, fake, http) = Make();
        using var _ = http;
        fake.Respond = _ => throw new HttpRequestException("No route to host");
        var result = await checker.CheckAsync(UpdateChannel.Stable, TestContext.Current.CancellationToken);
        Assert.Equal(UpdateCheckStatus.Failed, result.Status);
        Assert.Contains("No route to host", result.Message);

        fake.Respond = _ => FakeGitHub.Json("{ nope");
        Assert.Equal(UpdateCheckStatus.Failed, (await checker.CheckAsync(UpdateChannel.Stable, TestContext.Current.CancellationToken)).Status);
    }

    [Fact]
    public void Start_check_is_due_once_a_day_and_only_when_enabled()
    {
        var s = new UpdateSettings();
        Assert.True(s.StartCheckDue(Now));
        s.LastChecked = Now.AddHours(-23);
        Assert.False(s.StartCheckDue(Now));
        s.LastChecked = Now.AddHours(-24);
        Assert.True(s.StartCheckDue(Now));
        s.LastChecked = Now.AddDays(3);   // clock went backwards
        Assert.True(s.StartCheckDue(Now));
        s.CheckOnStart = false;
        Assert.False(s.StartCheckDue(Now));
    }
}
