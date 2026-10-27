using System.Net.WebSockets;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Integrations.Common;

namespace Cuebot.Integrations.HeartRate;

/// <summary>A heart rate source: publishes "&lt;prefix&gt;.heartRate" for each reading and updates the <see cref="HeartRateHub"/>.</summary>
public abstract class HeartRateConnection : IntegrationConnection
{
    private protected HeartRateConnection(string name, HeartRateHub hub, EventBus bus, AppLog log, Func<IWebSocketTransport> transports,
        IntegrationsTimings timings) : base(name, bus, log, transports, timings)
    {
        Hub = hub;
    }

    public HeartRateHub Hub { get; }

    /// <summary>The newest reading of this source.</summary>
    public HeartRateReading? Latest => Hub.Get(Name);

    /// <summary>Plausible human heart rates only; anything else is a sensor glitch (0 = no contact).</summary>
    internal static bool Plausible(int bpm) => bpm is > 0 and < 300;

    private protected void Report(int bpm)
    {
        if (!Plausible(bpm)) return;
        var previous = Hub.Report(Name, bpm);
        Publish("heartRate", new Dictionary<string, object?>
        {
            ["heartRate"] = bpm,
            ["previousHeartRate"] = previous?.Bpm ?? 0,
            ["source"] = Name,
        });
    }

    /// <summary>HTTP 401/403 (and 400 for a missing scope) on the websocket upgrade are credential problems.</summary>
    private protected static int? UpgradeStatus(Exception error)
    {
        if (error is not WebSocketException) return null;
        foreach (var code in new[] { 400, 401, 403 })
            if (error.Message.Contains($"'{code}'", StringComparison.Ordinal)) return code;
        return null;
    }
}
