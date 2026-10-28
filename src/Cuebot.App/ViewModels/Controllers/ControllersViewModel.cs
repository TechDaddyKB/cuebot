using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Controllers;

namespace Cuebot.App.ViewModels.Controllers;

/// <summary>
/// Game controllers page: status, settings (watch mode, deadzones, throttle, mapping file), the connected controllers
/// with live buttons/axes/hats, and a "learn" box that shows the trigger settings for the next input. While the page
/// is on screen it holds controller input open (<see cref="ControllerService.Hold"/>) and refreshes ~15 times a second.
/// </summary>
public sealed partial class ControllersViewModel : ObservableObject
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMilliseconds(66);

    private readonly ControllerService _service;
    private readonly DispatcherTimer _clock;
    private IDisposable? _hold;
    private long _seenVersion = -1;
    private CancellationTokenSource? _learn;

    public ControllersViewModel(ControllerService service)
    {
        _service = service;
        _clock = new DispatcherTimer { Interval = RefreshInterval };
        _clock.Tick += (_, _) => RefreshLive();
        service.StatusChanged += () => OnUi(RefreshStatus);
        service.ControllersChanged += () => OnUi(SyncRows);
        SyncRows();
    }

    public string Title => "Controllers";

    public static IReadOnlyList<string> Modes { get; } = Enum.GetNames<ControllerWatchMode>();

    private ControllerSettings S => _service.Settings;

    public string State => _service.State.ToString();
    public string StatusMessage => _service.StatusMessage;
    public string Backend => _service.BackendVersion.Length > 0 ? _service.BackendVersion : _service.BackendName;
    public bool HasError => _service.State == Cuebot.Core.ConnectionState.Error;

    public string Mode
    {
        get => S.Mode.ToString();
        set
        {
            if (!Enum.TryParse<ControllerWatchMode>(value, out var mode) || mode == S.Mode) return;
            S.Mode = mode;
            Save();
            _service.Sync();
        }
    }

    public decimal? StickDeadzone
    {
        get => (decimal)Math.Round(S.StickDeadzone * 100, 1);
        set { S.StickDeadzone = (double)Math.Clamp(value ?? 15, 0, 95) / 100; Save(); }
    }

    public decimal? TriggerDeadzone
    {
        get => (decimal)Math.Round(S.TriggerDeadzone * 100, 1);
        set { S.TriggerDeadzone = (double)Math.Clamp(value ?? 5, 0, 95) / 100; Save(); }
    }

    public decimal? AxisThrottleMs
    {
        get => S.AxisThrottleMs;
        set { S.AxisThrottleMs = (int)Math.Clamp(value ?? 50, 0, 5000); Save(); }
    }

    public decimal? AxisMinChange
    {
        get => (decimal)Math.Round(S.AxisMinChange * 100, 1);
        set { S.AxisMinChange = (double)Math.Clamp(value ?? 2, 0.1m, 50) / 100; Save(); }
    }

    public bool DPadAsHat
    {
        get => S.DPadAsHat;
        set { S.DPadAsHat = value; Save(); }
    }

    public bool IncludeJoysticks
    {
        get => S.IncludeJoysticks;
        set
        {
            if (S.IncludeJoysticks == value) return;
            S.IncludeJoysticks = value;
            Save();
            if (_service.IsRunning) _service.Restart();
        }
    }

    public string MappingsFile
    {
        get => S.MappingsFile;
        set { S.MappingsFile = value?.Trim() ?? ""; Save(); OnPropertyChanged(nameof(MappingsInUse)); }
    }

    public string MappingsInUse => _service.ResolveMappingsFile() is { } file
        ? "Using " + file
        : "Using SDL's built-in mappings (put gamecontrollerdb.txt in the data folder, or enter a path, to add more).";

    public ObservableCollection<ControllerRowViewModel> Rows { get; } = [];

    public bool HasNoControllers => Rows.Count == 0;

    [ObservableProperty] private string _learnText = "";
    [ObservableProperty] private string _learnSettings = "";
    [ObservableProperty] private bool _isLearning;
    [ObservableProperty] private string _info = "";
    [ObservableProperty] private string _error = "";

    public string LearnLabel => IsLearning ? "Cancel" : "Learn";

    partial void OnIsLearningChanged(bool value) => OnPropertyChanged(nameof(LearnLabel));

    /// <summary>The page became visible: watch controllers and refresh live state.</summary>
    public void StartLiveUpdates()
    {
        _hold ??= _service.Hold();
        SyncRows();
        _clock.Start();
    }

    public void StopLiveUpdates()
    {
        _clock.Stop();
        _hold?.Dispose();
        _hold = null;
        _learn?.Cancel();
    }

    [RelayCommand]
    private void Retry()
    {
        Error = "";
        _service.Restart();
    }

    [RelayCommand]
    private async Task ReloadMappings()
    {
        Error = "";
        Info = "";
        try
        {
            var added = await _service.ReloadMappingsAsync();
            Info = _service.IsRunning ? $"Loaded {added} mappings." : "Saved; the mappings load when controller input starts.";
        }
        catch (Exception ex) { Error = ex.Message; }
        OnPropertyChanged(nameof(MappingsInUse));
    }

    [RelayCommand]
    private async Task Learn()
    {
        if (IsLearning)
        {
            _learn?.Cancel();
            return;
        }
        Error = "";
        IsLearning = true;
        LearnText = "Press a button, move a stick or trigger past half way, or push the D-pad...";
        LearnSettings = "";
        _learn = new CancellationTokenSource();
        try
        {
            var input = await _service.LearnAsync(TimeSpan.FromSeconds(30), "", _learn.Token);
            if (input is null) LearnText = "Nothing was pressed within 30 seconds.";
            else
            {
                LearnText = input.Description;
                LearnSettings = $"Trigger \"{TriggerName(input.TriggerType)}\": {input.SettingsText}";
            }
        }
        catch (OperationCanceledException) { LearnText = ""; }
        catch (Exception ex)
        {
            LearnText = "";
            Error = ex.Message;
        }
        finally
        {
            IsLearning = false;
            _learn?.Dispose();
            _learn = null;
        }
    }

    private static string TriggerName(string type) => type switch
    {
        ControllerEvents.Button => "Controller Button",
        ControllerEvents.Axis => "Controller Axis",
        ControllerEvents.Hat => "Controller Hat / D-Pad",
        _ => type,
    };

    private void Save()
    {
        _service.SaveSettings();
        OnPropertyChanged(string.Empty);
    }

    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(Backend));
        OnPropertyChanged(nameof(HasError));
    }

    internal void SyncRows()
    {
        var list = _service.Controllers;
        for (var i = Rows.Count - 1; i >= 0; i--)
            if (list.All(c => c.Device.Id != Rows[i].Id)) Rows.RemoveAt(i);
        foreach (var snapshot in list)
        {
            var row = Rows.FirstOrDefault(r => r.Id == snapshot.Device.Id);
            if (row is null) Rows.Add(new ControllerRowViewModel(snapshot));
            else row.Update(snapshot);
        }
        OnPropertyChanged(nameof(HasNoControllers));
        RefreshStatus();
    }

    internal void RefreshLive()
    {
        var version = _service.InputVersion;
        if (version == _seenVersion) return;
        _seenVersion = version;
        foreach (var snapshot in _service.Controllers)
            Rows.FirstOrDefault(r => r.Id == snapshot.Device.Id)?.Update(snapshot);
    }

    internal static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}

/// <summary>One connected controller with its live inputs.</summary>
public sealed partial class ControllerRowViewModel : ObservableObject
{
    public ControllerRowViewModel(ControllerSnapshot snapshot)
    {
        Id = snapshot.Device.Id;
        Update(snapshot);
    }

    public uint Id { get; }

    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _details = "";
    [ObservableProperty] private string _identity = "";
    [ObservableProperty] private string _held = "";
    [ObservableProperty] private string _hats = "";

    public ObservableCollection<ButtonStateViewModel> Buttons { get; } = [];
    public ObservableCollection<AxisStateViewModel> Axes { get; } = [];

    public void Update(ControllerSnapshot c)
    {
        var d = c.Device;
        Name = c.DisplayName;
        var battery = d.BatteryPercent >= 0 ? $", battery {d.BatteryPercent} % ({d.Power})"
            : d.Power is not Cuebot.Controllers.Device.ControllerPower.Unknown ? $", {d.Power}" : "";
        var extras = string.Join(", ", new[] { d.CanRumble ? "rumble" : null, d.HasRgbLed ? "RGB light" : null }.Where(x => x is not null));
        Details = $"{d.Type}{(d.IsGamepad ? "" : $" ({d.Buttons} buttons, {d.Axes} axes, {d.Hats} hats)")}{battery}"
                  + (extras.Length > 0 ? $"; {extras}" : "");
        Identity = string.Create(CultureInfo.InvariantCulture, $"GUID {d.Guid}  ·  USB {d.VendorId:x4}:{d.ProductId:x4}");
        Held = c.HeldButtons.Count == 0 ? "No buttons held" : "Held: " + string.Join(" + ", c.HeldButtons);
        Hats = c.Hats.Count == 0 ? ""
            : string.Join("   ", c.Hats.Select((h, i) => $"{(d.IsGamepad ? "D-pad" : ControllerNames.Hat(i))}: {ControllerNames.HatDirection(h)}"));

        var names = d.IsGamepad ? ControllerNames.GamepadButtons.Take(21).ToArray()
            : Enumerable.Range(0, Math.Max(d.Buttons, c.HeldButtons.Count)).Select(i => ControllerNames.Button(false, i)).ToArray();
        if (Buttons.Count != names.Length || Buttons.Select(b => b.Name).Where((n, i) => n != names[i]).Any())
        {
            Buttons.Clear();
            foreach (var n in names) Buttons.Add(new ButtonStateViewModel(n, ControllerNames.ButtonLabel(d.Type, n)));
        }
        foreach (var b in Buttons) b.IsPressed = c.HeldButtons.Contains(b.Name);

        if (Axes.Count != c.Axes.Count)
        {
            Axes.Clear();
            foreach (var a in c.Axes) Axes.Add(new AxisStateViewModel(a.Name));
        }
        for (var i = 0; i < c.Axes.Count; i++) Axes[i].Value = c.Axes[i].Value;
    }
}

public sealed partial class ButtonStateViewModel(string name, string label) : ObservableObject
{
    public string Name { get; } = name;

    /// <summary>The printed label when it differs ("South (A)"), else the name.</summary>
    public string Text { get; } = string.Equals(label, name, StringComparison.OrdinalIgnoreCase) ? name : $"{name} ({label})";

    [ObservableProperty] private bool _isPressed;
}

public sealed partial class AxisStateViewModel(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent), nameof(Text))]
    private double _value;

    /// <summary>-100..100 for the bar.</summary>
    public double Percent => Math.Round(Value * 100);

    public string Text => string.Create(CultureInfo.InvariantCulture, $"{Value:+0.00;-0.00;0.00}");
}
