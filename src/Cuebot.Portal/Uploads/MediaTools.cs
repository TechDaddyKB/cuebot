using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Cuebot.Portal.Uploads;

/// <summary>Why a tool run was stopped before it finished.</summary>
public enum ToolStop { None, Timeout, Memory, Cancelled }

public sealed record ToolResult(int ExitCode, string Output, string Errors, ToolStop Stopped)
{
    public bool Ok => Stopped == ToolStop.None && ExitCode == 0;
}

/// <summary>
/// Finds ffmpeg and ffprobe and runs them on one upload at a time, boxed in: a fresh working folder, no inherited
/// environment, low priority, a time limit and a memory limit (polled everywhere; on Linux also a hard address-space
/// limit). Cuebot's own build (build/ffmpeg/build.sh) ships next to the app; a system ffmpeg on the PATH works too (the
/// Arch package, development), and the forced demuxers and file-only protocol keep either to the one file.
/// </summary>
public sealed class MediaTools(string ffmpeg, string ffprobe, bool bundled)
{
    public string Ffmpeg { get; } = ffmpeg;
    public string Ffprobe { get; } = ffprobe;

    /// <summary>Cuebot's own minimal build rather than a system ffmpeg.</summary>
    public bool Bundled { get; } = bundled;

    private const int MaxOutputChars = 1 << 20;

    /// <summary>CUEBOT_FFMPEG_DIR, then the ffmpeg folder next to the app, then the PATH; null when there is none.</summary>
    public static MediaTools? Find()
    {
        var exe = OperatingSystem.IsWindows() ? ".exe" : "";
        foreach (var dir in new[] { Environment.GetEnvironmentVariable("CUEBOT_FFMPEG_DIR"), Path.Combine(AppContext.BaseDirectory, "ffmpeg") })
        {
            if (string.IsNullOrEmpty(dir)) continue;
            var (f, p) = (Path.Combine(dir, "ffmpeg" + exe), Path.Combine(dir, "ffprobe" + exe));
            if (File.Exists(f) && File.Exists(p)) return new MediaTools(f, p, bundled: true);
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var (f, p) = (Path.Combine(dir, "ffmpeg" + exe), Path.Combine(dir, "ffprobe" + exe));
            if (File.Exists(f) && File.Exists(p)) return new MediaTools(f, p, bundled: false);
        }
        return null;
    }

    public async Task<ToolResult> RunAsync(string tool, IReadOnlyList<string> args, string workDir, TimeSpan timeout, long maxMemory,
        CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            WorkingDirectory = workDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (OperatingSystem.IsLinux() && File.Exists("/bin/sh"))
        {
            // A hard address-space limit the tool cannot raise. "$0" "$@" pass the tool and its arguments untouched.
            start.FileName = "/bin/sh";
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"ulimit -v {maxMemory * 2 / 1024} 2>/dev/null; exec \"$0\" \"$@\"");
            start.ArgumentList.Add(tool);
        }
        else start.FileName = tool;
        foreach (var a in args) start.ArgumentList.Add(a);

        // Nothing from Cuebot's environment (FFREPORT, AV_LOG_FORCE_*, proxies...). Windows needs its system folders.
        var keep = OperatingSystem.IsWindows() ? new[] { "SystemRoot", "windir", "TEMP", "TMP" } : [];
        var kept = keep.Select(k => (k, Environment.GetEnvironmentVariable(k))).ToList();
        start.Environment.Clear();
        foreach (var (k, v) in kept) if (v is not null) start.Environment[k] = v;
        start.Environment["TMPDIR"] = workDir;

        using var process = new Process { StartInfo = start };
        process.Start();
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { /* not allowed here: run at normal priority */ }
        process.StandardInput.Close();
        var output = ReadAsync(process.StandardOutput);
        var errors = ReadAsync(process.StandardError);

        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(timeout);
        var stopped = ToolStop.None;
        try
        {
            while (!process.HasExited)
            {
                var exit = process.WaitForExitAsync(limit.Token);
                if (await Task.WhenAny(exit, Task.Delay(200, limit.Token)).ConfigureAwait(false) == exit) { await exit.ConfigureAwait(false); break; }
                process.Refresh();
                if (!process.HasExited && process.WorkingSet64 > maxMemory) { stopped = ToolStop.Memory; break; }
            }
        }
        catch (OperationCanceledException)
        {
            stopped = ct.IsCancellationRequested ? ToolStop.Cancelled : ToolStop.Timeout;
        }
        if (stopped != ToolStop.None)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        return new ToolResult(stopped == ToolStop.None ? process.ExitCode : -1, await output.ConfigureAwait(false),
            await errors.ConfigureAwait(false), stopped);
    }

    private static async Task<string> ReadAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[8192];
        int n;
        while ((n = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            if (text.Length < MaxOutputChars) text.Append(buffer, 0, Math.Min(n, MaxOutputChars - text.Length));
        return text.ToString();
    }

    /// <summary>For logs: "ffmpeg (Cuebot's build, X64)".</summary>
    public override string ToString() => $"{Path.GetFileName(Ffmpeg)} ({(Bundled ? "Cuebot's build" : "system")}, {RuntimeInformation.ProcessArchitecture})";
}
