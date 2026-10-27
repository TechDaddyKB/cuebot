namespace Cuebot.App.Shell;

/// <summary>
/// Command-line switches. Switches start with <c>--</c> (or <c>/</c>, Streamer.bot style, on any platform) and
/// are case-insensitive; values follow as <c>--data=path</c> or <c>--data path</c>. Unknown arguments are kept in
/// <see cref="Unknown"/> and otherwise ignored, so the display backend switch and Avalonia's own arguments pass through.
/// </summary>
public sealed record StartupOptions
{
    /// <summary>No window: start every service, log to the console and the log file, stop on Ctrl+C / SIGTERM.</summary>
    public bool Headless { get; init; }

    /// <summary>Start hidden in the tray (or minimized when there is no tray).</summary>
    public bool Minimized { get; init; }

    /// <summary>Forget the saved window size and position.</summary>
    public bool ResetWindow { get; init; }

    /// <summary>Data folder; overrides <c>CUEBOT_DATA</c>.</summary>
    public string? DataFolder { get; init; }

    /// <summary><c>--backend=wayland|x11</c> (Linux only; parsed by <see cref="Platform.LinuxBackend"/>).</summary>
    public string? Backend { get; init; }

    public bool ShowHelp { get; init; }

    public IReadOnlyList<string> Unknown { get; init; } = [];

    public IReadOnlyList<string> Errors { get; init; } = [];

    public const string HelpText =
        """
        Usage: cuebot [options]

          --headless           Run without a window: all services start, logs go to the console and the
                               log file; Ctrl+C or SIGTERM shuts down cleanly.
          --minimized          Start hidden in the tray (minimized when no tray is available).
          --data <folder>      Use this data folder (overrides CUEBOT_DATA).
          --reset-window       Forget the saved window size and position.
          --backend=<name>     Linux display backend: wayland or x11 (default: Wayland when available).
          --help               Show this help.
        """;

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        var options = new StartupOptions();
        var unknown = new List<string>();
        var errors = new List<string>();
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (!TrySplit(arg, out var name, out var value))
            {
                unknown.Add(arg);
                continue;
            }

            switch (name)
            {
                case "headless": options = options with { Headless = true }; break;
                case "minimized" or "minimised" or "tray": options = options with { Minimized = true }; break;
                case "reset-window" or "resetwindow": options = options with { ResetWindow = true }; break;
                case "help" or "h" or "?": options = options with { ShowHelp = true }; break;
                case "backend": options = options with { Backend = value ?? Next(args, ref i) }; break;
                case "data":
                    var folder = value ?? Next(args, ref i);
                    if (string.IsNullOrWhiteSpace(folder)) errors.Add("--data needs a folder");
                    else options = options with { DataFolder = Path.GetFullPath(folder) };
                    break;
                default: unknown.Add(arg); break;
            }
        }
        return options with { Unknown = unknown, Errors = errors };
    }

    /// <summary>The data folder to use: <c>--data</c>, then <c>CUEBOT_DATA</c>, then the platform default (null).</summary>
    public string? ResolveDataFolder(Func<string, string?> env) =>
        DataFolder ?? (string.IsNullOrWhiteSpace(env("CUEBOT_DATA")) ? null : Path.GetFullPath(env("CUEBOT_DATA")!));

    private static string? Next(IReadOnlyList<string> args, ref int i)
    {
        if (i + 1 >= args.Count || args[i + 1].StartsWith("--", StringComparison.Ordinal)) return null;
        return args[++i];
    }
    private static bool TrySplit(string arg, out string name, out string? value)
    {
        name = "";
        value = null;
        string body;
        if (arg.StartsWith("--", StringComparison.Ordinal)) body = arg[2..];
        else if (arg.Length > 1 && arg[0] == '/' && !arg[1..].Contains('/')) body = arg[1..];   // "/headless", not "/home/x"
        else if (arg is "-h" or "-?") body = "help";
        else return false;

        var eq = body.IndexOf('=');
        if (eq >= 0)
        {
            value = body[(eq + 1)..];
            body = body[..eq];
        }
        name = body.ToLowerInvariant();
        return name.Length > 0;
    }
}
