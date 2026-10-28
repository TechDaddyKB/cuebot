using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.StreamApps;
using Cuebot.StreamApps.Lumia;

namespace Cuebot.App.ViewModels.StreamApps;

/// <summary>Lumia Stream tab: address and token, Lumia's states, its commands and a colour test.</summary>
public sealed partial class LumiaViewModel : StreamAppCardViewModel
{
    private readonly LumiaService _lumia;

    public LumiaViewModel(LumiaService lumia) : base(lumia)
    {
        _lumia = lumia;
        lumia.DataChanged += () => StreamAppsViewModel.OnUi(RefreshData);
        RefreshData();
    }

    public string Title => "Lumia Stream";

    private LumiaSettings S => _lumia.Settings;

    public string Host
    {
        get => S.Host;
        set { S.Host = value.Trim(); _lumia.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? Port
    {
        get => S.Port;
        set { S.Port = (int)(value ?? 39231); _lumia.SaveSettings(); OnPropertyChanged(); }
    }

    public string Token
    {
        get => S.Token;
        set { S.Token = value.Trim(); _lumia.SaveSettings(); OnPropertyChanged(); OnPropertyChanged(nameof(HasNoToken)); }
    }

    public bool HasNoToken => !_lumia.HasToken;

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; _lumia.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoReconnect
    {
        get => S.AutoReconnect;
        set { S.AutoReconnect = value; _lumia.SaveSettings(); OnPropertyChanged(); }
    }

    public string States => _lumia.States is { } s
        ? $"Lumia {(s.On ? "on" : "off")} · stream mode {(s.StreamMode ? "on" : "off")} · Fuze {(s.Fuze ? "on" : "off")}"
        : _lumia.IsConnected ? "States arrive with Lumia's next state change." : "—";

    public string Lights => _lumia.IsConnected
        ? _lumia.Info.Lights.Count == 0 ? "No lights reported." : string.Join(", ", _lumia.Info.Lights.Select(l => l.Display))
        : "—";

    [ObservableProperty] private string _testColor = "#FF4076";

    public ObservableCollection<LumiaCommandRowViewModel> Commands { get; } = [];

    public bool HasNoCommands => Commands.Count == 0;

    protected override Task StartAsync() =>
        _lumia.HasToken ? _lumia.ConnectAsync() : throw new InvalidOperationException("Paste the token from Lumia's Settings → API first.");

    protected override Task StopAsync() => _lumia.DisconnectAsync();

    [RelayCommand]
    private Task SetColor() => RunAsync(async () =>
    {
        await _lumia.SetColorAsync(TestColor);
        return $"Sent {TestColor}.";
    });

    [RelayCommand]
    private Task ToDefault() => RunAsync(async () =>
    {
        await _lumia.SetToDefaultAsync();
        return "Lights back to default.";
    });

    [RelayCommand]
    private Task Reload() => RunAsync(async () =>
    {
        var info = await _lumia.RefreshAsync();
        return $"{info.Values("chat-command").Count} commands, {info.Lights.Count} lights.";
    });

    internal Task RunCommandAsync(string command) => RunAsync(async () =>
    {
        await _lumia.SendCommandAsync(command);
        return $"Ran {command}.";
    });

    private void RefreshData()
    {
        Commands.Clear();
        foreach (var command in _lumia.Info.Values("chat-command")) Commands.Add(new LumiaCommandRowViewModel(this, command));
        Refresh();
    }
}

public sealed partial class LumiaCommandRowViewModel(LumiaViewModel owner, string name) : ObservableObject
{
    public string Name => name;

    [RelayCommand]
    private Task Run() => owner.RunCommandAsync(name);
}
