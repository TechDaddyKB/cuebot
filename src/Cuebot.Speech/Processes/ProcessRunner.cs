using System.Diagnostics;
using System.Text;

namespace Cuebot.Speech;

/// <summary>Result of a finished child process.</summary>
internal sealed record ProcessResult(int ExitCode, byte[] Output, string Error)
{
    /// <summary>Throws when the process failed, using its error output as the message.</summary>
    public ProcessResult EnsureSuccess(string program)
    {
        if (ExitCode == 0) return this;
        var detail = string.IsNullOrWhiteSpace(Error) ? $"exit code {ExitCode}" : Error.Trim();
        throw new InvalidOperationException($"{program} failed: {detail}");
    }
}

/// <summary>Runs a child process with optional standard input, capturing its output. Kills it on cancellation.</summary>
internal static class ProcessRunner
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, string? input,
        CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = input is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Utf8,
        };
        if (input is not null) startInfo.StandardInputEncoding = Utf8;
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo) ?? throw new InvalidOperationException($"Could not start {fileName}");
        try
        {
            using var output = new MemoryStream();
            var readOutput = process.StandardOutput.BaseStream.CopyToAsync(output, ct);
            var readError = process.StandardError.ReadToEndAsync(ct);
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), ct).ConfigureAwait(false);
                process.StandardInput.Close();
            }
            await Task.WhenAll(readOutput, readError).ConfigureAwait(false);
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, output.ToArray(), await readError.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // The process exited between the check and the kill.
        }
    }
}
