using System.Security.Cryptography;

namespace Cuebot.Scripting;

/// <summary>
/// Downloads the pinned python-build-standalone CPython build (<see cref="PinnedPython"/>) from GitHub, checks its
/// SHA-256, extracts it, then creates the virtual environment scripts run in.
/// </summary>
internal sealed class PythonInstaller(HttpClient http, PythonLayout layout)
{
    private const string UserAgent = "Cuebot";

    public async Task InstallAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var build = PinnedPython.ForThisComputer()
            ?? throw new PlatformNotSupportedException("The managed Python runtime is not available for this platform; set a custom Python path instead");

        Directory.CreateDirectory(layout.Root);
        var archive = Path.Combine(layout.Root, build.FileName);
        try
        {
            await DownloadAsync(build, archive, progress, ct).ConfigureAwait(false);
            await VerifyChecksumAsync(build, archive, ct).ConfigureAwait(false);
            progress?.Report("Checksum verified");

            progress?.Report($"Extracting Python {PinnedPython.Version}...");
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

    private async Task DownloadAsync(PythonBuild build, string path, IProgress<string>? progress, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, build.DownloadUrl);
        request.Headers.UserAgent.ParseAdd(UserAgent);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength ?? build.Size;

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
            var percent = total > 0 ? received * 100 / total : -1;
            if (percent / 10 != lastReportedPercent / 10)
            {
                lastReportedPercent = percent;
                progress?.Report(percent >= 0
                    ? $"Downloading Python {PinnedPython.Version} ({build.Megabytes} MB): {Math.Min(percent, 100)}%"
                    : $"Downloading Python {PinnedPython.Version}...");
            }
        }
    }

    /// <summary>Refuses an archive whose SHA-256 differs from the one compiled into Cuebot.</summary>
    internal static async Task VerifyChecksumAsync(PythonBuild build, string path, CancellationToken ct)
    {
        await using var file = File.OpenRead(path);
        var actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct).ConfigureAwait(false));
        if (!string.Equals(actual, build.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"The Python download does not match the expected checksum ({build.FileName}); it was not installed");
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
