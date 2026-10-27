using System.Security.Cryptography;
using System.Text.Json;

namespace Cuebot.Scripting;

/// <summary>
/// Downloads a python-build-standalone CPython build from GitHub, verifies and extracts it, then creates the
/// virtual environment scripts run in.
/// </summary>
internal sealed class PythonInstaller(HttpClient http, PythonLayout layout)
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/astral-sh/python-build-standalone/releases/latest";
    private const string UserAgent = "Cuebot";

    public async Task InstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var target = PythonReleaseAssets.CurrentTarget()
            ?? throw new PlatformNotSupportedException("The managed Python runtime is not available for this platform; set a custom Python path instead");

        progress?.Report("Looking up the latest Python build...");
        var assets = await GetLatestReleaseAssetsAsync(ct).ConfigureAwait(false);
        var build = PythonReleaseAssets.Select(assets, target)
            ?? throw new InvalidOperationException($"The latest python-build-standalone release has no CPython 3.12/3.13 build for {target}");

        Directory.CreateDirectory(layout.Root);
        var archive = Path.Combine(layout.Root, build.Asset.Name);
        try
        {
            await DownloadAsync(build.Asset, archive, progress, ct).ConfigureAwait(false);
            await VerifyChecksumAsync(assets, build.Asset, archive, progress, ct).ConfigureAwait(false);

            progress?.Report($"Extracting Python {build.PythonVersion}...");
            DeleteDirectory(layout.EnvironmentDirectory);
            DeleteDirectory(layout.BaseDirectory);
            await Task.Run(() => TarGzExtractor.Extract(archive, layout.BaseDirectory), ct).ConfigureAwait(false);

            progress?.Report("Creating the Python environment...");
            var venv = await ProcessRunner.RunAsync(layout.BaseExecutable, ["-m", "venv", layout.EnvironmentDirectory],
                layout.Root, progress, ct).ConfigureAwait(false);
            if (venv.ExitCode != 0) throw new InvalidOperationException($"Creating the Python environment failed: {venv.LastErrorLine}");
        }
        finally
        {
            File.Delete(archive);
        }
    }

    private async Task<IReadOnlyList<ReleaseAsset>> GetLatestReleaseAssetsAsync(CancellationToken ct)
    {
        using var request = CreateRequest(LatestReleaseUrl);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        return PythonReleaseAssets.ParseRelease(document.RootElement);
    }

    private async Task DownloadAsync(ReleaseAsset asset, string path, IProgress<string>? progress, CancellationToken ct)
    {
        using var request = CreateRequest(asset.DownloadUrl);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = File.Create(path);
        var buffer = new byte[81920];
        long received = 0;
        var lastReportedPercent = -1L;
        int read;
        while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            received += read;
            var percent = total is > 0 ? received * 100 / total.Value : -1;
            if (percent / 10 != lastReportedPercent / 10)
            {
                lastReportedPercent = percent;
                progress?.Report(percent >= 0 ? $"Downloading {asset.Name}: {percent}%" : $"Downloading {asset.Name}...");
            }
        }
    }

    private async Task VerifyChecksumAsync(IReadOnlyList<ReleaseAsset> assets, ReleaseAsset archive, string path,
        IProgress<string>? progress, CancellationToken ct)
    {
        var sums = assets.FirstOrDefault(a => a.Name == PythonReleaseAssets.ChecksumAssetName);
        if (sums is null)
        {
            progress?.Report("The release publishes no checksums; skipping verification");
            return;
        }

        using var request = CreateRequest(sums.DownloadUrl);
        using var response = await http.SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var expected = PythonReleaseAssets.FindChecksum(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false), archive.Name)
            ?? throw new InvalidDataException($"{PythonReleaseAssets.ChecksumAssetName} has no entry for {archive.Name}");

        await using var file = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
        if (actual != expected) throw new InvalidDataException($"Checksum mismatch for {archive.Name}; the download may be corrupt");
        progress?.Report("Checksum verified");
    }

    private static HttpRequestMessage CreateRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        return request;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
