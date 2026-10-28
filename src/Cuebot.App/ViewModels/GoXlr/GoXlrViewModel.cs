using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.GoXlr;
using Cuebot.GoXlr.Model;

namespace Cuebot.App.ViewModels.GoXlr;

/// <summary>
/// GoXLR tab: connection to the GoXLR Utility (host, port, auto-connect), the connected device, its faders with volume
/// and mute, profiles, effects and the cough button, and a test round trip. Hosted as a tab by the page that owns it.
/// </summary>
public sealed partial class GoXlrViewModel : ObservableObject
{
    private static readonly string[] EffectNames = ["FX", "Megaphone", "Robot", "HardTune"];
    private readonly GoXlrService _g;

    public GoXlrViewModel(GoXlrService goXlr)
    {
        _g = goXlr;
        goXlr.StatusChanged += () => OnUi(Refresh);
        goXlr.DataChanged += () => OnUi(RefreshData);
        RefreshData();
    }

    public string Title => "GoXLR";

    internal static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }

    private GoXlrSettings S => _g.Settings;

    // ---- Status ----

    public string State => _g.State.ToString();
    public string StatusMessage => _g.StatusMessage;
    public bool IsConnected => _g.IsConnected;

    public string Detected
    {
        get
        {
            if (!_g.IsConnected) return "—";
            var s = _g.Status;
            if (s.Mixers.Count == 0) return $"GoXLR Utility {s.DaemonVersion} · no GoXLR plugged in";
            return $"GoXLR Utility {s.DaemonVersion} · " + string.Join(", ", s.Mixers.Select(m => $"{(m.IsMini ? "GoXLR Mini" : "GoXLR")} {m.Serial}"));
        }
    }

    // ---- Settings ----

    public string Host
    {
        get => S.Host;
        set { S.Host = value.Trim(); _g.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? Port
    {
        get => S.Port;
        set { S.Port = (int)(value ?? 14564); _g.SaveSettings(); OnPropertyChanged(); }
    }

    /// <summary>Empty = the first GoXLR the utility reports.</summary>
    public string Serial
    {
        get => S.Serial;
        set { S.Serial = value.Trim(); _g.SaveSettings(); OnPropertyChanged(); RefreshData(); }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; _g.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoReconnect
    {
        get => S.AutoReconnect;
        set { S.AutoReconnect = value; _g.SaveSettings(); OnPropertyChanged(); }
    }

    // ---- Device ----

    public ObservableCollection<GoXlrFaderRowViewModel> Faders { get; } = [];
    public ObservableCollection<GoXlrEffectRowViewModel> Effects { get; } = [];
    public ObservableCollection<string> Profiles { get; } = [];
    public ObservableCollection<string> MicProfiles { get; } = [];

    public bool HasDevice => Faders.Count > 0;
    public bool HasNoDevice => !HasDevice;
    public bool HasEffects => Effects.Count > 0;

    [ObservableProperty] private string _deviceSummary = "";
    [ObservableProperty] private string _coughLabel = "Cough: —";
    [ObservableProperty] private string? _selectedProfile;
    [ObservableProperty] private string? _selectedMicProfile;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _info = "";

    private GoXlrMixer? Current()
    {
        if (!_g.IsConnected) return null;
        try { return _g.Mixer(); }
        catch (GoXlrRequestException) { return null; }
    }

    private void Refresh() => OnPropertyChanged(string.Empty);

    private void RefreshData()
    {
        var m = Current();
        var rows = m is null ? [] : m.Faders.Values.OrderBy(f => f.Name).Select(f =>
        {
            var row = Faders.FirstOrDefault(r => r.Fader == f.Name) ?? new GoXlrFaderRowViewModel(this, f.Name);
            row.Update(f, m);
            return row;
        }).ToArray();
        Faders.SyncTo(rows);
        var effects = m?.Effects is { } e ? EffectNames.Select(name =>
        {
            var row = Effects.FirstOrDefault(r => r.Effect == name) ?? new GoXlrEffectRowViewModel(this, name);
            row.Update(e.IsEnabled(name));
            return row;
        }).ToArray() : [];
        Effects.SyncTo(effects);
        Replace(Profiles, _g.Status.Profiles);
        Replace(MicProfiles, _g.Status.MicProfiles);
        if (m is null)
        {
            DeviceSummary = "";
            CoughLabel = "Cough: —";
        }
        else
        {
            SelectedProfile ??= m.ProfileName;
            SelectedMicProfile ??= m.MicProfileName;
            var preset = m.Effects is { } fx ? $" · Effect preset: {fx.PresetName(fx.ActivePreset)}" : "";
            var bank = m.Sampler is { } s ? $" · Sampler bank {s.ActiveBank}" : "";
            DeviceSummary = $"Profile: {m.ProfileName} · Mic profile: {m.MicProfileName}{preset}{bank}";
            CoughLabel = m.Cough.Muted ? "Unmute mic (cough)" : "Mute mic (cough)";
        }
        Refresh();
    }

    private static void Replace(ObservableCollection<string> target, IReadOnlyList<string> items)
    {
        if (target.SequenceEqual(items)) return;
        target.Clear();
        foreach (var item in items) target.Add(item);
    }

    // ---- Commands ----

    [RelayCommand]
    private async Task Connect()
    {
        Error = "";
        try { await _g.ConnectAsync(); }
        catch (Exception ex) { Error = ex.Message; }
        Refresh();
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        Error = "";
        await _g.DisconnectAsync();
        Refresh();
    }

    [RelayCommand]
    private Task Test() => RunAsync(async () =>
    {
        var elapsed = await _g.PingAsync();
        return $"The GoXLR Utility answered in {elapsed.TotalMilliseconds:0} ms ({_g.Status.Mixers.Count} device(s)).";
    });

    [RelayCommand]
    private Task LoadProfile() => RunAsync(async () =>
    {
        var name = SelectedProfile ?? throw new ArgumentException("Pick a profile first.");
        await _g.LoadProfileAsync(name);
        return $"Loaded profile {name}.";
    });

    [RelayCommand]
    private Task LoadMicProfile() => RunAsync(async () =>
    {
        var name = SelectedMicProfile ?? throw new ArgumentException("Pick a mic profile first.");
        await _g.LoadMicProfileAsync(name);
        return $"Loaded mic profile {name}.";
    });

    [RelayCommand]
    private Task ToggleCough() => RunAsync(async () =>
        $"Mic {(Cuebot.GoXlr.Protocol.GoXlrNames.IsMuted(await _g.SetCoughMuteAsync(GoXlrMuteAction.Toggle)) ? "muted" : "unmuted")}.");

    internal async Task RunAsync(Func<Task<string>> command)
    {
        Error = "";
        Info = "";
        try { Info = await command(); }
        catch (Exception ex) { Error = ex.Message; }
    }

    internal Task ToggleFaderMuteAsync(string fader) => RunAsync(async () =>
    {
        var state = await _g.SetFaderMuteAsync(fader, GoXlrMuteAction.Toggle);
        return $"Fader {fader} {(Cuebot.GoXlr.Protocol.GoXlrNames.IsMuted(state) ? "muted" : "unmuted")}.";
    });

    internal Task SetVolumeAsync(string channel, double volume) => RunAsync(async () =>
        $"{Cuebot.GoXlr.Protocol.GoXlrNames.Label(channel)} volume {await _g.SetVolumeAsync(channel, volume)} %.");

    internal Task ToggleEffectAsync(string effect) => RunAsync(async () =>
        $"{Cuebot.GoXlr.Protocol.GoXlrNames.Label(effect)} {(await _g.SetEffectAsync(effect, SwitchMode.Toggle) ? "on" : "off")}.");
}

/// <summary>One fader: the channel on it, its volume (sent once the slider pauses) and a mute toggle.</summary>
public sealed partial class GoXlrFaderRowViewModel(GoXlrViewModel owner, string fader) : ObservableObject
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(120);
    private CancellationTokenSource? _pending;
    private bool _updating;
    private double _volume;
    private string _channel = "";

    public string Fader => fader;
    public string Label => $"Fader {fader}";

    [ObservableProperty] private string _channelName = "";
    [ObservableProperty] private bool _muted;

    public string MuteLabel => Muted ? "Unmute" : "Mute";

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
        try { await Task.Delay(Debounce, ct); }
        catch (OperationCanceledException) { return; }
        await owner.SetVolumeAsync(_channel, value);
    }

    internal void Update(GoXlrFader f, GoXlrMixer m)
    {
        _channel = f.Channel;
        ChannelName = Cuebot.GoXlr.Protocol.GoXlrNames.Label(f.Channel);
        Muted = f.Muted;
        _updating = true;
        Volume = Cuebot.GoXlr.Protocol.GoXlrNames.Percent(m.Volume(f.Channel));
        _updating = false;
        OnPropertyChanged(nameof(MuteLabel));
    }

    [RelayCommand]
    private Task ToggleMute() => owner.ToggleFaderMuteAsync(fader);
}

/// <summary>One effect switch (FX, Megaphone, Robot, Hard Tune).</summary>
public sealed partial class GoXlrEffectRowViewModel(GoXlrViewModel owner, string effect) : ObservableObject
{
    public string Effect => effect;

    [ObservableProperty] private string _label = "";

    internal void Update(bool enabled) => Label = $"{Cuebot.GoXlr.Protocol.GoXlrNames.Label(effect)}: {(enabled ? "on" : "off")}";

    [RelayCommand]
    private Task Toggle() => owner.ToggleEffectAsync(effect);
}
