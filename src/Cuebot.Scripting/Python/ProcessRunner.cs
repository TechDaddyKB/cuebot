using System.Diagnostics;
using System.Text;

namespace Cuebot.Scripting;

/// <summary>Result of a finished child process.</summary>
internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    /// <summary>The last non-empty line of stderr (or stdout), for short error messages.</summary>
    public string LastErrorLine =>
        (StandardError + "\n" + StandardOutput).Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
}

/// <summary>Runs a short-lived Python process (pip, venv, version probes) and streams its output.</summary>
internal static class ProcessRunner
{
    public static ProcessStartInfo CreateStartInfo(string executable, IEnumerable<string> arguments, string workingDirectory)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        info.Environment["PYTHONIOENCODING"] = "utf-8";
        info.Environment["PYTHONUTF8"] = "1";
        info.Environment["PIP_DISABLE_PIP_VERSION_CHECK"] = "1";
        info.Environment["PIP_NO_INPUT"] = "1";
        return info;
    }

    /// <summary>Runs to completion; each output line is reported to <paramref name="progress"/>. Kills it on cancellation.</summary>
    public static async Task<ProcessResult> RunAsync(string executable, IEnumerable<string> arguments, string workingDirectory,
        IProgress<string>? progress, CancellationToken ct)
    {
        using var process = Process.Start(CreateStartInfo(executable, arguments, workingDirectory))
            ?? throw new InvalidOperationException($"Could not start {executable}");
        process.StandardInput.Close();
        await using var kill = ct.Register(() => KillQuietly(process));

        var output = PumpAsync(process.StandardOutput, progress);
        var error = PumpAsync(process.StandardError, progress);
        await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        var result = new ProcessResult(process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
        ct.ThrowIfCancellationRequested();
        return result;
    }

    public static void KillQuietly(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited.
        }
    }

    private static async Task<string> PumpAsync(StreamReader reader, IProgress<string>? progress)
    {
        var text = new StringBuilder();
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            text.AppendLine(line);
            if (line.Length > 0) progress?.Report(line);
        }
        return text.ToString();
    }
}
