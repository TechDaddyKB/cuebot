using Cuebot.App.Diagnostics;

namespace Cuebot.UiTests.Support;

/// <summary>The Markdown report, the new-issue link (length limit, short body fallback) and the gh command.</summary>
public sealed class ReportBuilderTests
{
    internal static EnvironmentSnapshot Env => new()
    {
        AppVersion = "0.1.0",
        BuildVersion = "0.1.0+abc123",
        Package = "AppImage",
        Os = "Linux 6.10",
        Distro = "Arch Linux",
        Architecture = "X64",
        DotNet = ".NET 10.0.0",
        RuntimeId = "linux-x64",
        Session = "wayland (WAYLAND_DISPLAY set)",
        Desktop = "KDE",
        DisplayBackend = "Wayland",
    };

    private static CollectedInfo Info(int logLines = 3, CrashRecord? crash = null) => new()
    {
        Environment = Env,
        Connections = [new("Twitch", "Connected", "Connected as techdaddykb"), new("OBS", "Disconnected", "")],
        AppSettings = """{ "general": { "showTrayIcon": true, "theme": "Dark" } }""",
        ModuleSettings = new Dictionary<string, string> { ["twitch.json"] = SecretScrubberTests.TwitchJson },
        Log = Enumerable.Range(0, logLines).Select(i => $"12:00:{i % 60:00} [Info] Twitch: line {i} as techdaddykb access_token=abcdefabcdef{i}").ToArray(),
        AccountNames = SecretScrubber.CollectAccountNames(SecretScrubberTests.TwitchJson),
        Crash = crash,
    };

    private static readonly ReportInput Input = new()
    {
        Title = "Chat stops after a raid",
        Summary = "Chat stops updating after a raid.",
        Steps = "1. Get raided\n2. Watch chat",
        Expected = "Chat keeps updating",
        Actual = "Chat froze",
    };

    [Fact]
    public void Report_has_user_sections_environment_and_scrubbed_collapsible_sections()
    {
        var body = ReportBuilder.Build(Input, Info(), new ReportOptions { IncludeModuleSettings = true, HomeFolder = null });

        Assert.StartsWith("### Summary\n\nChat stops updating after a raid.\n\n### Steps to reproduce\n\n1. Get raided\n2. Watch chat", body);
        Assert.Contains("### Expected behaviour\n\nChat keeps updating", body);
        Assert.Contains("### Actual behaviour\n\nChat froze", body);
        Assert.Contains("| Cuebot | 0.1.0 (0.1.0+abc123) |", body);
        Assert.Contains("| OS | Arch Linux (Linux 6.10) |", body);
        Assert.Contains("| Session | wayland (WAYLAND_DISPLAY set) |", body);
        Assert.Contains("| Display backend | Wayland |", body);
        Assert.Contains("<details><summary>Connections</summary>", body);
        Assert.Contains("- Twitch: Connected\n", body);   // status message (account name) left out by default
        Assert.Contains("<details><summary>App settings (app.json, secrets removed)</summary>", body);
        Assert.Contains("<details><summary>twitch.json (secrets removed)</summary>", body);
        Assert.Contains("<details><summary>Recent log (last 3 lines, secrets removed)</summary>", body);
        Assert.DoesNotContain("techdaddykb", body);
        Assert.DoesNotContain("abcdefabcdef", body);
        Assert.DoesNotContain("g5xk2m9q7w1r4t8y0u3i6o2p5a7s9d", body);
        Assert.EndsWith(ReportBuilder.Footer + "\n", body);

        var minimal = ReportBuilder.Build(new ReportInput(), Info(),
            new ReportOptions { IncludeAppSettings = false, IncludeLog = false, IncludeConnections = false });
        Assert.Contains("### Summary\n\n_No response_", minimal);
        Assert.DoesNotContain("<details>", minimal);
    }

    [Fact]
    public void Account_names_appear_only_when_ticked()
    {
        var body = ReportBuilder.Build(Input, Info(), new ReportOptions { IncludeAccountNames = true, IncludeModuleSettings = true });
        Assert.Contains("- Twitch: Connected — Connected as techdaddykb", body);
        Assert.Contains("\"login\": \"techdaddykb\"", body);
        Assert.DoesNotContain("g5xk2m9q7w1r4t8y0u3i6o2p5a7s9d", body);   // secrets still go
    }

    [Fact]
    public void Crash_section_and_default_title()
    {
        var crash = new CrashRecord
        {
            Time = new DateTimeOffset(2026, 10, 27, 12, 0, 0, TimeSpan.Zero),
            Source = "UI thread",
            Exception = ExceptionInfo.From(Throw()),
            Environment = Env,
            Log = ["12:00:00 [Info] Cuebot: Starting"],
        };
        var info = Info(crash: crash);
        Assert.Equal("Crash: InvalidOperationException: Boom password=[redacted]", ReportBuilder.DefaultTitle(info));
        var body = ReportBuilder.Build(Input, info, new ReportOptions());
        Assert.Contains("### Crash", body);
        Assert.Contains("System.InvalidOperationException: Boom password=[redacted]", body);
        Assert.Contains("<details><summary>Crash file (secrets removed)</summary>", body);
        Assert.DoesNotContain("hunter2", body);
        Assert.DoesNotContain("### Crash", ReportBuilder.Build(Input, info, new ReportOptions { IncludeCrash = false }));

        static Exception Throw()
        {
            try { throw new InvalidOperationException("Boom password=hunter2"); }
            catch (Exception ex) { return ex; }
        }
    }

    [Fact]
    public void Fences_grow_past_backticks_in_the_content()
    {
        Assert.Equal("```text\nplain\n```\n", ReportBuilder.Fence("plain", "text"));
        Assert.StartsWith("````json\n", ReportBuilder.Fence("a ``` b", "json"));
    }

    [Fact]
    public void Short_report_fits_the_url_directly()
    {
        var body = ReportBuilder.Build(Input, Info(), new ReportOptions());
        var link = IssueLink.Create(Input.Title, body, ReportBuilder.BuildShort(Input, Info(), new ReportOptions()));
        Assert.False(link.BodyTruncated);
        Assert.StartsWith("https://github.com/TechDaddyKB/cuebot/issues/new?title=Chat%20stops%20after%20a%20raid&labels=bug&body=", link.Url);
        Assert.Equal(body, Uri.UnescapeDataString(link.Url[(link.Url.IndexOf("&body=", StringComparison.Ordinal) + 6)..]));
    }

    [Fact]
    public void Long_report_uses_the_short_body_with_the_paste_marker()
    {
        var info = Info(logLines: 200);
        var body = ReportBuilder.Build(Input, info, new ReportOptions());
        Assert.True(IssueLink.BuildUrl(Input.Title, body).Length > IssueLink.MaxUrlLength);
        var link = IssueLink.Create(Input.Title, body, ReportBuilder.BuildShort(Input, info, new ReportOptions()));
        Assert.True(link.BodyTruncated);
        Assert.True(link.Url.Length <= IssueLink.MaxUrlLength);
        var sent = Uri.UnescapeDataString(link.Url[(link.Url.IndexOf("&body=", StringComparison.Ordinal) + 6)..]);
        Assert.Contains(ReportBuilder.PasteMarker, sent);
        Assert.Contains("Chat stops updating after a raid.", sent);
        Assert.DoesNotContain("Recent log", sent);
    }

    [Fact]
    public void Even_a_huge_summary_is_cut_to_fit_and_keeps_the_marker()
    {
        var huge = Input with { Summary = string.Concat(Enumerable.Repeat("é very long text ", 2000)) };
        var info = Info();
        var link = IssueLink.Create(new string('t', 500), ReportBuilder.Build(huge, info, new ReportOptions()), ReportBuilder.BuildShort(huge, info, new ReportOptions()));
        Assert.True(link.BodyTruncated);
        Assert.True(link.Url.Length <= IssueLink.MaxUrlLength);
        var sent = Uri.UnescapeDataString(link.Url[(link.Url.IndexOf("&body=", StringComparison.Ordinal) + 6)..]);
        Assert.EndsWith(ReportBuilder.PasteMarker + "\n", sent);
        var title = Uri.UnescapeDataString(link.Url.Split("title=")[1].Split('&')[0]);
        Assert.Equal(IssueLink.MaxTitleLength, title.Length);
    }

    [Fact]
    public void Gh_command_uses_the_repo_constant_and_quotes_for_display()
    {
        Assert.Equal(["issue", "create", "--repo", "TechDaddyKB/cuebot", "--title", "It broke", "--body-file", "/tmp/r.md"],
            GhCli.CreateIssueArguments("It broke", "/tmp/r.md"));
        Assert.Equal("gh issue create --repo TechDaddyKB/cuebot --title \"It \\\"broke\\\"\" --body-file /tmp/r.md",
            GhCli.DisplayCommand("It \"broke\"", "/tmp/r.md"));
        Assert.Equal("/usr/bin/gh", GhCli.FindOnPath("/usr/local/bin:/usr/bin", windows: false, p => p.Replace('\\', '/') == "/usr/bin/gh")?.Replace('\\', '/'));
        Assert.Null(GhCli.FindOnPath("/usr/bin", windows: false, _ => false));
    }
}
