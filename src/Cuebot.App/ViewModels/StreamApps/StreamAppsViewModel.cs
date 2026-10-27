using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core;
using Cuebot.StreamApps;
using Cuebot.StreamApps.Meld;
using Cuebot.StreamApps.PolyPop;

namespace Cuebot.App.ViewModels.StreamApps;

/// <summary>Stream apps tab (hosted on the OBS page): a card for Meld Studio and one for PolyPop.</summary>
public sealed class StreamAppsViewModel : ObservableObject
{
    public StreamAppsViewModel(MeldStudioService meld, PolyPopService polyPop)
    {
        Meld = new MeldCardViewModel(meld);
        PolyPop = new PolyPopCardViewModel(polyPop);
    }

    public string Title => "Meld & PolyPop";

    public MeldCardViewModel Meld { get; }
    public PolyPopCardViewModel PolyPop { get; }

    internal static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}

/// <summary>Status, connect/disconnect, error and info lines shared by both cards.</summary>
public abstract partial class StreamAppCardViewModel : ObservableObject
{
    private readonly IConnectionStatus _status;

    protected StreamAppCardViewModel(IConnectionStatus status)
    {
        _status = status;
        status.StatusChanged += () => StreamAppsViewModel.OnUi(Refresh);
    }

    public string State => _status.State.ToString();
    public string StatusMessage => _status.StatusMessage;
    public bool IsConnected => _status.State == ConnectionState.Connected;

    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _info = "";

    protected virtual void Refresh() => OnPropertyChanged(string.Empty);

    protected abstract Task StartAsync();

    protected abstract Task StopAsync();

    [RelayCommand]
    private async Task Connect()
    {
        Error = "";
        try { await StartAsync(); }
        catch (Exception ex) { Error = ex.Message; }
        Refresh();
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        Error = "";
        await StopAsync();
        Refresh();
    }

    protected async Task RunAsync(Func<Task<string>> command)
    {
        Error = "";
        Info = "";
        try { Info = await command(); }
        catch (Exception ex) { Error = ex.Message; }
    }
}

public sealed partial class MeldCardViewModel : StreamAppCardViewModel
{
    private readonly MeldStudioService _meld;

    public MeldCardViewModel(MeldStudioService meld) : base(meld)
    {
        _meld = meld;
        meld.DataChanged += () => StreamAppsViewModel.OnUi(RefreshData);
        RefreshData();
    }

    private MeldStudioSettings S => _meld.Settings;

    public string Host
    {
        get => S.Host;
        set { S.Host = value.Trim(); _meld.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? Port
    {
        get => S.Port;
        set { S.Port = (int)(value ?? 13376); _meld.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; _meld.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoReconnect
    {
        get => S.AutoReconnect;
        set { S.AutoReconnect = value; _meld.SaveSettings(); OnPropertyChanged(); }
    }

    public string Outputs => !_meld.IsConnected
        ? "—"
        : $"{(_meld.IsStreaming ? "Streaming" : "Not streaming")} · {(_meld.IsRecording ? "Recording" : "Not recording")} · WebChannel API v{_meld.ApiVersion}";

    public string CurrentScene => _meld.IsConnected ? _meld.Session.CurrentScene?.Name ?? "(none)" : "—";

    public ObservableCollection<MeldSceneRowViewModel> Scenes { get; } = [];

    public bool HasNoScenes => Scenes.Count == 0;

    protected override Task StartAsync() => _meld.ConnectAsync();

    protected override Task StopAsync() => _meld.DisconnectAsync();

    [RelayCommand]
    private Task ToggleRecording() => RunAsync(async () =>
        await _meld.SetRecordingAsync(SwitchMode.Toggle) ? "Recording starting..." : "Recording stopping...");

    [RelayCommand]
    private Task ToggleStreaming() => RunAsync(async () =>
        await _meld.SetStreamingAsync(SwitchMode.Toggle) ? "Stream starting..." : "Stream stopping...");

    [RelayCommand]
    private Task Screenshot() => RunAsync(async () =>
    {
        await _meld.TakeScreenshotAsync();
        return "Screenshot taken.";
    });

    internal Task ShowAsync(MeldItem scene) => RunAsync(async () =>
    {
        await _meld.ShowSceneAsync(scene.Id);
        return $"Showing {scene.Name}.";
    });

    private void RefreshData()
    {
        var session = _meld.IsConnected ? _meld.Session : MeldSession.Empty;
        Scenes.Clear();
        foreach (var scene in session.Scenes) Scenes.Add(new MeldSceneRowViewModel(this, scene, session.LayersOf(scene.Id).Count()));
        Refresh();
    }
}

public sealed partial class MeldSceneRowViewModel(MeldCardViewModel card, MeldItem scene, int layerCount) : ObservableObject
{
    public string Name => scene.Name;
    public string Details => $"{(scene.Current ? "live · " : "")}{layerCount} layer{(layerCount == 1 ? "" : "s")}{(scene.Staged ? " · staged" : "")}";
    public bool IsCurrent => scene.Current;

    [RelayCommand]
    private Task Show() => card.ShowAsync(scene);
}

public sealed partial class PolyPopCardViewModel : StreamAppCardViewModel
{
    private readonly PolyPopService _polyPop;

    public PolyPopCardViewModel(PolyPopService polyPop) : base(polyPop) => _polyPop = polyPop;

    private PolyPopSettings S => _polyPop.Settings;

    public IReadOnlyList<string> Modes { get; } = ["Cuebot listens (plugin connects)", "Cuebot connects to the plugin"];

    public int ModeIndex
    {
        get => S.Mode == PolyPopMode.Server ? 0 : 1;
        set { S.Mode = value == 1 ? PolyPopMode.Client : PolyPopMode.Server; _polyPop.SaveSettings(); OnPropertyChanged(); }
    }

    public string Address
    {
        get => S.Address;
        set { S.Address = value.Trim(); _polyPop.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? Port
    {
        get => S.Port;
        set { S.Port = (int)(value ?? 9652); _polyPop.SaveSettings(); OnPropertyChanged(); }
    }

    public string Endpoint
    {
        get => S.Endpoint;
        set { S.Endpoint = value.Trim(); _polyPop.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoStart
    {
        get => S.AutoStart;
        set { S.AutoStart = value; _polyPop.SaveSettings(); OnPropertyChanged(); }
    }

    public string LastMessage => _polyPop.LastMessage is { } m ? $"{m.Type} {m.Name} {m.Data}".Trim() : "—";

    [ObservableProperty] private string _testAlert = "Alert";
    [ObservableProperty] private string _testVariables = "username=Cuebot";

    protected override Task StartAsync() => _polyPop.ConnectAsync();

    protected override Task StopAsync() => _polyPop.DisconnectAsync();

    [RelayCommand]
    private Task SendTest() => RunAsync(async () =>
    {
        var reached = await _polyPop.TriggerAlertAsync(TestAlert, PolyPopService.ParseVariables(TestVariables));
        return $"Alert \"{TestAlert}\" sent to {reached} PolyPop plugin{(reached == 1 ? "" : "s")}.";
    });
}
