using System.Diagnostics;
using System.Text;

namespace Cuebot.App.Diagnostics;

public sealed record ProcessResult(int ExitCode, string Output, string Error);

/// <summary>Runs a program without a shell (arguments are passed as a list, never parsed).</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default);
}

public sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(string fileName, IReadOnlyList<string> arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        var info = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var a in arguments) info.ArgumentList.Add(a);
        info.Environment["GH_PROMPT_DISABLED"] = "1";   // never wait for input
        info.Environment["NO_COLOR"] = "1";
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"Could not start {fileName}");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var error = process.StandardError.ReadToEndAsync(ct);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            return new ProcessResult(-1, "", $"{Path.GetFileName(fileName)} did not finish within {timeout.TotalSeconds:0} seconds.");
        }
        return new ProcessResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}

/// <summary>
/// The optional GitHub CLI path: only used when <c>gh</c> is installed and signed in, and only after the user has seen
/// the exact command and clicked to run it.
/// </summary>
public sealed class GhCli(IProcessRunner runner, Func<string?> findGh)
{
    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CreateTimeout = TimeSpan.FromSeconds(60);

    public GhCli() : this(new ProcessRunner(), () => FindOnPath(Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows(), File.Exists)) { }

    /// <summary>Path of gh when it is installed and <c>gh auth status</c> succeeds; null otherwise. Never throws.</summary>
    public async Task<string?> FindReadyAsync(CancellationToken ct = default)
    {
        try
        {
            if (findGh() is not { } gh) return null;
            var status = await runner.RunAsync(gh, ["auth", "status", "--hostname", "github.com"], StatusTimeout, ct).ConfigureAwait(false);
            return status.ExitCode == 0 ? gh : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public static IReadOnlyList<string> CreateIssueArguments(string title, string bodyFile) =>
        ["issue", "create", "--repo", SupportLinks.RepoSlug, "--title", title, "--body-file", bodyFile];

    /// <summary>The command as a user would type it (for display only; it is never run through a shell).</summary>
    public static string DisplayCommand(string title, string bodyFile) =>
        "gh " + string.Join(' ', CreateIssueArguments(title, bodyFile).Select(Quote));

    public Task<ProcessResult> CreateIssueAsync(string gh, string title, string bodyFile, CancellationToken ct = default) =>
        runner.RunAsync(gh, CreateIssueArguments(title, bodyFile), CreateTimeout, ct);

    private static string Quote(string arg) =>
        arg.Length > 0 && arg.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '/' or '.' or ':' or '\\')
            ? arg
            : "\"" + arg.Replace("\\\"", "\\\\\"").Replace("\"", "\\\"") + "\"";

    public static string? FindOnPath(string? path, bool windows, Func<string, bool> exists)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var names = windows ? new[] { "gh.exe" } : ["gh"];
        foreach (var dir in path.Split(windows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir.Trim('"'), name);
                if (exists(candidate)) return candidate;
            }
        return null;
    }
}
