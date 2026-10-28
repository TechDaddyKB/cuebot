using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core;
using Cuebot.WaveLink;
using Cuebot.WaveLink.Model;

namespace Cuebot.App.ViewModels.WaveLink;

/// <summary>
/// Elgato Wave Link tab: connection status, detected API and port, settings, the channels and mixes with mute and
/// volume, and a test round trip. Hosted as a tab by the page that owns it.
/// </summary>
public sealed partial class WaveLinkViewModel : ObservableObject
{
    private readonly WaveLinkService _wl;

    public WaveLinkViewModel(WaveLinkService waveLink)
    {
        _wl = waveLink;
        waveLink.StatusChanged += () => OnUi(Refresh);
        waveLink.DataChanged += () => OnUi(RefreshData);
        RefreshData();
    }

    public string Title => "Wave Link";

    internal static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private WaveLinkSettings S => _wl.Settings;

    // ---- Status ----

    public string State => _wl.State.ToString();
    public string StatusMessage => _wl.StatusMessage;
    public bool IsConnected => _wl.IsConnected;
    public bool IsAvailable => _wl.IsAvailable;
    public bool IsUnavailable => !_wl.IsAvailable;
    public string UnavailableText => _wl.UnsupportedReason is { } reason
        ? $"{reason}. Elgato only ships Wave Link for Windows and macOS; Cuebot's Wave Link triggers and sub-actions stay idle here."
        : "";

    public string Detected => _wl.IsConnected
        ? $"{_wl.ApiDisplay} · port {_wl.Port} · interface revision {_wl.InterfaceRevision}"
        : "—";

    // ---- Settings ----

    public IReadOnlyList<string> ApiChoices { get; } = ["Detect automatically", "Wave Link 3", "Wave Link 1.x/2.x"];

    public int ApiIndex
    {
        get => (int)S.Api;
        set { S.Api = (WaveLinkApiPreference)Math.Clamp(value, 0, 2); _wl.SaveSettings(); OnPropertyChanged(); }
    }

    /// <summary>0 = detect.</summary>
    public decimal? Port
    {
        get => S.Port;
        set { S.Port = (int)(value ?? 0); _wl.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; _wl.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoReconnect
    {
        get => S.AutoReconnect;
        set { S.AutoReconnect = value; _wl.SaveSettings(); OnPropertyChanged(); }
    }

    public bool LevelMeters
    {
        get => S.LevelMeters;
        set
        {
            S.LevelMeters = value;
            OnPropertyChanged();
            _ = ApplySubscriptionsAsync();
        }
    }

    public decimal? LevelMeterRate
    {
        get => (decimal)S.LevelMeterRate;
        set { S.LevelMeterRate = Math.Clamp((double)(value ?? 10), 1, 30); _wl.SaveSettings(); OnPropertyChanged(); }
    }

    private async Task ApplySubscriptionsAsync()
    {
        try { await _wl.ApplySubscriptionsAsync(); }
        catch (Exception ex) { Error = ex.Message; }
    }

    // ---- Channels and mixes ----

    public ObservableCollection<WaveLinkChannelRowViewModel> Channels { get; } = [];
    public ObservableCollection<WaveLinkMixRowViewModel> Mixes { get; } = [];

    public bool HasNoChannels => Channels.Count == 0;
    public string ChannelsHeader => _wl.Api == WaveLinkApi.Legacy ? "Inputs (volume = Monitor Mix)" : "Channels";

    public string Devices
    {
        get
        {
            if (!_wl.IsConnected) return "—";
            var s = _wl.Snapshot;
            var inputs = s.InputDevices.Count == 0 ? "no input devices" : string.Join(", ", s.InputDevices.Select(d => d.Name));
            var main = s.MainOutputDevice?.Name ?? "(none)";
            var monitored = s.MonitoredMixId.Length > 0 ? s.MixName(s.MonitoredMixId) : "(unknown)";
            return $"Inputs: {inputs} · Main output: {main} · Monitoring: {monitored}";
        }
    }

    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _info = "";

    private void Refresh() => OnPropertyChanged(string.Empty);

    private void RefreshData()
    {
        var s = _wl.IsConnected ? _wl.Snapshot : WaveLinkSnapshot.Empty;
        var channels = s.Channels.Select(c =>
        {
            var row = Channels.FirstOrDefault(r => r.Id == c.Id) ?? new WaveLinkChannelRowViewModel(this, c.Id);
            row.Update(c, s);
            return row;
        }).ToArray();
        Channels.SyncTo(channels);
        var mixes = s.Mixes.Select(m =>
        {
            var row = Mixes.FirstOrDefault(r => r.Id == m.Id) ?? new WaveLinkMixRowViewModel(this, m.Id);
            row.Update(m);
            return row;
        }).ToArray();
        Mixes.SyncTo(mixes);
        Refresh();
    }

    // ---- Commands ----

    [RelayCommand]
    private async Task Connect()
    {
        Error = "";
        try { await _wl.ConnectAsync(); }
        catch (Exception ex) { Error = ex.Message; }
        Refresh();
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        Error = "";
        await _wl.DisconnectAsync();
        Refresh();
    }

    [RelayCommand]
    private Task Test() => RunAsync(async () =>
    {
        var elapsed = await _wl.PingAsync();
        var s = _wl.Snapshot;
        return $"Wave Link answered in {elapsed.TotalMilliseconds:0} ms: {s.Channels.Count} channels, {s.Mixes.Count} mixes, " +
            $"{s.InputDevices.Count} input and {s.OutputDevices.Count} output devices.";
    });

    internal async Task RunAsync(Func<Task<string>> command)
    {
        Error = "";
        Info = "";
        try { Info = await command(); }
        catch (Exception ex) { Error = ex.Message; }
    }

    internal Task ToggleChannelMuteAsync(string id) => RunAsync(async () =>
    {
        var muted = await _wl.SetChannelMuteAsync(id, "", SwitchMode.Toggle);
        return $"{Name(id)} {(muted ? "muted" : "unmuted")}.";
    });

    internal Task SetChannelVolumeAsync(string id, double volume) => RunAsync(async () =>
    {
        // Wave Link 1.x/2.x has no channel volume of its own; the slider drives the Monitor Mix.
        var mix = _wl.Api == WaveLinkApi.Legacy ? "Monitor Mix" : "";
        var set = await _wl.SetChannelVolumeAsync(id, mix, volume);
        return $"{Name(id)} volume {set.ToString("0", CultureInfo.InvariantCulture)} %.";
    });

    internal Task ToggleMixMuteAsync(string id) => RunAsync(async () =>
    {
        var muted = await _wl.SetMixMuteAsync(id, SwitchMode.Toggle);
        return $"{_wl.Snapshot.MixName(id)} {(muted ? "muted" : "unmuted")}.";
    });

    internal Task SetMixVolumeAsync(string id, double volume) => RunAsync(async () =>
    {
        var set = await _wl.SetMixVolumeAsync(id, volume);
        return $"{_wl.Snapshot.MixName(id)} volume {set.ToString("0", CultureInfo.InvariantCulture)} %.";
    });

    internal Task MonitorMixAsync(string id) => RunAsync(async () => $"Monitoring {(await _wl.SetMonitoredMixAsync(id)).Name}.");

    private string Name(string channelId) => _wl.Snapshot.Channels.FirstOrDefault(c => c.Id == channelId)?.Name ?? channelId;
}

/// <summary>A slider value sent to Wave Link once the user pauses (not on every pixel of a drag).</summary>
public abstract partial class WaveLinkVolumeRow : ObservableObject
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(120);
    private CancellationTokenSource? _pending;
    private bool _updating;
    private double _volume;

    public double Volume
    {
        get => _volume;
        set
        {
            if (!SetProperty(ref _volume, value) || _updating) return;
            _pending?.Cancel();
            var pending = _pending = new CancellationTokenSource();
            _ = SendLaterAsync(value, pending.Token);
        }
    }

    private async Task SendLaterAsync(double value, CancellationToken ct)
    {
        try
        {
            await Task.Delay(Debounce, ct);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await SendAsync(value);
    }

    protected abstract Task SendAsync(double volume);

    protected void ShowVolume(double volume)
    {
        _updating = true;
        Volume = volume;
        _updating = false;
    }
}

public sealed partial class WaveLinkChannelRowViewModel(WaveLinkViewModel owner, string id) : WaveLinkVolumeRow
{
    public string Id => id;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private bool _muted;

    public string MuteLabel => Muted ? "Unmute" : "Mute";

    internal void Update(WaveLinkChannel channel, WaveLinkSnapshot snapshot)
    {
        Name = channel.Name;
        Muted = channel.Muted;
        var mixes = channel.Mixes.Select(m =>
            $"{(m.MixName.Length > 0 ? m.MixName : snapshot.MixName(m.MixId))} {m.Volume:0}%{(m.Muted ? " (muted)" : "")}");
        var extra = channel.Apps.Count > 0 ? $" · apps: {string.Join(", ", channel.Apps.Select(a => a.Name))}" : "";
        Details = string.Join(" · ", mixes) + extra;
        // Wave Link 3: the channel's own level; 1.x/2.x: its Monitor Mix volume (the model maps it so).
        ShowVolume(channel.Volume);
        OnPropertyChanged(nameof(MuteLabel));
    }

    protected override Task SendAsync(double volume) => owner.SetChannelVolumeAsync(id, volume);

    [RelayCommand]
    private Task ToggleMute() => owner.ToggleChannelMuteAsync(id);
}

public sealed partial class WaveLinkMixRowViewModel(WaveLinkViewModel owner, string id) : WaveLinkVolumeRow
{
    public string Id => id;

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private bool _muted;

    public string MuteLabel => Muted ? "Unmute" : "Mute";

    internal void Update(WaveLinkMix mix)
    {
        Name = mix.Name;
        Muted = mix.Muted;
        ShowVolume(mix.Volume);
        OnPropertyChanged(nameof(MuteLabel));
    }

    protected override Task SendAsync(double volume) => owner.SetMixVolumeAsync(id, volume);

    [RelayCommand]
    private Task ToggleMute() => owner.ToggleMixMuteAsync(id);

    [RelayCommand]
    private Task Monitor() => owner.MonitorMixAsync(id);
}
