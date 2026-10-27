using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core;
using Cuebot.Obs;
using Cuebot.Obs.Protocol;

namespace Cuebot.App.ViewModels.Obs;

/// <summary>One OBS connection on the OBS page: settings, status, scenes with their sources, and audio inputs.</summary>
public sealed partial class ObsConnectionViewModel : ObservableObject
{
    private readonly ObsService _obs;
    private readonly ObsViewModel _page;
    private readonly Action _onStatus;
    private readonly Action _onScenes;
    private readonly Action<ObsEvent> _onEvent;
    private int _loadVersion;
    private int _audioVersion;
    private IReadOnlyList<string>? _seenInputs;
    private bool _itemsLoadedWhileConnected;

    public ObsConnectionViewModel(ObsService obs, ObsConnection connection, ObsViewModel page)
    {
        _obs = obs;
        _page = page;
        Connection = connection;
        _name = connection.Name;
        _onStatus = () => OnUi(() =>
        {
            OnPropertyChanged(nameof(State));
            OnPropertyChanged(nameof(StatusMessage));
            OnPropertyChanged(nameof(IsConnected));
            OnPropertyChanged(nameof(IsGood));
            OnPropertyChanged(nameof(IsBad));
        });
        _onScenes = () => OnUi(SyncScenes);
        _onEvent = e => OnUi(() => OnObsEvent(e));
        connection.StatusChanged += _onStatus;
        connection.ScenesChanged += _onScenes;
        connection.EventReceived += _onEvent;
        SyncScenes();
    }

    public ObsConnection Connection { get; }
    public bool IsDefault => Connection.IsDefault;
    public bool CanRemove => !Connection.IsDefault;

    // ---- Settings ----

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _nameError = "";

    partial void OnNameChanged(string value)
    {
        NameError = "";
        if (string.Equals(value.Trim(), Connection.Name, StringComparison.Ordinal)) return;
        try
        {
            _obs.RenameConnection(Connection, value);
        }
        catch (ArgumentException ex)
        {
            NameError = ex.Message;
        }
    }

    internal void RefreshName()
    {
        if (!string.Equals(Name.Trim(), Connection.Name, StringComparison.Ordinal) && NameError.Length == 0) Name = Connection.Name;
        OnPropertyChanged(nameof(Title));
    }

    public string Title => IsDefault ? $"{Connection.Name} (default)" : Connection.Name;

    public string Host
    {
        get => Connection.Settings.Host;
        set { Connection.Settings.Host = value.Trim(); Connection.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal Port
    {
        get => Connection.Settings.Port;
        set { Connection.Settings.Port = (int)value; Connection.SaveSettings(); OnPropertyChanged(); }
    }

    public string Password
    {
        get => Connection.Settings.Password;
        set { Connection.Settings.Password = value; Connection.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => Connection.Settings.AutoConnect;
        set { Connection.Settings.AutoConnect = value; Connection.SaveSettings(); OnPropertyChanged(); }
    }

    // ---- Status ----

    public string State => Connection.State.ToString();
    public string StatusMessage => Connection.StatusMessage;
    public bool IsConnected => Connection.IsConnected;
    public bool IsGood => Connection.State == ConnectionState.Connected;
    public bool IsBad => Connection.State == ConnectionState.Error;
    public string Version => $"Version: {Connection.Version ?? "—"}";
    public string CurrentScene => Connection.CurrentScene ?? "—";
    public string PreviewScene => Connection.StudioModeEnabled ? Connection.PreviewScene ?? "—" : "studio mode off";
    public bool StudioModeEnabled => Connection.StudioModeEnabled;
    public string Details => $"Profile: {Connection.CurrentProfile ?? "—"} · Scene collection: {Connection.CurrentSceneCollection ?? "—"} · Transition: {Connection.CurrentTransition ?? "—"}";

    [ObservableProperty] private string _error = "";

    // ---- Scenes and sources ----

    public ObservableCollection<ObsSceneViewModel> Scenes { get; } = [];
    public ObservableCollection<ObsSceneItemViewModel> Items { get; } = [];
    public ObservableCollection<ObsAudioViewModel> Audio { get; } = [];

    [ObservableProperty] private ObsSceneViewModel? _selectedScene;

    partial void OnSelectedSceneChanged(ObsSceneViewModel? value) => _ = LoadItemsAsync();

    [RelayCommand]
    private Task Connect() => Run(() => Connection.ConnectAsync());

    [RelayCommand]
    private Task Disconnect() => Run(Connection.DisconnectAsync);

    [RelayCommand]
    private Task Remove() => _page.RemoveAsync(this);

    [RelayCommand]
    private Task SwitchScene(ObsSceneViewModel scene) => Run(() => Connection.SetCurrentSceneAsync(scene.Name));

    [RelayCommand]
    private Task ToggleStudioMode() => Run(() => Connection.SetStudioModeAsync(EnableMode.Toggle));

    [RelayCommand]
    private Task StudioTransition() => Run(() => Connection.TriggerStudioTransitionAsync());

    [RelayCommand]
    private Task ToggleStream() => Run(() => Connection.ControlStreamAsync(OutputCommand.Toggle));

    [RelayCommand]
    private Task ToggleRecord() => Run(() => Connection.ControlRecordAsync(OutputCommand.Toggle));

    [RelayCommand]
    private Task SaveReplay() => Run(async () => Error = $"Replay saved: {await Connection.SaveReplayBufferAsync()}");

    [RelayCommand]
    private async Task Refresh()
    {
        await LoadItemsAsync();
        await RefreshAudioAsync();
    }

    internal Task SetItemVisibleAsync(ObsSceneItemViewModel item, bool visible) =>
        Run(() => Connection.SetSceneItemEnabledAsync(item.SceneName, item.Id, visible));

    internal Task SetMutedAsync(ObsAudioViewModel input, bool muted) =>
        Run(() => Connection.SetInputMuteAsync(input.Name, muted ? MuteMode.Mute : MuteMode.Unmute));

    internal void Detach()
    {
        Connection.StatusChanged -= _onStatus;
        Connection.ScenesChanged -= _onScenes;
        Connection.EventReceived -= _onEvent;
    }

    private void SyncScenes()
    {
        var names = Connection.Scenes;
        foreach (var stale in Scenes.Where(s => !names.Contains(s.Name)).ToArray()) Scenes.Remove(stale);
        for (var i = 0; i < names.Count; i++)
        {
            var existing = Scenes.FirstOrDefault(s => s.Name == names[i]);
            if (existing is null) Scenes.Insert(Math.Min(i, Scenes.Count), new ObsSceneViewModel(names[i]));
            else if (Scenes.IndexOf(existing) != i) Scenes.Move(Scenes.IndexOf(existing), Math.Min(i, Scenes.Count - 1));
        }
        foreach (var scene in Scenes)
        {
            scene.IsCurrent = scene.Name == Connection.CurrentScene;
            scene.IsPreview = Connection.StudioModeEnabled && scene.Name == Connection.PreviewScene;
        }
        SelectedScene ??= Scenes.FirstOrDefault(s => s.IsCurrent) ?? Scenes.FirstOrDefault();
        if (!Connection.IsConnected) _itemsLoadedWhileConnected = false;
        else if (!_itemsLoadedWhileConnected && SelectedScene is not null)
        {
            _itemsLoadedWhileConnected = true;
            _ = LoadItemsAsync();
        }
        if (Connection.IsConnected && !ReferenceEquals(_seenInputs, Connection.Inputs))
        {
            _seenInputs = Connection.Inputs;   // the cache swaps in a new list whenever inputs are reloaded
            _ = RefreshAudioAsync();
        }
        foreach (var property in new[] { nameof(Version), nameof(CurrentScene), nameof(PreviewScene), nameof(StudioModeEnabled), nameof(Details) })
            OnPropertyChanged(property);
    }

    private async Task LoadItemsAsync()
    {
        var version = ++_loadVersion;
        var scene = SelectedScene;
        if (scene is null || !Connection.IsConnected)
        {
            Items.Clear();
            return;
        }
        try
        {
            var loaded = new List<ObsSceneItemViewModel>();
            foreach (var item in await Connection.GetSceneItemsAsync(scene.Name))
            {
                var row = new ObsSceneItemViewModel(this, scene.Name, item, depth: 0);
                loaded.Add(row);
                if (!item.IsGroup) continue;
                foreach (var child in await Connection.GetSceneItemsAsync(item.SourceName, group: true))
                    loaded.Add(new ObsSceneItemViewModel(this, item.SourceName, child, depth: 1));
            }
            if (version != _loadVersion) return;
            Items.Clear();
            foreach (var row in loaded) Items.Add(row);
        }
        catch (Exception ex)
        {
            if (version == _loadVersion) Error = ex.Message;
        }
    }

    private async Task RefreshAudioAsync()
    {
        var version = ++_audioVersion;
        var found = new List<ObsAudioViewModel>();
        foreach (var input in Connection.Inputs.ToArray())
        {
            try
            {
                found.Add(new ObsAudioViewModel(this, input, await Connection.GetInputMuteAsync(input)));
            }
            catch (ObsRequestException)
            {
                // Not an audio input.
            }
            catch (ObsConnectionException)
            {
                return;
            }
        }
        if (version != _audioVersion) return;
        Audio.Clear();
        foreach (var input in found) Audio.Add(input);
    }

    private void OnObsEvent(ObsEvent e)
    {
        switch (e.EventType)
        {
            case "SceneItemEnableStateChanged":
                var scene = e.EventData.TryGetProperty("sceneName", out var s) ? s.GetString() : null;
                var id = e.EventData.TryGetProperty("sceneItemId", out var i) ? i.GetInt32() : -1;
                var visible = e.EventData.TryGetProperty("sceneItemEnabled", out var v) && v.GetBoolean();
                foreach (var item in Items.Where(x => x.SceneName == scene && x.Id == id)) item.SetVisibleFromObs(visible);
                break;
            case "InputMuteStateChanged":
                var input = e.EventData.TryGetProperty("inputName", out var n) ? n.GetString() : null;
                var muted = e.EventData.TryGetProperty("inputMuted", out var m) && m.GetBoolean();
                foreach (var audio in Audio.Where(a => a.Name == input)) audio.SetMutedFromObs(muted);
                break;
            case "SceneItemCreated" or "SceneItemRemoved" or "SceneItemListReindexed":
                _ = LoadItemsAsync();
                break;
            case "InputCreated" or "InputRemoved" or "InputNameChanged":
                _ = RefreshAudioAsync();
                break;
        }
    }

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

/// <summary>A scene button in the scene list.</summary>
public sealed partial class ObsSceneViewModel(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Badge))]
    private bool _isCurrent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Badge))]
    private bool _isPreview;

    public string Badge => IsCurrent ? "live" : IsPreview ? "preview" : "";
}

/// <summary>A source in the selected scene (groups are followed by their children, indented), with a visibility toggle.</summary>
public sealed class ObsSceneItemViewModel : ObservableObject
{
    private readonly ObsConnectionViewModel _owner;
    private bool _visible;

    public ObsSceneItemViewModel(ObsConnectionViewModel owner, string sceneName, ObsSceneItem item, int depth)
    {
        _owner = owner;
        SceneName = sceneName;
        Id = item.Id;
        Name = item.SourceName;
        IsGroup = item.IsGroup;
        Indent = new Avalonia.Thickness(depth * 24, 0, 0, 0);
        _visible = item.Enabled;
    }

    public string SceneName { get; }
    public int Id { get; }
    public string Name { get; }
    public bool IsGroup { get; }
    public Avalonia.Thickness Indent { get; }
    public string Kind => IsGroup ? "group" : "";

    public bool Visible
    {
        get => _visible;
        set
        {
            if (!SetProperty(ref _visible, value)) return;
            _ = _owner.SetItemVisibleAsync(this, value);
        }
    }

    internal void SetVisibleFromObs(bool visible) => SetProperty(ref _visible, visible, nameof(Visible));
}

/// <summary>An audio input with a mute toggle.</summary>
public sealed class ObsAudioViewModel(ObsConnectionViewModel owner, string name, bool muted) : ObservableObject
{
    private bool _muted = muted;

    public string Name { get; } = name;

    public bool Muted
    {
        get => _muted;
        set
        {
            if (!SetProperty(ref _muted, value)) return;
            _ = owner.SetMutedAsync(this, value);
        }
    }

    internal void SetMutedFromObs(bool muted) => SetProperty(ref _muted, muted, nameof(Muted));
}
