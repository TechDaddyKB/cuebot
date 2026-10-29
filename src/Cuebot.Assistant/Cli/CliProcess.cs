using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Cuebot.Assistant.Cli;

/// <summary>Finds an AI CLI: the configured command, then PATH, then the folders installers commonly use.</summary>
public static class CliLocator
{
    /// <summary>
    /// The full path of <paramref name="name"/>, or null. A desktop-launched Cuebot often has a shorter PATH than a terminal,
    /// so npm, bun, pipx, mise and Homebrew locations are searched too.
    /// </summary>
    public static string? Find(string name, string configured = "", IEnumerable<string>? extraDirectories = null)
    {
        if (configured.Trim() is { Length: > 0 } command)
            return File.Exists(command) ? Path.GetFullPath(command) : Find(command, "", extraDirectories);

        var windows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        string[] names = windows ? [name + ".exe", name + ".cmd", name + ".bat", name] : [name];
        foreach (var directory in Directories(extraDirectories))
        {
            foreach (var candidate in names)
            {
                var path = Path.Combine(directory, candidate);
                if (File.Exists(path)) return path;
            }
        }
        return null;
    }

    private static IEnumerable<string> Directories(IEnumerable<string>? extra)
    {
        foreach (var d in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            yield return d;
        foreach (var d in extra ?? []) yield return d;
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(appData, "npm");
            yield return Path.Combine(home, ".local", "bin");
            yield return Path.Combine(home, ".bun", "bin");
            yield return Path.Combine(local, "Programs");
            yield break;
        }
        yield return Path.Combine(home, ".local", "bin");
        yield return Path.Combine(home, ".local", "share", "mise", "shims");
        yield return Path.Combine(home, ".npm-global", "bin");
        yield return Path.Combine(home, ".bun", "bin");
        yield return Path.Combine(home, ".cargo", "bin");
        yield return Path.Combine(home, ".volta", "bin");
        yield return "/opt/homebrew/bin";
        yield return "/usr/local/bin";
        yield return "/usr/bin";
    }
}

/// <summary>
/// A running AI CLI with its standard input and output as streams. The MCP token goes in the environment (never on the
/// command line, which other users can read); arguments and environment are never logged. Disposing kills the process
/// tree.
/// </summary>
public sealed class CliProcess : IAsyncDisposable
{
    private const int StderrKeep = 4000;
    private readonly Process _process;
    private readonly StringBuilder _stderr = new();

    private CliProcess(Process process)
    {
        _process = process;
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (_stderr)
            {
                _stderr.AppendLine(e.Data);
                if (_stderr.Length > StderrKeep) _stderr.Remove(0, _stderr.Length - StderrKeep);
            }
        };
        process.BeginErrorReadLine();
    }

    public Stream Output => _process.StandardOutput.BaseStream;
    public Stream Input => _process.StandardInput.BaseStream;
    public bool HasExited => _process.HasExited;

    /// <summary>The last lines the CLI wrote to standard error (for error messages).</summary>
    public string ErrorTail
    {
        get { lock (_stderr) return _stderr.ToString().Trim(); }
    }

    public static CliProcess Start(string path, IEnumerable<string> arguments, string workingDirectory, IReadOnlyDictionary<string, string>? environment = null)
    {
        var info = new ProcessStartInfo
        {
            FileName = path,
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        // npm installs a .cmd shim on Windows; it has to run through cmd.exe.
        if (path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
        {
            info.FileName = Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe";
            info.ArgumentList.Add("/d");
            info.ArgumentList.Add("/s");
            info.ArgumentList.Add("/c");
            info.ArgumentList.Add(path);
        }
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        foreach (var (key, value) in environment ?? new Dictionary<string, string>()) info.Environment[key] = value;
        // The CLI may live next to its runtime (node, bun) in a folder a desktop-launched Cuebot lacks on PATH.
        var folder = Path.GetDirectoryName(path);
        if (folder is { Length: > 0 }) info.Environment["PATH"] = folder + Path.PathSeparator + (info.Environment.TryGetValue("PATH", out var p) ? p : "");
        var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {Path.GetFileName(path)}.");
        return new CliProcess(process);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (!_process.HasExited)
            {
                try
                {
                    _process.StandardInput.Close();
                }
                catch (IOException)
                {
                }
                using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try
                {
                    await _process.WaitForExitAsync(wait.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    _process.Kill(entireProcessTree: true);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // never started or already gone
        }
        _process.Dispose();
    }
}
