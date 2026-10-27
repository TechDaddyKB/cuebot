namespace Cuebot.Scripting;

/// <summary>
/// File locations of the managed Python runtime under &lt;data&gt;/python: the extracted interpreter ("base"), the
/// virtual environment scripts run in ("env") and the worker script.
/// </summary>
internal sealed class PythonLayout(string root)
{
    public string Root { get; } = root;

    /// <summary>Extraction target of the python-build-standalone archive (which contains a "python" folder).</summary>
    public string BaseDirectory => Path.Combine(Root, "base");

    public string EnvironmentDirectory => Path.Combine(Root, "env");

    public string WorkerScript => Path.Combine(Root, "cuebot_worker.py");

    public string BaseExecutable => OperatingSystem.IsWindows()
        ? Path.Combine(BaseDirectory, "python", "python.exe")
        : Path.Combine(BaseDirectory, "python", "bin", "python3");

    public string EnvironmentExecutable => OperatingSystem.IsWindows()
        ? Path.Combine(EnvironmentDirectory, "Scripts", "python.exe")
        : Path.Combine(EnvironmentDirectory, "bin", "python");

    /// <summary>Reads the Python version recorded in the environment's pyvenv.cfg, without starting Python.</summary>
    public string? ReadEnvironmentVersion()
    {
        var config = Path.Combine(EnvironmentDirectory, "pyvenv.cfg");
        if (!File.Exists(config)) return null;
        foreach (var line in File.ReadLines(config))
        {
            var separator = line.IndexOf('=');
            if (separator < 0) continue;
            var key = line[..separator].Trim();
            if (key is "version" or "version_info")
                return string.Join('.', line[(separator + 1)..].Trim().Split('.').Take(3));
        }
        return null;
    }
}
