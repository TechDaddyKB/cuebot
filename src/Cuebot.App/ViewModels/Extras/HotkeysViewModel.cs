using System.Collections.ObjectModel;
using Avalonia.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Desktop.Hotkeys;
using Cuebot.Core.Desktop.Notifications;
using Cuebot.Core.Storage;

namespace Cuebot.App.ViewModels.Extras;

/// <summary>One hotkey definition; edits save (and re-register) immediately.</summary>
public sealed partial class HotkeyItemViewModel : ObservableObject
{
    private readonly HotkeysViewModel _owner;

    public HotkeyItemViewModel(HotkeysViewModel owner, HotkeyDefinition definition)
    {
        _owner = owner;
        Definition = definition;
    }

    public HotkeyDefinition Definition { get; }

    public string Name
    {
        get => Definition.Name;
        set => Change(Definition.Name, value, v => Definition.Name = v);
    }

    public string Gesture
    {
        get => Definition.Gesture;
        set
        {
            Change(Definition.Gesture, value, v => Definition.Gesture = v);
            OnPropertyChanged(nameof(GestureError));
        }
    }

    public bool Enabled
    {
        get => Definition.Enabled;
        set => Change(Definition.Enabled, value, v => Definition.Enabled = v);
    }

    /// <summary>Action run directly on press (optional; the "Hotkey pressed" trigger works either way).</summary>
    public string? Action
    {
        get => Definition.Action;
        set => Change(Definition.Action, value ?? "", v => Definition.Action = v);
    }

    public IReadOnlyList<string> ActionNames => _owner.ActionNames;

    public string GestureError => Definition.Gesture.Trim().Length > 0 && !HotkeyGesture.TryParse(Definition.Gesture, out _, out var error) ? error : "";

    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string _state = "";
    [ObservableProperty] private bool _registered;

    public string RecordLabel => IsRecording ? "Press keys… (Esc cancels)" : "Record";

    partial void OnIsRecordingChanged(bool value) => OnPropertyChanged(nameof(RecordLabel));

    [RelayCommand]
    private void Record() => _owner.StartRecording(this);

    [RelayCommand]
    private void Test() => _owner.Test(this);

    [RelayCommand]
    private void Remove() => _owner.Remove(this);

    private void Change<T>(T current, T value, Action<T> assign, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        assign(value);
        OnPropertyChanged(name);
        _owner.Save(this);
    }
}

/// <summary>Global hotkeys bound to actions, plus the desktop notification and clipboard-watch settings.</summary>
public sealed partial class HotkeysViewModel : PageViewModel
{
    public static readonly IReadOnlyList<string> NotificationTargets = ["Auto", "In-app", "System"];

    private readonly HotkeyService _service;
    private readonly JsonFile<DesktopSettings>? _desktop;
    private readonly Func<IEnumerable<string>> _actionNames;
    private HotkeyItemViewModel? _recording;

    public HotkeysViewModel(HotkeyService service, JsonFile<DesktopSettings>? desktop = null, Func<IEnumerable<string>>? actionNames = null)
        : base("Hotkeys")
    {
        _service = service;
        _desktop = desktop;
        _actionNames = actionNames ?? (() => []);
        foreach (var hotkey in service.Hotkeys) Hotkeys.Add(new HotkeyItemViewModel(this, hotkey));
        service.Changed += () => OnUi(RefreshStates);
        RefreshStates();
    }

    public ObservableCollection<HotkeyItemViewModel> Hotkeys { get; } = [];

    public IReadOnlyList<string> ActionNames => ["", .. _actionNames().Order(StringComparer.OrdinalIgnoreCase)];

    public string BackendStatus => $"{_service.BackendName}: {_service.BackendStatus}";

    public bool IsEmpty => Hotkeys.Count == 0;

    [ObservableProperty] private string _status = "";

    public string NotificationTarget
    {
        get => (_desktop?.Value.NotificationTarget ?? Core.Desktop.Notifications.NotificationTarget.Auto) switch
        {
            Core.Desktop.Notifications.NotificationTarget.InApp => "In-app",
            Core.Desktop.Notifications.NotificationTarget.System => "System",
            _ => "Auto",
        };
        set
        {
            if (_desktop is null || value is null) return;
            _desktop.Value.NotificationTarget = value switch
            {
                "In-app" => Core.Desktop.Notifications.NotificationTarget.InApp,
                "System" => Core.Desktop.Notifications.NotificationTarget.System,
                _ => Core.Desktop.Notifications.NotificationTarget.Auto,
            };
            _desktop.ScheduleSave();
            OnPropertyChanged();
        }
    }

    public bool WatchClipboard
    {
        get => _desktop?.Value.WatchClipboard ?? false;
        set
        {
            if (_desktop is null || _desktop.Value.WatchClipboard == value) return;
            _desktop.Value.WatchClipboard = value;
            _desktop.ScheduleSave();
            OnPropertyChanged();
        }
    }

    public bool IsRecording => _recording is not null;

    [RelayCommand]
    private void AddHotkey()
    {
        var definition = new HotkeyDefinition { Name = $"Hotkey {Hotkeys.Count + 1}" };
        var item = new HotkeyItemViewModel(this, definition);
        Hotkeys.Add(item);
        Save(item);
        OnPropertyChanged(nameof(IsEmpty));
        StartRecording(item);
    }

    internal void Save(HotkeyItemViewModel item) => _ = _service.SaveAsync(item.Definition);

    internal void Remove(HotkeyItemViewModel item)
    {
        if (_recording == item) StopRecording();
        Hotkeys.Remove(item);
        _ = _service.RemoveAsync(item.Definition.Id);
        OnPropertyChanged(nameof(IsEmpty));
    }

    internal void Test(HotkeyItemViewModel item) =>
        Status = _service.Simulate(item.Definition.Id) ? $"Fired '{item.Name}' as if pressed." : "Save the hotkey first.";

    internal void StartRecording(HotkeyItemViewModel item)
    {
        StopRecording();
        _recording = item;
        item.IsRecording = true;
        Status = "Press the key combination for '" + item.Name + "' (Esc cancels).";
        OnPropertyChanged(nameof(IsRecording));
    }

    private void StopRecording()
    {
        if (_recording is not null) _recording.IsRecording = false;
        _recording = null;
        OnPropertyChanged(nameof(IsRecording));
    }

    /// <summary>Called by the view for key presses; returns true when it consumed one for recording.</summary>
    public bool OnKey(Key key, KeyModifiers modifiers)
    {
        if (_recording is not { } item) return false;
        if (key == Key.Escape && modifiers == KeyModifiers.None)
        {
            StopRecording();
            Status = "Recording cancelled.";
            return true;
        }
        if (HotkeyKeyMap.ToGesture(key, modifiers) is not { } gesture) return true;   // a modifier on its own: keep waiting
        item.Gesture = gesture.ToString();
        StopRecording();
        Status = $"'{item.Name}' set to {gesture}.";
        return true;
    }

    internal void RefreshStates()
    {
        foreach (var item in Hotkeys)
        {
            var state = _service.State(item.Definition.Id);
            item.Registered = state?.Registered ?? false;
            item.State = state?.Message ?? "Not registered yet";
        }
        OnPropertyChanged(nameof(BackendStatus));
    }
}

/// <summary>Maps Avalonia keys to hotkey key names (layout-independent physical keys are not needed for combos).</summary>
public static class HotkeyKeyMap
{
    public static HotkeyGesture? ToGesture(Key key, KeyModifiers modifiers)
    {
        if (Name(key) is not { } name) return null;
        var m = HotkeyModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control)) m |= HotkeyModifiers.Ctrl;
        if (modifiers.HasFlag(KeyModifiers.Alt)) m |= HotkeyModifiers.Alt;
        if (modifiers.HasFlag(KeyModifiers.Shift)) m |= HotkeyModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Meta)) m |= HotkeyModifiers.Meta;
        return new HotkeyGesture(m, name);
    }

    /// <summary>Hotkey key name for an Avalonia key; null for modifier keys and keys without a mapping.</summary>
    public static string? Name(Key key)
    {
        if (key is >= Key.A and <= Key.Z) return ((char)('A' + (key - Key.A))).ToString();
        if (key is >= Key.D0 and <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key is >= Key.NumPad0 and <= Key.NumPad9) return "NumPad" + (key - Key.NumPad0);
        if (key is >= Key.F1 and <= Key.F24) return "F" + (key - Key.F1 + 1);
        return key switch
        {
            Key.Space => "Space", Key.Enter => "Enter", Key.Tab => "Tab", Key.Escape => "Escape", Key.Back => "Backspace",
            Key.Insert => "Insert", Key.Delete => "Delete", Key.Home => "Home", Key.End => "End", Key.PageUp => "PageUp",
            Key.PageDown => "PageDown", Key.Left => "Left", Key.Up => "Up", Key.Right => "Right", Key.Down => "Down",
            Key.PrintScreen => "PrintScreen", Key.Pause => "Pause", Key.Scroll => "ScrollLock", Key.Multiply => "Multiply",
            Key.Add => "Add", Key.Subtract => "Subtract", Key.Decimal => "Decimal", Key.Divide => "Divide",
            Key.OemMinus => "Minus", Key.OemPlus => "Equals", Key.OemComma => "Comma", Key.OemPeriod => "Period",
            Key.OemSemicolon => "Semicolon", Key.OemQuestion => "Slash", Key.OemPipe => "Backslash", Key.OemQuotes => "Quote",
            Key.OemOpenBrackets => "BracketLeft", Key.OemCloseBrackets => "BracketRight", Key.OemTilde => "Backquote",
            Key.VolumeMute => "VolumeMute", Key.VolumeDown => "VolumeDown", Key.VolumeUp => "VolumeUp",
            Key.MediaNextTrack => "MediaNext", Key.MediaPreviousTrack => "MediaPrevious", Key.MediaStop => "MediaStop",
            Key.MediaPlayPause => "MediaPlayPause",
            _ => null,
        };
    }
}
