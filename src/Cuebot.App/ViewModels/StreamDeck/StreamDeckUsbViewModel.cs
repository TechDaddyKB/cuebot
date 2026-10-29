using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.StreamDeck.Hid;

namespace Cuebot.App.ViewModels.StreamDeck;

/// <summary>
/// The "Direct USB" card of the Stream Deck tab: on/off, brightness, the decks found on USB (or why they cannot be
/// used) and, per deck, which button id each key has and the title it shows by default.
/// </summary>
public sealed partial class StreamDeckUsbViewModel : ObservableObject
{
    private readonly StreamDeckHidService _usb;
    private string _deviceSignature = "";

    public StreamDeckUsbViewModel(StreamDeckHidService usb)
    {
        _usb = usb;
        usb.StatusChanged += () => OnUi(Refresh);
        Refresh();
    }

    public string State => _usb.State.ToString();
    public string StatusMessage => _usb.StatusMessage;
    public bool IsAvailable => _usb.UnavailableReason is null;

    public bool Enabled
    {
        get => _usb.Settings.Enabled;
        set
        {
            if (value == _usb.Settings.Enabled) return;
            _usb.SetEnabled(value);
            OnPropertyChanged();
        }
    }

    public decimal Brightness
    {
        get => _usb.Settings.Brightness;
        set
        {
            var percent = (int)Math.Clamp(value, 0, 100);
            if (percent == _usb.Settings.Brightness) return;
            _usb.SetBrightness(percent);
            OnPropertyChanged();
        }
    }

    public ObservableCollection<StreamDeckUsbDeviceViewModel> Devices { get; } = [];
    public bool HasNoDevices => Devices.Count == 0;

    [ObservableProperty] private string _message = "";

    [RelayCommand]
    private void Rescan() => _usb.Wake();

    /// <summary>Stores a deck's key list (its Save button) and repaints the deck.</summary>
    internal async Task SaveAsync(StreamDeckUsbDeviceViewModel device)
    {
        try
        {
            await _usb.UpdateDeviceAsync(device.Serial, config => device.ApplyTo(config));
            Message = $"Saved the keys of {device.Serial}.";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            Message = "Could not save: " + ex.Message;
        }
    }

    [RelayCommand]
    private void Refresh()
    {
        // Rebuilt only when the set of decks changes, so unsaved edits survive status updates.
        var devices = _usb.Devices;
        var signature = string.Join("|", devices.Select(d => $"{d.Key}:{d.Connected}:{d.Problem}"));
        if (signature != _deviceSignature)
        {
            _deviceSignature = signature;
            Devices.Clear();
            foreach (var d in devices) Devices.Add(new StreamDeckUsbDeviceViewModel(d, _usb.Settings, SaveAsync));
        }
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(HasNoDevices));
    }

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess()) action();
        else Dispatcher.UIThread.Post(action);
    }
}

/// <summary>One deck on USB with its editable key list.</summary>
public sealed class StreamDeckUsbDeviceViewModel
{
    public StreamDeckUsbDeviceViewModel(StreamDeckHidDeviceInfo info, StreamDeckHidSettings settings, Func<StreamDeckUsbDeviceViewModel, Task> save)
    {
        SaveCommand = new AsyncRelayCommand(() => save(this));
        Serial = info.Serial;
        Summary = info.Summary;
        IsConnected = info.Connected;
        var config = StreamDeckHidMapping.Device(settings, info.Serial);
        Name = config?.Name ?? "";
        if (info is { Connected: true, Model: { } model })
        {
            Keys =
            [
                .. Enumerable.Range(0, model.KeyCount).Select(i => new StreamDeckUsbKeyViewModel(i, false, model, info.Serial, StreamDeckHidMapping.Key(config, i))),
                .. Enumerable.Range(0, model.DialCount).Select(i => new StreamDeckUsbKeyViewModel(i, true, model, info.Serial, StreamDeckHidMapping.Key(config, i, dial: true))),
            ];
        }
    }

    public IAsyncRelayCommand SaveCommand { get; }

    public string Serial { get; }
    public string Summary { get; }
    public bool IsConnected { get; }
    public string Name { get; set; }
    public IReadOnlyList<StreamDeckUsbKeyViewModel> Keys { get; } = [];

    internal void ApplyTo(StreamDeckHidDeviceConfig config)
    {
        config.Name = Name.Trim();
        foreach (var key in Keys)
        {
            var list = key.IsDial ? config.Dials : config.Keys;
            var entry = list.LastOrDefault(k => k.Index == key.Index);
            if (entry is null)
            {
                entry = new StreamDeckHidKeyConfig { Index = key.Index };
                list.Add(entry);
            }
            entry.ButtonId = key.ButtonId.Trim();
            entry.Title = key.Title;
            entry.Image = key.Image.Trim();
        }
        config.Keys.Sort((a, b) => a.Index.CompareTo(b.Index));
        config.Dials.Sort((a, b) => a.Index.CompareTo(b.Index));
    }
}

/// <summary>One key or dial: label, button id (placeholder: the default id), title and image.</summary>
public sealed class StreamDeckUsbKeyViewModel(int index, bool dial, StreamDeckHidModel model, string serial, StreamDeckHidKeyConfig? config)
{
    public int Index { get; } = index;
    public bool IsDial { get; } = dial;

    public string Label => IsDial
        ? $"Dial {Index + 1}"
        : Index < model.ImageKeyCount ? $"Key {Index} (row {model.Position(Index).Row + 1}, column {model.Position(Index).Column + 1})" : $"Touch key {Index - model.ImageKeyCount + 1}";

    public string DefaultButtonId { get; } = StreamDeckHidMapping.DefaultButtonId(serial, index, dial);
    public bool HasDisplay => !IsDial && Index < model.ImageKeyCount && model.HasDisplay;
    public string ButtonId { get; set; } = config?.ButtonId ?? "";
    public string Title { get; set; } = config?.Title ?? "";
    public string Image { get; set; } = config?.Image ?? "";
}
