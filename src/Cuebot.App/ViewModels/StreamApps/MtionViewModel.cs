using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.StreamApps;
using Cuebot.StreamApps.Mtion;

namespace Cuebot.App.ViewModels.StreamApps;

/// <summary>mtion tab: the local developer API's address, the open clubhouse and its external triggers (each can be fired).</summary>
public sealed partial class MtionViewModel : StreamAppCardViewModel
{
    private readonly MtionService _mtion;

    public MtionViewModel(MtionService mtion) : base(mtion)
    {
        _mtion = mtion;
        mtion.DataChanged += () => StreamAppsViewModel.OnUi(RefreshData);
        RefreshData();
    }

    private MtionSettings S => _mtion.Settings;

    public string Host
    {
        get => S.Host;
        set { S.Host = value.Trim(); _mtion.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? Port
    {
        get => S.Port;
        set { S.Port = (int)(value ?? 35393); _mtion.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? PollSeconds
    {
        get => S.PollSeconds;
        set { S.PollSeconds = (int)(value ?? 5); _mtion.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; _mtion.SaveSettings(); OnPropertyChanged(); }
    }

    public string Clubhouse => _mtion.Clubhouse is { } c ? c.Name : _mtion.IsConnected ? "(no clubhouse open)" : "—";

    public ObservableCollection<MtionTriggerRowViewModel> Triggers { get; } = [];

    public bool HasNoTriggers => Triggers.Count == 0;

    protected override Task StartAsync() => _mtion.ConnectAsync();

    protected override Task StopAsync() => _mtion.DisconnectAsync();

    [RelayCommand]
    private Task Check() => RunAsync(async () =>
    {
        var (running, clubhouse) = await _mtion.GetActiveClubhouseAsync();
        RefreshData();
        return !running ? $"mtion studio is not answering at {_mtion.BaseUrl}." : clubhouse is null ? "mtion studio is running; no clubhouse is open." : $"Open clubhouse: {clubhouse.Name}.";
    });

    internal Task FireAsync(MtionTrigger trigger) => RunAsync(async () =>
    {
        var result = await _mtion.FireTriggerAsync(trigger.Id, new Dictionary<string, string>());
        return result switch
        {
            { Running: false } => "mtion studio is not running.",
            { Found: false } => $"\"{trigger.Name}\" is not in the open clubhouse any more.",
            { Sent: true } => $"Fired \"{trigger.Name}\".",
            _ => $"mtion did not fire \"{trigger.Name}\".",
        };
    });

    private void RefreshData()
    {
        Triggers.Clear();
        foreach (var t in _mtion.Clubhouse?.Triggers ?? []) Triggers.Add(new MtionTriggerRowViewModel(this, t));
        Refresh();
    }
}

public sealed partial class MtionTriggerRowViewModel(MtionViewModel card, MtionTrigger trigger) : ObservableObject
{
    public string Name => trigger.Name;

    public string Details => trigger.Parameters.Count == 0
        ? "no parameters"
        : string.Join(", ", trigger.Parameters.Select(p => $"{p.Name} ({p.DataType})"));

    [RelayCommand]
    private Task Fire() => card.FireAsync(trigger);
}
