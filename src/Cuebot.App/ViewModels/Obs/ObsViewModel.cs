using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.App.ViewModels.Obs;
using Cuebot.Obs;

namespace Cuebot.App.ViewModels;

/// <summary>The OBS page: every OBS connection (settings, status, scenes and sources with quick toggles) plus Streamlabs Desktop.</summary>
public sealed partial class ObsViewModel : PageViewModel
{
    private readonly ObsService _obs;

    public ObsViewModel(ObsService obs) : base("OBS")
    {
        _obs = obs;
        foreach (var connection in obs.Connections) Connections.Add(new ObsConnectionViewModel(obs, connection, this));
        _selectedConnection = Connections[0];
        if (obs.StreamlabsDesktop is { } slobs) Streamlabs = new StreamlabsViewModel(slobs);
        obs.ConnectionsChanged += () => OnUi(SyncConnections);
    }

    public ObservableCollection<ObsConnectionViewModel> Connections { get; } = [];

    [ObservableProperty] private ObsConnectionViewModel _selectedConnection;
    [ObservableProperty] private string _error = "";

    /// <summary>Null when Streamlabs Desktop is not registered.</summary>
    public StreamlabsViewModel? Streamlabs { get; }

    public bool HasStreamlabs => Streamlabs is not null;

    [RelayCommand]
    private void AddConnection()
    {
        Error = "";
        var connection = _obs.AddConnection($"OBS {_obs.Connections.Count + 1}");
        SyncConnections();
        SelectedConnection = Connections.First(c => c.Connection == connection);
    }

    internal async Task RemoveAsync(ObsConnectionViewModel item)
    {
        Error = "";
        try
        {
            await _obs.RemoveConnectionAsync(item.Connection);
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        SyncConnections();
    }

    private void SyncConnections()
    {
        var current = _obs.Connections;
        foreach (var stale in Connections.Where(vm => !current.Contains(vm.Connection)).ToArray())
        {
            stale.Detach();
            Connections.Remove(stale);
        }
        foreach (var connection in current.Where(c => Connections.All(vm => vm.Connection != c)))
            Connections.Add(new ObsConnectionViewModel(_obs, connection, this));
        foreach (var vm in Connections) vm.RefreshName();
        if (!Connections.Contains(SelectedConnection)) SelectedConnection = Connections[0];
    }
}
