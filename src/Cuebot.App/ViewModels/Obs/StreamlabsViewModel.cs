using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core;
using Cuebot.Obs;
using Cuebot.Obs.Streamlabs;

namespace Cuebot.App.ViewModels.Obs;

/// <summary>The Streamlabs Desktop card on the OBS page.</summary>
public sealed partial class StreamlabsViewModel : ObservableObject
{
    private readonly StreamlabsDesktopService _slobs;

    public StreamlabsViewModel(StreamlabsDesktopService slobs)
    {
        _slobs = slobs;
        slobs.StatusChanged += () => OnUi(() => OnPropertyChanged(string.Empty));
        slobs.ScenesChanged += () => OnUi(() =>
        {
            OnPropertyChanged(nameof(Scenes));
            OnPropertyChanged(nameof(CurrentScene));
        });
    }

    public string Host
    {
        get => _slobs.Settings.Host;
        set { _slobs.Settings.Host = value.Trim(); _slobs.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal Port
    {
        get => _slobs.Settings.Port;
        set { _slobs.Settings.Port = (int)value; _slobs.SaveSettings(); OnPropertyChanged(); }
    }

    public string Token
    {
        get => _slobs.Settings.Token;
        set { _slobs.Settings.Token = value.Trim(); _slobs.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => _slobs.Settings.AutoConnect;
        set { _slobs.Settings.AutoConnect = value; _slobs.SaveSettings(); OnPropertyChanged(); }
    }

    public string State => _slobs.State.ToString();
    public string StatusMessage => _slobs.StatusMessage;
    public bool IsGood => _slobs.State == ConnectionState.Connected;
    public bool IsBad => _slobs.State == ConnectionState.Error;
    public IReadOnlyList<string> Scenes => _slobs.SceneNames;
    public string CurrentScene => _slobs.CurrentScene ?? "—";

    [ObservableProperty] private string _error = "";

    [RelayCommand]
    private Task Connect() => Run(() => _slobs.ConnectAsync());

    [RelayCommand]
    private Task Disconnect() => Run(_slobs.DisconnectAsync);

    [RelayCommand]
    private Task SwitchScene(string scene) => Run(() => _slobs.SetSceneAsync(scene));

    [RelayCommand]
    private Task ToggleStream() => Run(() => _slobs.ControlStreamingAsync(OutputCommand.Toggle));

    [RelayCommand]
    private Task ToggleRecord() => Run(() => _slobs.ControlRecordingAsync(OutputCommand.Toggle));

    private async Task Run(Func<Task> operation)
    {
        Error = "";
        try { await operation(); }
        catch (Exception ex) { Error = ex.Message; }
    }

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}
