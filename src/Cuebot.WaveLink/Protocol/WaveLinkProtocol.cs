using System.Text.Json;
using System.Text.Json.Nodes;
using Cuebot.WaveLink.Common;
using Cuebot.WaveLink.Model;

namespace Cuebot.WaveLink.Protocol;

/// <summary>What a protocol needs from the service: settings, event publishing, level meters and change notification.</summary>
internal interface IWaveLinkHost
{
    WaveLinkSettings Settings { get; }

    /// <summary>Publishes a bot event; the service adds waveLinkApi/waveLinkPort.</summary>
    void Publish(string type, Dictionary<string, object?> args);

    /// <summary>Hands level meter readings to the throttle (ignored while level meters are off).</summary>
    void OfferLevels(IReadOnlyList<WaveLinkLevel> levels);

    void Log(string message);

    /// <summary>The snapshot changed (for the UI).</summary>
    void DataChanged();
}

/// <summary>
/// One API generation's view of Wave Link: loads the state, turns notifications into Cuebot events and implements the
/// commands. Names given to commands are resolved against the snapshot (id first, then name). Every command updates the
/// local state as soon as Wave Link accepted it, through the same path as notifications, so triggers fire once whether
/// or not Wave Link echoes the change back.
/// </summary>
internal abstract class WaveLinkProtocol(JsonRpcPeer rpc, IWaveLinkHost host)
{
    protected readonly object Gate = new();
    private volatile WaveLinkSnapshot _snapshot = WaveLinkSnapshot.Empty;

    public abstract WaveLinkApi Api { get; }

    public WaveLinkSnapshot Snapshot
    {
        get => _snapshot;
        protected set => _snapshot = value;
    }

    protected JsonRpcPeer Rpc => rpc;
    protected IWaveLinkHost Host => host;

    /// <summary>Loads everything; called once before the connection is reported.</summary>
    public abstract Task InitializeAsync(CancellationToken ct);

    /// <summary>Handles a notification (on the read loop).</summary>
    public abstract void OnNotification(string method, JsonElement parameters);

    /// <summary>The level meter settings changed while connected.</summary>
    public virtual Task UpdateSubscriptionsAsync(CancellationToken ct) => Task.CompletedTask;

    /// <summary>A request round trip (getApplicationInfo), for the "Test" button.</summary>
    public Task<JsonElement> PingAsync(CancellationToken ct) => rpc.CallAsync("getApplicationInfo", null, ct);

    protected Task<JsonElement> CallAsync(string method, JsonNode? parameters, CancellationToken ct) => rpc.CallAsync(method, parameters, ct);

    /// <summary>Publishes collected events after the state lock is released.</summary>
    protected void Flush(List<(string Type, Dictionary<string, object?> Args)> events)
    {
        foreach (var (type, args) in events) host.Publish(type, args);
        host.DataChanged();
    }

    protected WaveLinkUnsupportedException Unsupported(string what) => new(Api == WaveLinkApi.V3
        ? $"{what} is a Wave Link 1.x/2.x feature; the connected Wave Link 3 does not offer it."
        : $"{what} needs Wave Link 3; the connected Wave Link 1.x/2.x does not offer it.");

    /// <summary>Empty, "Both" or "All": the whole channel rather than one mix.</summary>
    protected static bool IsAll(string mix) => mix.Trim() is "" || mix.Trim().Equals("both", StringComparison.OrdinalIgnoreCase)
        || mix.Trim().Equals("all", StringComparison.OrdinalIgnoreCase);

    protected static double Apply(double current, double value, bool relative, double min = 0, double max = 100) =>
        Math.Clamp(relative ? current + value : value, min, max);

    // ---- Commands (names are resolved by the implementations) ----

    public virtual Task<bool> SetChannelMuteAsync(string channel, string mix, SwitchMode mode, CancellationToken ct) => throw Unsupported("Channel mute");
    public virtual Task<double> SetChannelVolumeAsync(string channel, string mix, double value, bool relative, CancellationToken ct) => throw Unsupported("Channel volume");
    public virtual Task<bool> SetChannelEffectAsync(string channel, string effect, SwitchMode mode, CancellationToken ct) => throw Unsupported("Channel effects");
    public virtual Task<bool> SetFilterBypassAsync(string channel, string mix, SwitchMode mode, CancellationToken ct) => throw Unsupported("Filter bypass");
    public virtual Task<WaveLinkApp> AddAppToChannelAsync(string channel, string app, CancellationToken ct) => throw Unsupported("Adding an application to a channel");
    public virtual Task<bool> SetMixMuteAsync(string mix, SwitchMode mode, CancellationToken ct) => throw Unsupported("Mix mute");
    public virtual Task<double> SetMixVolumeAsync(string mix, double value, bool relative, CancellationToken ct) => throw Unsupported("Mix volume");
    public virtual Task<bool> SetInputMuteAsync(string device, SwitchMode mode, CancellationToken ct) => throw Unsupported("Input mute");
    public virtual Task<double> SetInputGainAsync(string device, double value, bool relative, CancellationToken ct) => throw Unsupported("Input gain");
    public virtual Task<bool> SetInputGainLockAsync(string device, SwitchMode mode, CancellationToken ct) => throw Unsupported("Gain lock");
    public virtual Task<bool> SetInputLowCutAsync(string device, SwitchMode mode, CancellationToken ct) => throw Unsupported("Low cut filter");
    public virtual Task<bool> SetInputClipGuardAsync(string device, SwitchMode mode, CancellationToken ct) => throw Unsupported("Clipguard");
    public virtual Task<double> SetMicPcMixAsync(string device, double value, bool relative, CancellationToken ct) => throw Unsupported("Mic/PC balance");
    public virtual Task<double> SetMicOutputVolumeAsync(string device, double value, bool relative, CancellationToken ct) => throw Unsupported("Microphone output volume");
    public virtual Task<WaveLinkOutputDevice> SetMainOutputAsync(string device, CancellationToken ct) => throw Unsupported("Selecting the output device");
    public virtual Task<double> SetOutputVolumeAsync(string device, double value, bool relative, CancellationToken ct) => throw Unsupported("Output device volume");
    public virtual Task<bool> SetOutputMuteAsync(string device, SwitchMode mode, CancellationToken ct) => throw Unsupported("Output device mute");
    public virtual Task AddOutputToMixAsync(string device, string mix, CancellationToken ct) => throw Unsupported("Assigning an output to a mix");
    public virtual Task<WaveLinkMix> SetMonitoredMixAsync(string mix, CancellationToken ct) => throw Unsupported("Selecting the monitored mix");
}
