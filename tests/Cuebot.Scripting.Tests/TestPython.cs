using System.Diagnostics;

namespace Cuebot.Scripting.Tests;

/// <summary>
/// Finds a real Python 3 interpreter for integration tests: CUEBOT_TEST_PYTHON, then python3/python on PATH.
/// Candidates are verified by running them (on Windows "python" may be the Microsoft Store stub).
/// </summary>
internal static class TestPython
{
    private static readonly Lazy<string?> Found = new(Find);

    public static string? Executable => Found.Value;

    private static string? Find()
    {
        string?[] candidates = [Environment.GetEnvironmentVariable("CUEBOT_TEST_PYTHON"), "python3", "python"];
        return candidates.Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => Probe(c!)).FirstOrDefault(p => p is not null);
    }

    /// <summary>Returns the interpreter's full path when it is a working Python 3.</summary>
    private static string? Probe(string command)
    {
        try
        {
            var info = new ProcessStartInfo(command) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("-c");
            info.ArgumentList.Add("import sys; print(sys.version_info[0]); print(sys.executable)");
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd().Split('\n', StringSplitOptions.TrimEntries);
            if (!process.WaitForExit(10_000) || process.ExitCode != 0) return null;
            return output is ["3", var path, ..] && File.Exists(path) ? path : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}

/// <summary>A fact that is skipped when no Python interpreter is available.</summary>
internal sealed class PythonFactAttribute : FactAttribute
{
    public PythonFactAttribute()
    {
        if (TestPython.Executable is null) Skip = "No Python 3 interpreter found (set CUEBOT_TEST_PYTHON or put python3 on PATH)";
    }
}
