namespace Cuebot.WaveLink;

/// <summary>Which Wave Link API a connection speaks.</summary>
public enum WaveLinkApi
{
    /// <summary>Not connected / not detected yet.</summary>
    None,

    /// <summary>Wave Link 1.x/2.x: inputs with a Monitor and a Stream mix, one microphone, setInputConfig/... methods.</summary>
    Legacy,

    /// <summary>Wave Link 3.x (2025+): channels, mixes, input/output devices, setChannel/setMix/... methods.</summary>
    V3,
}

/// <summary>Which API to look for. <see cref="Auto"/> scans both port ranges and asks the app.</summary>
public enum WaveLinkApiPreference { Auto, V3, Legacy }

/// <summary>Mute / Unmute / Toggle style switches (labels differ per sub-action; values parse leniently).</summary>
public enum SwitchMode { Toggle, On, Off }

/// <summary>Timeouts and intervals; tests use short ones.</summary>
public sealed record WaveLinkTimings(TimeSpan ConnectTimeout, TimeSpan RequestTimeout, TimeSpan ReconnectDelay, TimeSpan MaxReconnectDelay)
{
    public static WaveLinkTimings Default { get; } = new(
        ConnectTimeout: TimeSpan.FromSeconds(3),
        RequestTimeout: TimeSpan.FromSeconds(10),
        ReconnectDelay: TimeSpan.FromSeconds(2),
        MaxReconnectDelay: TimeSpan.FromSeconds(30));
}

/// <summary>A command was sent while Wave Link is not connected.</summary>
public sealed class WaveLinkNotConnectedException() : InvalidOperationException("Wave Link is not connected.");

/// <summary>Wave Link answered with a JSON-RPC error, or the request names something Wave Link does not have.</summary>
public sealed class WaveLinkRequestException(string message, int code = 0) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>The connected Wave Link version has no such feature (e.g. filter bypass on Wave Link 3).</summary>
public sealed class WaveLinkUnsupportedException(string message) : InvalidOperationException(message);
