using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core;
using Cuebot.Elgato;
using Cuebot.Elgato.CameraHub;
using Cuebot.Elgato.Common;
using Cuebot.Elgato.Lights;
using Cuebot.Elgato.Studio;

namespace Cuebot.App.ViewModels.Elgato;

/// <summary>Elgato tab: lights (Control Center), Camera Hub (webcam control) and Elgato Studio status.</summary>
public sealed class ElgatoViewModel : ObservableObject
{
    public ElgatoViewModel(ElgatoLightsService lights, CameraHubService cameraHub)
    {
        Lights = new ElgatoLightsCardViewModel(lights);
        CameraHub = new CameraHubCardViewModel(cameraHub);
        Studio = new ElgatoStudioCardViewModel();
    }

    public string Title => "Elgato";

    public ElgatoLightsCardViewModel Lights { get; }
    public CameraHubCardViewModel CameraHub { get; }
    public ElgatoStudioCardViewModel Studio { get; }

    internal static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}

public sealed partial class ElgatoLightsCardViewModel : ObservableObject
{
    private readonly ElgatoLightsService _lights;

    public ElgatoLightsCardViewModel(ElgatoLightsService lights)
    {
        _lights = lights;
        lights.StatusChanged += () => ElgatoViewModel.OnUi(() => OnPropertyChanged(string.Empty));
        lights.LightsChanged += () => ElgatoViewModel.OnUi(SyncRows);
        SyncRows();
    }

    private ElgatoLightsSettings S => _lights.Settings;

    public string State => _lights.State.ToString();
    public string StatusMessage => _lights.StatusMessage;
    public bool IsRunning => _lights.IsRunning;

    public bool AutoStart
    {
        get => S.AutoStart;
        set { S.AutoStart = value; _lights.SaveSettings(); OnPropertyChanged(); }
    }

    public bool Discovery
    {
        get => S.Discovery;
        set { S.Discovery = value; _lights.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? PollSeconds
    {
        get => S.PollIntervalMs / 1000m;
        set { S.PollIntervalMs = (int)Math.Clamp((value ?? 2) * 1000, 500, 60000); _lights.SaveSettings(); OnPropertyChanged(); }
    }

    public ObservableCollection<ElgatoLightRowViewModel> Rows { get; } = [];

    public bool HasNoLights => Rows.Count == 0;

    [ObservableProperty] private string _newAddress = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _info = "";

    [RelayCommand]
    private Task Start() => RunAsync(async () =>
    {
        await _lights.StartPollingAsync();
        return S.Discovery ? "Polling; looking for lights on the network..." : "Polling the lights below.";
    });

    [RelayCommand]
    private Task Stop() => RunAsync(async () =>
    {
        await _lights.StopAsync();
        return "Stopped.";
    });

    [RelayCommand]
    private Task Discover() => RunAsync(async () =>
    {
        var added = await _lights.DiscoverAsync();
        return added.Count == 0 ? "No new lights found." : $"Found {string.Join(", ", added.Select(l => l.Name))}.";
    });

    [RelayCommand]
    private Task Add() => RunAsync(async () =>
    {
        var light = await _lights.AddLightAsync(NewAddress);
        NewAddress = "";
        return $"Added {light.Name}.";
    });

    internal Task RunAsync(Func<Task<string>> command) => RunCoreAsync(command);

    private async Task RunCoreAsync(Func<Task<string>> command)
    {
        Error = "";
        Info = "";
        try { Info = await command(); }
        catch (Exception ex) { Error = ex.Message; }
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(IsRunning));
    }

    internal void Remove(ElgatoLight light)
    {
        Error = "";
        try { _lights.RemoveLights(light.Id); }
        catch (Exception ex) { Error = ex.Message; }
    }

    internal ElgatoLightsService Service => _lights;

    private void SyncRows()
    {
        var lights = _lights.Lights;
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (!lights.Contains(Rows[i].Light)) Rows.RemoveAt(i);
        foreach (var light in lights)
        {
            var row = Rows.FirstOrDefault(r => r.Light == light);
            if (row is null) Rows.Add(new ElgatoLightRowViewModel(this, light));
            else row.Refresh();
        }
        OnPropertyChanged(nameof(HasNoLights));
    }
}

/// <summary>One light: power switch, brightness and temperature sliders (applied after the slider rests for a moment).</summary>
public sealed partial class ElgatoLightRowViewModel : ObservableObject
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);
    private readonly ElgatoLightsCardViewModel _card;
    private CancellationTokenSource? _pending;
    private bool _syncing;

    internal ElgatoLightRowViewModel(ElgatoLightsCardViewModel card, ElgatoLight light)
    {
        _card = card;
        Light = light;
        Refresh();
    }

    internal ElgatoLight Light { get; }

    public string Name => Light.Name;
    public string Details => $"{Light.ProductName} · {Light.Address}:{Light.Port}{(Light.IsManual ? " · added by hand" : "")}";
    public string Reachability => Light.IsOnline ? "Online" : Light.LastError is { } e ? $"Offline ({e})" : "Offline";
    public bool IsOnline => Light.IsOnline;
    public bool HasTemperature => Light.State?.Temperature is not null;
    public bool HasColor => Light.State?.Hue is not null;

    [ObservableProperty] private bool _isOn;
    [ObservableProperty] private double _brightness;
    [ObservableProperty] private double _temperature = 4500;

    internal void Refresh()
    {
        if (_pending is null && Light.State is { } s)
        {
            _syncing = true;
            IsOn = s.On;
            Brightness = s.Brightness;
            if (s.Kelvin is { } k) Temperature = k;
            _syncing = false;
        }
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Details));
        OnPropertyChanged(nameof(Reachability));
        OnPropertyChanged(nameof(IsOnline));
        OnPropertyChanged(nameof(HasTemperature));
        OnPropertyChanged(nameof(HasColor));
    }

    partial void OnIsOnChanged(bool value)
    {
        if (!_syncing) Apply(s => s.SetPowerAsync(Light.Id, value ? SwitchMode.On : SwitchMode.Off));
    }

    partial void OnBrightnessChanged(double value)
    {
        if (!_syncing) Apply(s => s.SetBrightnessAsync(Light.Id, Math.Round(value)));
    }

    partial void OnTemperatureChanged(double value)
    {
        if (!_syncing && HasTemperature) Apply(s => s.SetTemperatureAsync(Light.Id, Math.Round(value / 50) * 50));
    }

    private void Apply(Func<ElgatoLightsService, Task> change)
    {
        _pending?.Cancel();
        var cts = new CancellationTokenSource();
        _pending = cts;
        _ = ApplyLaterAsync(change, cts);
    }

    private async Task ApplyLaterAsync(Func<ElgatoLightsService, Task> change, CancellationTokenSource cts)
    {
        try
        {
            await Task.Delay(Debounce, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        await _card.RunAsync(async () =>
        {
            await change(_card.Service);
            return "";
        });
        if (_pending == cts) _pending = null;
        cts.Dispose();
        Refresh();
    }

    [RelayCommand]
    private Task Identify() => _card.RunAsync(async () =>
    {
        await _card.Service.IdentifyAsync(Light.Id);
        return $"{Light.Name} is blinking.";
    });

    [RelayCommand]
    private void Remove() => _card.Remove(Light);
}

public sealed partial class CameraHubCardViewModel : ObservableObject
{
    private readonly CameraHubService _hub;

    public CameraHubCardViewModel(CameraHubService hub)
    {
        _hub = hub;
        hub.StatusChanged += () => ElgatoViewModel.OnUi(() => OnPropertyChanged(string.Empty));
        hub.WebcamsChanged += () => ElgatoViewModel.OnUi(SyncRows);
        SyncRows();
    }

    public bool IsAvailable => _hub.IsAvailable;
    public string State => _hub.State.ToString();
    public string StatusMessage => _hub.StatusMessage;
    public bool IsConnected => _hub.State == ConnectionState.Connected;

    public string AppStatus => ElgatoAppPresence.IsRunning(ElgatoAppPresence.CameraHubProcesses)
        ? "The Camera Hub app is running on this PC, but it has no public control API."
        : "The Camera Hub app is not running on this PC.";

    public string PrompterNote => ElgatoAppPresence.CameraHubPrompterUnavailable;

    public bool AutoConnect
    {
        get => _hub.Settings.AutoConnect;
        set { _hub.Settings.AutoConnect = value; _hub.SaveSettings(); OnPropertyChanged(); }
    }

    public string NameFilter
    {
        get => _hub.Settings.NameFilter;
        set { _hub.Settings.NameFilter = value.Trim(); _hub.SaveSettings(); OnPropertyChanged(); }
    }

    public ObservableCollection<WebcamRowViewModel> Webcams { get; } = [];

    [ObservableProperty] private string _error = "";

    [RelayCommand]
    private async Task Connect()
    {
        Error = "";
        try { await _hub.ConnectAsync(); }
        catch (Exception ex) { Error = ex.Message; }
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        Error = "";
        await _hub.DisconnectAsync();
    }

    private void SyncRows()
    {
        Webcams.Clear();
        foreach (var webcam in _hub.Webcams) Webcams.Add(new WebcamRowViewModel(webcam));
        OnPropertyChanged(string.Empty);
    }
}

public sealed class WebcamRowViewModel(Webcam webcam)
{
    public string Name => webcam.Name;

    public string Settings => string.Join(" · ", CameraHubScript.Describe(webcam)
        .Where(p => p.Key is not ("deviceId" or "deviceName" or "devicePath" or "zoomPercent" or "maximumZoom") && p.Value is not null)
        .Select(p => $"{p.Key} {Format(p.Value)}"));

    public string Path => $"{webcam.Device.Path} · {webcam.Id}";

    private static string Format(object? value) => value switch
    {
        bool b => b ? "on" : "off",
        _ => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? "",
    };
}

public sealed class ElgatoStudioCardViewModel
{
    public string AppStatus => ElgatoAppPresence.IsRunning(ElgatoAppPresence.StudioProcesses)
        ? "Elgato Studio is running on this PC."
        : "Elgato Studio is not running on this PC.";

    public string Note => ElgatoAppPresence.StudioUnavailable;
}
