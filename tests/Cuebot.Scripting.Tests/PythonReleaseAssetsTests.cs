using System.Text.Json;

namespace Cuebot.Scripting.Tests;

public sealed class PythonReleaseAssetsTests
{
    private static readonly string[] SampleNames =
    [
        "SHA256SUMS",
        "cpython-3.10.16+20241206-x86_64-pc-windows-msvc-install_only.tar.gz",
        "cpython-3.12.8+20241206-x86_64-pc-windows-msvc-install_only.tar.gz",
        "cpython-3.13.0+20241206-x86_64-pc-windows-msvc-install_only.tar.gz",
        "cpython-3.13.1+20241206-x86_64-pc-windows-msvc-install_only.tar.gz",
        "cpython-3.13.1+20241206-x86_64-pc-windows-msvc-install_only_stripped.tar.gz",
        "cpython-3.13.1+20241206-x86_64-pc-windows-msvc-pgo-full.tar.zst",
        "cpython-3.13.1+20241206-x86_64-pc-windows-msvc-freethreaded+pgo-full.tar.zst",
        "cpython-3.14.0a2+20241206-x86_64-pc-windows-msvc-install_only.tar.gz",
        "cpython-3.14.2+20241206-x86_64-pc-windows-msvc-install_only.tar.gz",
        "cpython-3.13.1+20241206-x86_64_v3-unknown-linux-gnu-install_only.tar.gz",
        "cpython-3.13.1+20241206-x86_64-unknown-linux-gnu-install_only.tar.gz",
        "cpython-3.13.1+20241206-x86_64-unknown-linux-musl-install_only.tar.gz",
        "cpython-3.13.1+20241206-aarch64-unknown-linux-gnu-install_only.tar.gz",
    ];

    private static IEnumerable<ReleaseAsset> Assets(IEnumerable<string> names) =>
        names.Select(n => new ReleaseAsset(n, $"https://example.invalid/{n}"));

    [Theory]
    [InlineData("x86_64-pc-windows-msvc", "cpython-3.13.1+20241206-x86_64-pc-windows-msvc-install_only.tar.gz")]
    [InlineData("x86_64-unknown-linux-gnu", "cpython-3.13.1+20241206-x86_64-unknown-linux-gnu-install_only.tar.gz")]
    [InlineData("aarch64-unknown-linux-gnu", "cpython-3.13.1+20241206-aarch64-unknown-linux-gnu-install_only.tar.gz")]
    public void Selects_the_newest_preferred_install_only_archive(string target, string expected)
    {
        var build = PythonReleaseAssets.Select(Assets(SampleNames), target);

        Assert.NotNull(build);
        Assert.Equal(expected, build.Asset.Name);
        Assert.Equal(new Version(3, 13, 1), build.PythonVersion);
    }

    [Fact]
    public void Falls_back_to_python_3_12()
    {
        var build = PythonReleaseAssets.Select(Assets(SampleNames.Where(n => !n.Contains("3.13"))), "x86_64-pc-windows-msvc");

        Assert.Equal("cpython-3.12.8+20241206-x86_64-pc-windows-msvc-install_only.tar.gz", build?.Asset.Name);
    }

    [Fact]
    public void Returns_null_without_a_matching_build() =>
        Assert.Null(PythonReleaseAssets.Select(Assets(SampleNames), "x86_64-apple-darwin"));

    [Fact]
    public void Parses_github_release_assets()
    {
        using var json = JsonDocument.Parse("""
            {"tag_name":"20241206","assets":[
              {"name":"SHA256SUMS","browser_download_url":"https://example.invalid/SHA256SUMS"},
              {"name":"no-url"}
            ]}
            """);

        var assets = PythonReleaseAssets.ParseRelease(json.RootElement);

        Assert.Equal([new ReleaseAsset("SHA256SUMS", "https://example.invalid/SHA256SUMS")], assets);
    }

    [Fact]
    public void Finds_checksums_by_file_name()
    {
        const string sums = "ABCDEF01  cpython-a.tar.gz\n0123abcd *cpython-b.tar.gz\n";

        Assert.Equal("abcdef01", PythonReleaseAssets.FindChecksum(sums, "cpython-a.tar.gz"));
        Assert.Equal("0123abcd", PythonReleaseAssets.FindChecksum(sums, "cpython-b.tar.gz"));
        Assert.Null(PythonReleaseAssets.FindChecksum(sums, "cpython-c.tar.gz"));
    }
}
