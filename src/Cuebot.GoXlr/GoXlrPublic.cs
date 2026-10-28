namespace Cuebot.GoXlr;

public static class GoXlrDefaults
{
    /// <summary>The GoXLR Utility's default HTTP/WebSocket port.</summary>
    public const int Port = 14564;

    /// <summary>The WebSocket endpoint on the utility's HTTP server.</summary>
    public const string WebSocketPath = "/api/websocket";
}

/// <summary>On / Off / Toggle switches (values parse leniently).</summary>
public enum SwitchMode { Toggle, On, Off }

/// <summary>Timeouts and intervals; tests use short ones.</summary>
public sealed record GoXlrTimings(TimeSpan ConnectTimeout, TimeSpan RequestTimeout, TimeSpan ReconnectDelay, TimeSpan MaxReconnectDelay)
{
    public static GoXlrTimings Default { get; } = new(
        ConnectTimeout: TimeSpan.FromSeconds(3),
        RequestTimeout: TimeSpan.FromSeconds(10),
        ReconnectDelay: TimeSpan.FromSeconds(2),
        MaxReconnectDelay: TimeSpan.FromSeconds(30));
}

/// <summary>A command was sent while the GoXLR Utility is not connected.</summary>
public sealed class GoXlrNotConnectedException() : InvalidOperationException("The GoXLR Utility is not connected.");

/// <summary>The utility answered with an error, or the request names something the GoXLR does not have.</summary>
public sealed class GoXlrRequestException(string message) : Exception(message);
