using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.StreamApps;
using Cuebot.StreamApps.Obsbot;
using P = Cuebot.StreamApps.Obsbot.ObsbotProtocol;

namespace Cuebot.App.ViewModels.StreamApps;

/// <summary>OBSBOT tab: OBSBOT Center's OSC connection settings, the cameras it reports, and quick PTZ controls.</summary>
public sealed partial class ObsbotViewModel : StreamAppCardViewModel
{
    private readonly ObsbotService _obsbot;

    public ObsbotViewModel(ObsbotService obsbot) : base(obsbot)
    {
        _obsbot = obsbot;
        obsbot.DataChanged += () => StreamAppsViewModel.OnUi(RefreshData);
        RefreshData();
    }

    private ObsbotSettings S => _obsbot.Settings;

    public string Host
    {
        get => S.Host;
        set { S.Host = value.Trim(); _obsbot.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? Port
    {
        get => S.Port;
        set { S.Port = (int)(value ?? 16284); _obsbot.SaveSettings(); OnPropertyChanged(); }
    }

    public IReadOnlyList<string> Transports { get; } = ["UDP", "TCP"];

    public int TransportIndex
    {
        get => S.Transport == ObsbotTransport.Tcp ? 1 : 0;
        set { S.Transport = value == 1 ? ObsbotTransport.Tcp : ObsbotTransport.Udp; _obsbot.SaveSettings(); OnPropertyChanged(); OnPropertyChanged(nameof(IsUdp)); }
    }

    public bool IsUdp => S.Transport == ObsbotTransport.Udp;

    public decimal? ListenPort
    {
        get => S.ListenPort;
        set { S.ListenPort = (int)(value ?? 57120); _obsbot.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? PollSeconds
    {
        get => S.PollSeconds;
        set { S.PollSeconds = (int)(value ?? 5); _obsbot.SaveSettings(); OnPropertyChanged(); }
    }

    public IReadOnlyList<string> SeriesNames { get; } =
        ["Auto (from the camera name)", "Tiny series", "Tail series", "Meet series", "Tail 2 direct (no OBSBOT Center)", "Tail Air direct (no OBSBOT Center)"];

    public int SeriesIndex
    {
        get => (int)S.Series;
        set { S.Series = (ObsbotSeries)Math.Clamp(value, 0, 5); _obsbot.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; _obsbot.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoReconnect
    {
        get => S.AutoReconnect;
        set { S.AutoReconnect = value; _obsbot.SaveSettings(); OnPropertyChanged(); }
    }

    public bool CanSend => _obsbot.CanSend;

    public ObservableCollection<string> Devices { get; } = [];

    public bool HasNoDevices => Devices.Count == 0;

    public string Camera
    {
        get
        {
            var s = _obsbot.Current;
            if (s.SelectedDevice is null) return "—";
            var parts = new List<string> { s.SelectedName.Length > 0 ? s.SelectedName : $"Device {s.SelectedDevice + 1}" };
            if (s.Awake is { } awake) parts.Add(awake ? "awake" : "asleep");
            if (s.Zoom is { } z) parts.Add($"zoom {z}%");
            if (ObsbotState.FovLabel(s.Fov) is { Length: > 0 } fov) parts.Add($"FOV {fov}");
            if (s.Pitch is { } pitch && s.Yaw is { } yaw) parts.Add($"pitch {pitch}° yaw {yaw}°");
            if (s.AiLocked is { } locked) parts.Add(locked ? "AI locked" : "AI unlocked");
            parts.Add(_obsbot.Series + " series");
            return string.Join(" · ", parts);
        }
    }

    public string Presets => _obsbot.Current.Presets.Count == 0
        ? "—"
        : string.Join(" · ", _obsbot.Current.Presets.Select(p => $"{p.Index + 1}: {(p.Exists ? (p.Name.Length > 0 ? p.Name : "set") : "empty")}"));

    public string LastMessage => _obsbot.LastMessage?.ToString() ?? "—";

    [ObservableProperty] private decimal? _zoom = 0;

    protected override Task StartAsync() => _obsbot.ConnectAsync();

    protected override Task StopAsync() => _obsbot.DisconnectAsync();

    [RelayCommand]
    private Task ApplyZoom() => RunAsync(async () =>
    {
        var z = (int)Math.Clamp(Zoom ?? 0, 0, 100);
        await _obsbot.SendAsync(new OscMessage(P.General + "SetZoom", z));
        return $"Zoom set to {z}%.";
    });

    [RelayCommand]
    private Task Nudge(string direction) => RunAsync(async () =>
    {
        var address = P.General + "SetGimbal" + direction;
        await _obsbot.SendAsync(new OscMessage(address, 40));
        await Task.Delay(300);
        await _obsbot.SendAsync(new OscMessage(address, 0));
        return $"Moved {direction.ToLowerInvariant()}.";
    });

    [RelayCommand]
    private Task ResetGimbal() => RunAsync(async () =>
    {
        await _obsbot.SendAsync(new OscMessage(P.General + "ResetGimbal", 0));
        return "Gimbal reset.";
    });

    [RelayCommand]
    private Task Preset(string number) => RunAsync(async () =>
    {
        var index = int.Parse(number, System.Globalization.CultureInfo.InvariantCulture) - 1;
        await _obsbot.SendAsync(new OscMessage((P.IsTail(_obsbot.Series) ? P.TailPrefix(_obsbot.Series) : P.Tiny) + "TriggerPreset", index));
        return $"Preset {number}.";
    });

    [RelayCommand]
    private Task ToggleAiLock() => RunAsync(async () =>
    {
        var locked = !(_obsbot.Current.AiLocked ?? false);
        await _obsbot.SendAsync(new OscMessage(P.Tiny + "ToggleAILock", locked ? 1 : 0));
        return locked ? "AI tracking locked." : "AI tracking unlocked.";
    });

    [RelayCommand]
    private Task Snapshot() => RunAsync(async () =>
    {
        await _obsbot.SendAsync(new OscMessage(P.General + "PCSnapshot", 1));
        return "Snapshot requested (OBSBOT Center saves it while the preview is open).";
    });

    [RelayCommand]
    private Task RefreshState() => RunAsync(async () =>
    {
        await _obsbot.RefreshAsync();
        RefreshData();
        return "State refreshed.";
    });

    private void RefreshData()
    {
        Devices.Clear();
        foreach (var d in _obsbot.Current.Devices.Where(d => d.Connected))
            Devices.Add($"{d.Index + 1}. {(d.Name.Length > 0 ? d.Name : "OBSBOT camera")}{(d.Index == _obsbot.Current.SelectedDevice ? " (selected)" : "")}");
        if (_obsbot.Current.Zoom is { } z) Zoom = z;
        Refresh();
    }
}
