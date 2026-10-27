using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Speech;

namespace Cuebot.App.ViewModels.Speech;

/// <summary>One VoiceGate profile: microphone, threshold and release delay.</summary>
public sealed partial class VoiceGateProfileViewModel : ObservableObject
{
    private readonly SpeechGateViewModel _owner;

    public VoiceGateProfileViewModel(VoiceGateProfile profile, SpeechGateViewModel owner)
    {
        Profile = profile;
        _owner = owner;
    }

    public VoiceGateProfile Profile { get; }

    public string Name
    {
        get => Profile.Name;
        set
        {
            var name = (value ?? "").Trim();
            if (name == Profile.Name) return;
            _owner.Rename(this, name);
            OnPropertyChanged();
        }
    }

    public Choice? Device
    {
        get => SpeechPageContext.Find(_owner.InputDevices, Profile.DeviceId);
        set
        {
            if (value is null || value.Value == Profile.DeviceId) return;
            Profile.DeviceId = value.Value;
            _owner.OnProfileChanged(this);
            OnPropertyChanged();
        }
    }

    public double ThresholdDb
    {
        get => Profile.ThresholdDb;
        set
        {
            var db = Math.Round(Math.Clamp(value, -96, 0));
            if (Math.Abs(db - Profile.ThresholdDb) < 0.01) return;
            Profile.ThresholdDb = db;
            _owner.OnProfileChanged(this);
            OnPropertyChanged();
        }
    }

    public decimal ReleaseMilliseconds
    {
        get => Profile.ReleaseMilliseconds;
        set
        {
            var ms = SpeechText.Clamp(value, 0, 60_000);
            if (ms == Profile.ReleaseMilliseconds) return;
            Profile.ReleaseMilliseconds = ms;
            _owner.OnProfileChanged(this);
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<Choice> InputDevices => _owner.InputDevices;

    [RelayCommand]
    private void RefreshDevices() => _owner.RefreshDevicesCommand.Execute(null);

    public void RefreshDevice()
    {
        OnPropertyChanged(nameof(InputDevices));
        OnPropertyChanged(nameof(Device));
    }

    public override string ToString() => Name;
}

/// <summary>VoiceGate &amp; queue tab: VoiceGate profiles with a live level meter, queue limits and save-to-file.</summary>
public sealed partial class SpeechGateViewModel : ObservableObject, IDisposable
{
    public const double MeterFloorDb = -96;

    private readonly SpeechPageContext _ctx;
    private readonly DispatcherTimer _reapply;
    private IDisposable? _monitor;

    public SpeechGateViewModel(SpeechPageContext ctx)
    {
        _ctx = ctx;
        InputDevices = ctx.InputDeviceChoices();
        Profiles = new ObservableCollection<VoiceGateProfileViewModel>(Gate.Profiles.Select(p => new VoiceGateProfileViewModel(p, this)));
        _selected = Profiles.FirstOrDefault();
        ActiveChoices = BuildActiveChoices();
        // Re-activating restarts the microphone monitor; wait until slider drags settle.
        _reapply = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _reapply.Tick += (_, _) =>
        {
            _reapply.Stop();
            if (Gate.ActiveProfile.Length > 0) _ctx.Speech.ActivateVoiceGateProfile(Gate.ActiveProfile);
        };
    }

    private VoiceGateSettings Gate => _ctx.Settings.VoiceGate;
    private SpeechQueueSettings Queue => _ctx.Settings.Queue;

    public bool IsAvailable => _ctx.Speech.VoiceGateAvailable && _ctx.AudioInput is not null;

    public bool IsListening => IsAvailable && Gate.ActiveProfile.Length > 0;

    public string AvailabilityText =>
        !IsAvailable ? "No audio input backend is available, so VoiceGate cannot listen to a microphone."
        : Gate.ActiveProfile.Length == 0 ? "Off. Pick an active profile to hold speech while you talk."
        : _ctx.Speech.VoiceGateHolding ? "Holding speech — the microphone is above the threshold."
        : $"Listening with \"{Gate.ActiveProfile}\".";

    public bool IsHolding => _ctx.Speech.VoiceGateHolding;

    public IReadOnlyList<Choice> InputDevices { get; private set; }

    public ObservableCollection<VoiceGateProfileViewModel> Profiles { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private VoiceGateProfileViewModel? _selected;

    public bool HasSelection => Selected is not null;

    public IReadOnlyList<Choice> ActiveChoices { get; private set; }

    public Choice? ActiveProfile
    {
        get => SpeechPageContext.Find(ActiveChoices, Gate.ActiveProfile);
        set
        {
            if (value is null || value.Value == Gate.ActiveProfile) return;
            _ctx.Speech.ActivateVoiceGateProfile(value.Value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AvailabilityText));
        OnPropertyChanged(nameof(IsListening));
        }
    }

    // Live meter
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(LevelText), nameof(IsAboveThreshold))]
    private double _levelDb = MeterFloorDb;

    public string LevelText => IsMonitoring ? $"{LevelDb:0} dBFS" : "Meter off";

    public bool IsAboveThreshold => IsMonitoring && Selected is { } p && LevelDb > p.ThresholdDb;

    public bool IsMonitoring
    {
        get => _monitor is not null;
        set
        {
            if (value == IsMonitoring) return;
            if (value) StartMonitor();
            else StopMonitor();
            OnPropertyChanged();
            OnPropertyChanged(nameof(LevelText));
            OnPropertyChanged(nameof(IsAboveThreshold));
        }
    }

    [ObservableProperty] private string _monitorError = "";

    // Queue
    public decimal MaxQueueLength { get => Queue.MaxLength; set => Set(Queue.MaxLength, SpeechText.Clamp(value, 0, 10_000), v => Queue.MaxLength = v); }
    public IReadOnlyList<QueueDropPolicy> DropPolicies { get; } = Enum.GetValues<QueueDropPolicy>();
    public QueueDropPolicy DropPolicy { get => Queue.DropPolicy; set => Set(Queue.DropPolicy, value, v => Queue.DropPolicy = v); }
    public string SaveFolder { get => Queue.SaveFolder; set => Set(Queue.SaveFolder, (value ?? "").Trim(), v => Queue.SaveFolder = v); }

    [RelayCommand]
    private void Add()
    {
        var profile = new VoiceGateProfile { Name = SpeechText.UniqueName("Microphone", Profiles.Select(p => p.Name)) };
        Gate.Profiles.Add(profile);
        _ctx.Save();
        var vm = new VoiceGateProfileViewModel(profile, this);
        Profiles.Add(vm);
        Selected = vm;
        RefreshActiveChoices();
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (Selected is not { } profile) return;
        if (string.Equals(Gate.ActiveProfile, profile.Name, StringComparison.OrdinalIgnoreCase)) _ctx.Speech.ActivateVoiceGateProfile(null);
        Gate.Profiles.Remove(profile.Profile);
        _ctx.Save();
        Profiles.Remove(profile);
        Selected = Profiles.FirstOrDefault();
        RefreshActiveChoices();
    }

    [RelayCommand]
    private void RefreshDevices()
    {
        InputDevices = _ctx.InputDeviceChoices();
        OnPropertyChanged(nameof(InputDevices));
        foreach (var profile in Profiles) profile.RefreshDevice();
    }

    public void Refresh()
    {
        OnPropertyChanged(nameof(IsHolding));
        OnPropertyChanged(nameof(AvailabilityText));
        OnPropertyChanged(nameof(IsListening));
        OnPropertyChanged(nameof(ActiveProfile));
    }

    internal void Rename(VoiceGateProfileViewModel profile, string name)
    {
        var wasActive = string.Equals(Gate.ActiveProfile, profile.Profile.Name, StringComparison.OrdinalIgnoreCase);
        profile.Profile.Name = name;
        if (wasActive) Gate.ActiveProfile = name;
        _ctx.Save();
        RefreshActiveChoices();
    }

    internal void OnProfileChanged(VoiceGateProfileViewModel profile)
    {
        _ctx.Save();
        OnPropertyChanged(nameof(IsAboveThreshold));
        if (ReferenceEquals(profile, Selected) && IsMonitoring)
        {
            StopMonitor();
            StartMonitor();
        }
        if (string.Equals(Gate.ActiveProfile, profile.Profile.Name, StringComparison.OrdinalIgnoreCase))
        {
            _reapply.Stop();
            _reapply.Start();
        }
    }

    partial void OnSelectedChanged(VoiceGateProfileViewModel? value)
    {
        if (!IsMonitoring) return;
        StopMonitor();
        if (value is not null) StartMonitor();
        OnPropertyChanged(nameof(IsMonitoring));
        OnPropertyChanged(nameof(LevelText));
    }

    private void StartMonitor()
    {
        MonitorError = "";
        if (_ctx.AudioInput is not { } input)
        {
            MonitorError = "No audio input is available.";
            return;
        }
        var device = Selected?.Profile.DeviceId;
        try
        {
            _monitor = input.MonitorLevel(string.IsNullOrEmpty(device) ? null : device,
                db => Dispatcher.UIThread.Post(() => { if (_monitor is not null) LevelDb = Math.Clamp(db, MeterFloorDb, 0); }));
        }
        catch (Exception ex)
        {
            MonitorError = $"Could not open the microphone: {ex.Message}";
            _monitor = null;
        }
    }

    private void StopMonitor()
    {
        _monitor?.Dispose();
        _monitor = null;
        LevelDb = MeterFloorDb;
    }

    public void Dispose()
    {
        _reapply.Stop();
        StopMonitor();
    }

    private IReadOnlyList<Choice> BuildActiveChoices() =>
    [
        new Choice("", "Off"),
        .. Gate.Profiles.Where(p => p.Name.Length > 0).Select(p => new Choice(p.Name, p.Name)),
    ];

    private void RefreshActiveChoices()
    {
        ActiveChoices = BuildActiveChoices();
        OnPropertyChanged(nameof(ActiveChoices));
        OnPropertyChanged(nameof(ActiveProfile));
    }

    private void Set<T>(T current, T value, Action<T> set, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        set(value);
        _ctx.Save();
        OnPropertyChanged(name);
    }
}
