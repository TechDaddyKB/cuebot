namespace Cuebot.StreamApps;

/// <summary>Timeouts and intervals of the stream-app connections; tests use short ones.</summary>
public sealed record StreamAppsTimings(TimeSpan ConnectTimeout, TimeSpan RequestTimeout, TimeSpan ReconnectDelay, TimeSpan MaxReconnectDelay)
{
    public static StreamAppsTimings Default { get; } = new(
        ConnectTimeout: TimeSpan.FromSeconds(5),
        RequestTimeout: TimeSpan.FromSeconds(10),
        ReconnectDelay: TimeSpan.FromSeconds(2),
        MaxReconnectDelay: TimeSpan.FromSeconds(30));
}

/// <summary>A command was sent while the app is not connected.</summary>
public sealed class StreamAppNotConnectedException(string name) : InvalidOperationException($"{name} is not connected.");

/// <summary>The app does not offer what was asked for (e.g. an API version too old, an unknown scene).</summary>
public sealed class StreamAppRequestException(string message) : Exception(message);

/// <summary>Visible / Hidden / Toggle style switches (labels differ per sub-action, values parse leniently).</summary>
public enum SwitchMode { Toggle, On, Off }

