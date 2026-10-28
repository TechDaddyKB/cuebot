using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core;
using Cuebot.Remote;

namespace Cuebot.App.ViewModels.Servers;

/// <summary>
/// "Remote instances" card on the Servers page: this instance's name, the other Cuebot instances it connects to (host,
/// port, their API token) with live status, and the instances connected to this one. Edits are saved at once;
/// Connect applies them.
/// </summary>
public sealed partial class RemoteInstancesViewModel : ObservableObject
{
    private readonly RemoteHub _hub;

    public RemoteInstancesViewModel(RemoteHub hub)
    {
        _hub = hub;
        foreach (var definition in hub.Settings.Instances) Instances.Add(new RemoteInstanceViewModel(this, definition));
        hub.StatusChanged += () => OnUi(Refresh);
        Refresh();
    }

    internal RemoteHub Hub => _hub;

    internal void Save() => _hub.Save();

    /// <summary>The name other instances see (empty = the computer name).</summary>
    public string InstanceName
    {
        get => _hub.Settings.InstanceName;
        set { _hub.Settings.InstanceName = value.Trim(); Save(); OnPropertyChanged(); OnPropertyChanged(nameof(SelfName)); }
    }

    public string SelfName => _hub.SelfName;

    public bool ReleaseLocalWaiters
    {
        get => _hub.Settings.ReleaseLocalWaiters;
        set { _hub.Settings.ReleaseLocalWaiters = value; Save(); OnPropertyChanged(); }
    }

    public string AcceptHint => _hub.AcceptsConnections
        ? "Other instances can connect to this one: give them this computer's address, the REST API port and the API token (REST API card above)."
        : "Other instances cannot connect to this one while the REST API (above) is off. Links you add below still work both ways.";

    public string StatusMessage => _hub.StatusMessage;

    public ObservableCollection<RemoteInstanceViewModel> Instances { get; } = [];
    public ObservableCollection<string> Incoming { get; } = [];
    public bool HasNoIncoming => Incoming.Count == 0;

    [ObservableProperty] private string _message = "";

    [RelayCommand]
    private void AddInstance()
    {
        var definition = _hub.AddInstance();
        Instances.Add(new RemoteInstanceViewModel(this, definition));
    }

    internal async Task RemoveAsync(RemoteInstanceViewModel item)
    {
        await _hub.RemoveInstanceAsync(item.Definition.Id);
        Instances.Remove(item);
        Message = $"Removed '{item.Name}'.";
    }

    internal void Refresh()
    {
        var statuses = _hub.Instances;
        foreach (var item in Instances) item.Refresh(statuses.FirstOrDefault(s => s.DefinitionId == item.Definition.Id));
        Incoming.Clear();
        foreach (var s in statuses.Where(s => !s.Outgoing))
            Incoming.Add($"{s.Name}  ·  {s.Endpoint}  ·  since {s.ConnectedAt:HH:mm:ss}  ·  {s.Peer?.Os}, Cuebot {s.Peer?.Version}");
        OnPropertyChanged(nameof(HasNoIncoming));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(AcceptHint));
    }

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}

/// <summary>One instance in remote.json. The token is only ever shown masked.</summary>
public sealed partial class RemoteInstanceViewModel(RemoteInstancesViewModel owner, RemoteInstanceDefinition definition) : ObservableObject
{
    internal RemoteInstanceDefinition Definition => definition;

    public string Name { get => definition.Name; set { definition.Name = value.Trim(); Changed(); } }
    public string Host { get => definition.Host; set { definition.Host = value.Trim(); Changed(); } }
    public decimal Port { get => definition.Port; set { definition.Port = (int)value; Changed(); } }
    public string Token { get => definition.Token; set { definition.Token = value.Trim(); Changed(); } }
    public bool Enabled { get => definition.Enabled; set { definition.Enabled = value; Changed(); } }

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _isError;
    [ObservableProperty] private string _statusText = "Not connected";
    [ObservableProperty] private bool _hasPendingChanges;

    private void Changed([System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        owner.Save();
        if (owner.Hub.IsStarted(definition.Id)) HasPendingChanges = true;
        OnPropertyChanged(property);
    }

    internal void Refresh(RemoteInstanceStatus? status)
    {
        IsConnected = status?.State == ConnectionState.Connected;
        IsError = status?.State == ConnectionState.Error;
        StatusText = status?.Message ?? "Not connected";
    }

    /// <summary>Connects (or reconnects with the edited settings).</summary>
    [RelayCommand]
    private async Task Connect()
    {
        HasPendingChanges = false;
        try
        {
            await owner.Hub.ConnectAsync(definition);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        owner.Refresh();
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        HasPendingChanges = false;
        await owner.Hub.DisconnectAsync(definition.Id);
        owner.Refresh();
    }

    [RelayCommand]
    private Task Remove() => owner.RemoveAsync(this);
}
