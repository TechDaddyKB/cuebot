using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.StreamApps;
using Cuebot.StreamApps.Tits;

namespace Cuebot.App.ViewModels.StreamApps;

/// <summary>T.I.T.S. tab: connection settings, the item and trigger lists with test buttons.</summary>
public sealed partial class TitsViewModel : StreamAppCardViewModel
{
    private readonly TitsService _tits;

    public TitsViewModel(TitsService tits) : base(tits)
    {
        _tits = tits;
        tits.DataChanged += () => StreamAppsViewModel.OnUi(RefreshData);
        RefreshData();
    }

    public string Title => "T.I.T.S.";

    private TitsSettings S => _tits.Settings;

    public string Host
    {
        get => S.Host;
        set { S.Host = value.Trim(); _tits.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? Port
    {
        get => S.Port;
        set { S.Port = (int)(value ?? 42069); _tits.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; _tits.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoReconnect
    {
        get => S.AutoReconnect;
        set { S.AutoReconnect = value; _tits.SaveSettings(); OnPropertyChanged(); }
    }

    public string Sockets => !_tits.IsConnected
        ? "—"
        : $"Requests (/websocket) connected · events (/events) {(_tits.EventsConnected ? "connected" : "not connected yet")}";

    [ObservableProperty] private decimal? _throwAmount = 1;

    public ObservableCollection<TitsRowViewModel> Items { get; } = [];
    public ObservableCollection<TitsRowViewModel> Triggers { get; } = [];

    public bool HasNoItems => Items.Count == 0;
    public bool HasNoTriggers => Triggers.Count == 0;

    protected override Task StartAsync() => _tits.ConnectAsync();

    protected override Task StopAsync() => _tits.DisconnectAsync();

    [RelayCommand]
    private Task Reload() => RunAsync(async () =>
    {
        await _tits.RefreshAsync();
        return $"{_tits.Items.Count} items, {_tits.Triggers.Count} triggers.";
    });

    internal Task ThrowAsync(TitsEntry item) => RunAsync(async () =>
    {
        var thrown = await _tits.ThrowItemsAsync([item.Id], 0.05, (int)Math.Max(1, ThrowAmount ?? 1));
        return $"Threw {thrown} × {item.Name}.";
    });

    internal Task ActivateAsync(TitsEntry trigger) => RunAsync(async () =>
    {
        await _tits.ActivateTriggerAsync(trigger.Id);
        return $"Activated {trigger.Name}.";
    });

    private void RefreshData()
    {
        Items.Clear();
        foreach (var item in _tits.Items) Items.Add(new TitsRowViewModel(item, "Throw", () => ThrowAsync(item)));
        Triggers.Clear();
        foreach (var trigger in _tits.Triggers) Triggers.Add(new TitsRowViewModel(trigger, "Activate", () => ActivateAsync(trigger)));
        Refresh();
    }
}

public sealed partial class TitsRowViewModel(TitsEntry entry, string actionLabel, Func<Task> run) : ObservableObject
{
    public string Name => entry.Name;
    public string Id => entry.Id;
    public string ActionLabel => actionLabel;

    [RelayCommand]
    private Task Run() => run();
}
