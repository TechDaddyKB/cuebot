using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Obs;

namespace Cuebot.App.ViewModels;

public sealed partial class ObsViewModel : PageViewModel
{
    private readonly ObsService _obs;

    public ObsViewModel(ObsService obs) : base("OBS")
    {
        _obs = obs;
        obs.StatusChanged += () => OnUi(() => OnPropertyChanged(string.Empty));
        obs.ScenesChanged += () => OnUi(() =>
        {
            OnPropertyChanged(nameof(Scenes));
            OnPropertyChanged(nameof(CurrentScene));
        });
    }

    public string Host
    {
        get => _obs.Settings.Host;
        set { _obs.Settings.Host = value.Trim(); _obs.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal Port
    {
        get => _obs.Settings.Port;
        set { _obs.Settings.Port = (int)value; _obs.SaveSettings(); OnPropertyChanged(); }
    }

    public string Password
    {
        get => _obs.Settings.Password;
        set { _obs.Settings.Password = value; _obs.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => _obs.Settings.AutoConnect;
        set { _obs.Settings.AutoConnect = value; _obs.SaveSettings(); OnPropertyChanged(); }
    }

    public string State => _obs.State.ToString();
    public string StatusMessage => _obs.StatusMessage;
    public IReadOnlyList<string> Scenes => _obs.Scenes;
    public string CurrentScene => _obs.CurrentScene ?? "—";

    [ObservableProperty] private string _error = "";

    [RelayCommand]
    private Task Connect() => Run(() => _obs.ConnectAsync());

    [RelayCommand]
    private Task Disconnect() => Run(_obs.DisconnectAsync);

    [RelayCommand]
    private Task SwitchScene(string scene) => Run(() => _obs.RequestAsync("SetCurrentProgramScene", new { sceneName = scene }));

    private async Task Run(Func<Task> operation)
    {
        Error = "";
        try { await operation(); }
        catch (Exception ex) { Error = ex.Message; }
    }
}
