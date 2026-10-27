namespace Cuebot.LiveTests;

/// <summary>
/// Opt-in configuration for tests against real Twitch and OBS. Everything is off unless the environment
/// variables below are set; see docs/LIVE_TESTING.md.
/// </summary>
internal static class LiveEnvironment
{
    /// <summary>
    /// Optional KEY=VALUE file (CUEBOT_LIVE_ENV_FILE, default ~/.cuebot/live.env) holding the settings below,
    /// including stream keys. Real environment variables win. Values are never logged.
    /// </summary>
    static LiveEnvironment()
    {
        var file = Environment.GetEnvironmentVariable("CUEBOT_LIVE_ENV_FILE")
                   ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cuebot", "live.env");
        if (!File.Exists(file)) return;
        foreach (var (key, value) in EnvFile.Parse(File.ReadAllLines(file)))
        {
            if (key == RunSwitch) continue;   // the run switch must come from the caller, never from the file
            if (Environment.GetEnvironmentVariable(key) is null) Environment.SetEnvironmentVariable(key, value);
        }
    }

    /// <summary>
    /// Live tests only run when the caller sets CUEBOT_LIVE_TESTS=1 (build/run-live-tests.* does), so a plain
    /// `dotnet test` of the solution never goes live just because ~/.cuebot/live.env exists.
    /// </summary>
    public const string RunSwitch = "CUEBOT_LIVE_TESTS";

    public static bool RunRequested => Environment.GetEnvironmentVariable(RunSwitch) == "1";

    /// <summary>Client ID of a Twitch application registered as a Public client.</summary>
    public static string? TwitchClientId => Get("CUEBOT_LIVE_TWITCH_CLIENT_ID");

    public static string? ObsPassword => Get("CUEBOT_OBS_PASSWORD");

    public static string ObsHost => Get("CUEBOT_OBS_HOST") ?? "127.0.0.1";

    public static int ObsPort => int.TryParse(Get("CUEBOT_OBS_PORT"), out var port) ? port : 4455;

    /// <summary>Going live is public, so it needs its own explicit switch: CUEBOT_LIVE_STREAM=1.</summary>
    public static bool StreamingAllowed => Get("CUEBOT_LIVE_STREAM") == "1";

    /// <summary>OBS scene shown while live (e.g. a "dev testing" card). Required when streaming.</summary>
    public static string? StreamScene => Get("CUEBOT_LIVE_OBS_SCENE");

    public static TimeSpan StreamDuration =>
        TimeSpan.FromSeconds(int.TryParse(Get("CUEBOT_LIVE_STREAM_SECONDS"), out var s) ? Math.Max(s, 20) : 60);

    /// <summary>Where the test keeps its Twitch sign-in between runs, so you only approve the device once.</summary>
    public static string DataDirectory =>
        Get("CUEBOT_LIVE_DATA") ?? Path.Combine(Path.GetTempPath(), "cuebot-live-tests");

    /// <summary>RTMP ingest for a platform from CUEBOT_LIVE_&lt;PLATFORM&gt;_RTMP_URL / _STREAM_KEY, if configured.</summary>
    public static StreamTarget? Target(string platform)
    {
        var prefix = $"CUEBOT_LIVE_{platform.ToUpperInvariant()}_";
        if (Get(prefix + "STREAM_KEY") is not { } key) return null;
        var url = Get(prefix + "RTMP_URL") ?? StreamTarget.DefaultUrl(platform);
        return url is null ? null : new StreamTarget(platform, url, key);
    }

    private static string? Get(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}

/// <summary>Where to send a test stream. <see cref="ToString"/> deliberately omits the key.</summary>
public sealed record StreamTarget(string Platform, string Url, string Key)
{
    public static string? DefaultUrl(string platform) => platform.ToLowerInvariant() switch
    {
        "twitch" => "rtmp://live.twitch.tv/app",
        "youtube" => "rtmps://a.rtmps.youtube.com:443/live2",
        _ => null,   // Kick's ingest URL is per channel: set CUEBOT_LIVE_KICK_RTMP_URL
    };

    public override string ToString() => $"{Platform} ({Url})";
}

/// <summary>Minimal .env parser: KEY=VALUE lines, # comments, optional surrounding quotes.</summary>
public static class EnvFile
{
    public static IEnumerable<(string Key, string Value)> Parse(IEnumerable<string> lines)
    {
        foreach (var raw in lines)
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ", StringComparison.Ordinal)) line = line[7..].TrimStart();
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) value = value[1..^1];
            yield return (line[..eq].Trim(), value);
        }
    }
}

/// <summary>Streams to one platform from the env file; needs OBS, CUEBOT_LIVE_STREAM=1 and CUEBOT_LIVE_OBS_SCENE.</summary>
public sealed class LivePlatformStreamFactAttribute : LiveFactAttribute
{
    public LivePlatformStreamFactAttribute(string platform)
    {
        if (Skip is not null) return;
        if (LiveEnvironment.ObsPassword is null)
            Skip = "Set CUEBOT_OBS_PASSWORD (see docs/LIVE_TESTING.md)";
        else if (!LiveEnvironment.StreamingAllowed || LiveEnvironment.StreamScene is null)
            Skip = "Going live is public: set CUEBOT_LIVE_STREAM=1 and CUEBOT_LIVE_OBS_SCENE to allow it";
        else if (LiveEnvironment.Target(platform) is null)
            Skip = $"Set CUEBOT_LIVE_{platform.ToUpperInvariant()}_STREAM_KEY (and _RTMP_URL for Kick) to stream to {platform}";
    }
}

/// <summary>Base for every live test: skips unless the run was explicitly requested (CUEBOT_LIVE_TESTS=1).</summary>
public abstract class LiveFactAttribute : FactAttribute
{
    protected LiveFactAttribute()
    {
        if (!LiveEnvironment.RunRequested)
            Skip = "Live tests run only via build/run-live-tests (sets CUEBOT_LIVE_TESTS=1); see docs/LIVE_TESTING.md";
    }
}

/// <summary>Runs only with CUEBOT_LIVE_TWITCH_CLIENT_ID set. Posts "dev testing" messages to your own chat.</summary>
public sealed class LiveTwitchFactAttribute : LiveFactAttribute
{
    public LiveTwitchFactAttribute()
    {
        if (Skip is not null) return;
        if (LiveEnvironment.TwitchClientId is null)
            Skip = "Set CUEBOT_LIVE_TWITCH_CLIENT_ID to run against real Twitch (see docs/LIVE_TESTING.md)";
    }
}

/// <summary>
/// Goes live on Twitch: needs the Twitch client ID and stream key, OBS, and CUEBOT_LIVE_STREAM=1 plus
/// CUEBOT_LIVE_OBS_SCENE, because it is public.
/// </summary>
public sealed class LiveTwitchStreamFactAttribute : LiveFactAttribute
{
    public LiveTwitchStreamFactAttribute()
    {
        if (Skip is not null) return;
        if (LiveEnvironment.TwitchClientId is null || LiveEnvironment.ObsPassword is null)
            Skip = "Set CUEBOT_LIVE_TWITCH_CLIENT_ID and CUEBOT_OBS_PASSWORD (see docs/LIVE_TESTING.md)";
        else if (!LiveEnvironment.StreamingAllowed || LiveEnvironment.StreamScene is null)
            Skip = "Going live is public: set CUEBOT_LIVE_STREAM=1 and CUEBOT_LIVE_OBS_SCENE to allow it";
        else if (LiveEnvironment.Target("twitch") is null)
            Skip = "Set CUEBOT_LIVE_TWITCH_STREAM_KEY to stream to Twitch";
    }
}
