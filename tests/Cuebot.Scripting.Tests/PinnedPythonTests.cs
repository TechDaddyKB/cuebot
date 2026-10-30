using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace Cuebot.Scripting.Tests;

public sealed class PinnedPythonTests : IDisposable
{
    private readonly TempDirectory _dir = new();

    [Fact]
    public void Every_supported_platform_has_one_pinned_build_of_the_pinned_version()
    {
        string[] targets = ["x86_64-pc-windows-msvc", "aarch64-pc-windows-msvc", "x86_64-unknown-linux-gnu", "aarch64-unknown-linux-gnu"];

        Assert.Equal(targets.Order(), PinnedPython.All.Select(b => b.Target).Order());
        Assert.All(PinnedPython.All, b =>
        {
            Assert.Equal($"cpython-{PinnedPython.Version}+{PinnedPython.Release}-{b.Target}-install_only_stripped.tar.gz", b.FileName);
            Assert.Matches(new Regex("^[0-9a-f]{64}$"), b.Sha256);
            Assert.InRange(b.Size, 10_000_000, 100_000_000);
        });
        Assert.Equal(PinnedPython.All.Count, PinnedPython.All.Select(b => b.Sha256).Distinct().Count());
    }

    [Fact]
    public void Download_url_points_at_the_pinned_release_with_the_plus_escaped()
    {
        var build = PinnedPython.For("x86_64-unknown-linux-gnu")!;

        Assert.Equal(
            $"https://github.com/astral-sh/python-build-standalone/releases/download/{PinnedPython.Release}/cpython-{PinnedPython.Version}%2B{PinnedPython.Release}-x86_64-unknown-linux-gnu-install_only_stripped.tar.gz",
            build.DownloadUrl);
    }

    [Fact]
    public void This_computer_has_a_managed_build()
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux()) return;
        Assert.NotNull(PinnedPython.ForThisComputer());
    }

    [Fact]
    public async Task Archive_must_match_the_compiled_in_checksum()
    {
        var path = Path.Combine(_dir.Path, "python.tar.gz");
        await File.WriteAllTextAsync(path, "not really python");
        var hash = Convert.ToHexStringLower(SHA256.HashData(await File.ReadAllBytesAsync(path)));

        await PythonInstaller.VerifyChecksumAsync(new PythonBuild("t", "python.tar.gz", hash, 17), path, CancellationToken.None);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PythonInstaller.VerifyChecksumAsync(new PythonBuild("t", "python.tar.gz", new string('0', 64), 17), path, CancellationToken.None));
    }

    public void Dispose() => _dir.Dispose();
}
