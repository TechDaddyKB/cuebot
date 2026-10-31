using System.Collections.Concurrent;
using System.Text.Json;
using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.StreamDeck.Hid;

/// <summary>A Stream Deck found on USB: driven by Cuebot, or present with a <see cref="Problem"/> that stops it.</summary>
public sealed record StreamDeckHidDeviceInfo(
    string Key,
    string Serial,
    string Name,
    StreamDeckHidModel? Model,
    string Firmware,
    string Path,
    bool Connected,
    string Problem)
{
    public string ModelName => Model?.Name ?? "Stream Deck";

    /// <summary>Another program (the Stream Deck app, StreamController, a VM) has the deck: not a fault in Cuebot.</summary>
    public bool InUseElsewhere { get; init; }

    public string Summary => Connected
        ? $"{Name} · {ModelName} · serial {Serial}{(Firmware.Length > 0 ? " · firmware " + Firmware : "")}"
        : $"{ModelName}{(Serial.Length > 0 ? " · serial " + Serial : "")}: {Problem}";
}

/// <summary>
/// Direct USB mode: drives Stream Decks over HID with no Elgato software (the Stream Deck app does not exist on Linux).
/// Scans USB every few seconds (hot-plug without udev), opens supported decks, turns key presses, dial turns and touches
/// into the Stream Deck Action trigger (with each key's button id from <c>streamdeck-usb.json</c>, or
/// <c>hid-&lt;serial&gt;-&lt;index&gt;</c>), fires Stream Deck Connected / Info / Disconnected per deck, and applies the
/// Stream Deck sub-actions (registered with <see cref="StreamDeckHub"/> as a key target) by rendering key images.
/// Decks present on USB but not usable (another program took them over, no permission, unsupported model) are reported
/// in the status line. Never throws from background work.
/// </summary>
public sealed class StreamDeckHidService : IConnectionStatus, IStreamDeckKeyTarget, IAsyncDisposable, IDisposable
{
    public const string LogSource = "Stream Deck USB";
    private const int ReadTimeoutMs = 200;

    private readonly JsonFile<StreamDeckHidSettings> _settings;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly IHidTransport _transport;
    private readonly KeyImageLoader _images;
    private readonly Lock _gate = new();
    private readonly Lock _scanGate = new();
    private readonly Dictionary<string, OpenDeck> _decks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StreamDeckHidDeviceInfo> _problems = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Look> _looks = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _wake = new(0);
    private CancellationTokenSource? _run;
    private Task _loop = Task.CompletedTask;
    private string _lastStatus = "";
    private volatile bool _disposed;

    public StreamDeckHidService(JsonFile<StreamDeckHidSettings> settings, EventBus bus, AppLog log, StreamDeckHub? hub = null,
        IHidTransport? transport = null, KeyImageLoader? images = null)
    {
        _settings = settings;
        _bus = bus;
        _log = log;
        _transport = transport ?? HidTransports.CreateDefault();
        _images = images ?? new KeyImageLoader();
        _images.Failed += message => _log.Warn(LogSource, message);
        hub?.AddKeyTarget(this);
        StatusMessage = Settings.Enabled ? "Not started" : "Off";
    }

    public string Name => "Stream Deck (USB)";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; }
    public event Action? StatusChanged;

    public StreamDeckHidSettings Settings => _settings.Value;

    /// <summary>Why direct USB mode cannot work on this system (e.g. Windows for now), or null.</summary>
    public string? UnavailableReason => _transport.UnavailableReason;

    /// <summary>How long Show Alert / Show Ok stay on a key.</summary>
    internal TimeSpan OverlayDuration { get; set; } = TimeSpan.FromSeconds(1.5);

    /// <summary>Decks being driven, then decks found with a problem.</summary>
    public IReadOnlyList<StreamDeckHidDeviceInfo> Devices
    {
        get
        {
            lock (_gate)
            {
                return [.. _decks.Values.OrderBy(d => d.Serial, StringComparer.Ordinal).Select(d => d.Info()),
                    .. _problems.Values.OrderBy(p => p.Key, StringComparer.Ordinal)];
            }
        }
    }

    // ---- Lifecycle -------------------------------------------------------------------------------------------------

    /// <summary>Scans for decks now and then every <see cref="StreamDeckHidSettings.ScanIntervalMs"/>. Never throws.</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        if (_disposed || _run is not null) return Task.CompletedTask;
        _run = new CancellationTokenSource();
        var token = _run.Token;
        if (UnavailableReason is { } reason) _log.Info(LogSource, reason);
        _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Run(Scan, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _log.Error(LogSource, "Scanning for Stream Decks failed", ex);
            }
            try
            {
                await _wake.WaitAsync(TimeSpan.FromMilliseconds(Math.Clamp(Settings.ScanIntervalMs, 250, 60000)), ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
        }
    }

    /// <summary>Rescan soon (after a deck was lost or the settings changed).</summary>
    public void Wake()
    {
        if (_disposed) return;
        try
        {
            _wake.Release();
        }
        catch (ObjectDisposedException)
        {
            // Disposing.
        }
    }

    /// <summary>For containers disposed synchronously; prefer <see cref="DisposeAsync"/>.</summary>
    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_run is { } run)
        {
            await run.CancelAsync().ConfigureAwait(false);
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Stopped.
            }
            run.Dispose();
        }
        OpenDeck[] decks;
        lock (_gate)
        {
            decks = [.. _decks.Values];
            _decks.Clear();
        }
        foreach (var deck in decks) await CloseAsync(deck, Settings.ResetOnExit, publish: false).ConfigureAwait(false);
        _wake.Dispose();
    }

    // ---- Scanning --------------------------------------------------------------------------------------------------

    /// <summary>One pass: open new decks, drop vanished ones, record problems, update the status.</summary>
    internal void Scan()
    {
        lock (_scanGate)
        {
            if (_disposed) return;
            if (!Settings.Enabled || UnavailableReason is not null)
            {
                CloseAll(reset: true);
                lock (_gate) _problems.Clear();
                UpdateStatus();
                return;
            }

            IReadOnlyList<HidDeviceInfo> found;
            try
            {
                found = _transport.Enumerate(StreamDeckHidModels.ElgatoVendorId);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _log.Warn(LogSource, $"Could not list USB devices: {ex.Message}");
                found = [];
            }

            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var info in found)
            {
                if (StreamDeckHidModels.Find(info.VendorId, info.ProductId) is not { } model) continue;   // Elgato, but not a deck
                var key = info.Key;
                if (!seen.Add(key)) continue;
                OpenDeck? existing;
                lock (_gate) existing = _decks.GetValueOrDefault(key);
                if (existing is not null && existing.Device.Path == info.Path) continue;
                if (existing is not null) Lose(existing, "moved to another port");
                TryOpen(info, model);
            }

            OpenDeck[] vanished;
            lock (_gate)
            {
                vanished = [.. _decks.Values.Where(d => !seen.Contains(d.Key))];
                foreach (var k in _problems.Keys.Where(k => !seen.Contains(k)).ToArray()) _problems.Remove(k);
            }
            foreach (var deck in vanished) Lose(deck, "unplugged");
            UpdateStatus();
        }
    }

    private void TryOpen(HidDeviceInfo info, StreamDeckHidModel model)
    {
        string? problem = null;
        var elsewhere = false;
        if (!model.Supported)
            problem = "not supported in direct USB mode yet; use the Stream Deck app with the Cuebot plugin";
        else if (!info.HasHidInterface)
            (elsewhere, problem) = (true, "on USB, but another program has taken it over (StreamController and other libusb programs detach the kernel HID driver). Close that program; unplug and replug the deck if it does not come back");

        if (problem is null)
        {
            try
            {
                var deck = Open(info, model);
                lock (_gate)
                {
                    _decks[deck.Key] = deck;
                    _problems.Remove(deck.Key);
                }
                _log.Info(LogSource, $"{deck.Name} ({model.Name}, serial {deck.Serial}, firmware {deck.Firmware}) connected on {info.Path}");
                _bus.Publish(new BotEvent(StreamDeckHub.ConnectedEvent, DeviceArgs(deck)));
                _bus.Publish(new BotEvent(StreamDeckHub.InfoEvent, InfoArgs(deck)));
                deck.StartReader(ReadLoop);
                return;
            }
            catch (HidOpenException ex)
            {
                elsewhere = ex.Error == HidOpenError.Busy;
                problem = ex.Error == HidOpenError.Busy && info.Path is not null
                    ? ex.Message + " (close it, or turn direct USB mode off)"
                    : ex.Message;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException or InvalidOperationException)
            {
                problem = "could not be opened: " + ex.Message;
            }
        }

        var entry = new StreamDeckHidDeviceInfo(info.Key, info.Serial, model.Name, model, "", info.Path ?? "", false, problem)
        {
            InUseElsewhere = elsewhere,
        };
        bool changed;
        lock (_gate)
        {
            changed = !_problems.TryGetValue(info.Key, out var old) || old.Problem != problem;
            _problems[info.Key] = entry;
        }
        if (changed) _log.Warn(LogSource, $"{model.Name}{(info.Serial.Length > 0 ? " " + info.Serial : "")}: {problem}");
    }

    private OpenDeck Open(HidDeviceInfo info, StreamDeckHidModel model)
    {
        var device = _transport.Open(info);
        StreamDeckHidDeck? deck = null;
        try
        {
            deck = new StreamDeckHidDeck(device, model);
            var serial = info.Serial.Length > 0 ? info.Serial : deck.ReadSerial();
            var firmware = deck.ReadFirmware();
            var config = StreamDeckHidMapping.Device(Settings, serial);
            var open = new OpenDeck(info, model, deck, serial, firmware,
                config?.Name is { Length: > 0 } name ? name : model.Name);
            deck.ResetKeyStream();
            deck.SetBrightness(config?.Brightness ?? Settings.Brightness);
            for (var key = 0; key < model.ImageKeyCount; key++) PaintAsync(open, key).GetAwaiter().GetResult();
            return open;
        }
        catch
        {
            if (deck is not null) deck.Dispose();
            else device.Dispose();
            throw;
        }
    }

    private void CloseAll(bool reset)
    {
        OpenDeck[] decks;
        lock (_gate)
        {
            decks = [.. _decks.Values];
            _decks.Clear();
        }
        foreach (var deck in decks) CloseAsync(deck, reset, publish: true).GetAwaiter().GetResult();
    }

    /// <summary>A deck went away (unplugged or failed): forget it, fire Disconnected, rescan soon.</summary>
    private void Lose(OpenDeck deck, string why)
    {
        lock (_gate)
        {
            if (!_decks.TryGetValue(deck.Key, out var current) || current != deck) return;
            _decks.Remove(deck.Key);
        }
        _log.Info(LogSource, $"{deck.Name} (serial {deck.Serial}) disconnected: {why}");
        _ = CloseAsync(deck, reset: false, publish: true);
        UpdateStatus();
        Wake();
    }

    private async Task CloseAsync(OpenDeck deck, bool reset, bool publish)
    {
        try
        {
            await deck.StopReaderAsync().ConfigureAwait(false);
            if (reset && !deck.Lost)
            {
                // Let a key image that is being sent finish first, so the reset is not followed by a stray page.
                var painting = await deck.Paint.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                try
                {
                    deck.Deck.Reset();
                    deck.Deck.SetBrightness(StreamDeckHidMapping.Device(Settings, deck.Serial)?.Brightness ?? Settings.Brightness);
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // Already gone.
                }
                finally
                {
                    if (painting) deck.Paint.Release();
                }
            }
            deck.Lost = true;   // no more paints
            deck.Deck.Dispose();
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, $"Closing {deck.Name} failed", ex);
        }
        if (publish) _bus.Publish(new BotEvent(StreamDeckHub.DisconnectedEvent, DeviceArgs(deck)));
    }

    // ---- Input -----------------------------------------------------------------------------------------------------

    private void ReadLoop(OpenDeck deck, CancellationToken ct)
    {
        var errors = 0;
        while (!ct.IsCancellationRequested)
        {
            StreamDeckHidInput? input;
            try
            {
                input = deck.Deck.ReadInput(ReadTimeoutMs);
                errors = 0;
            }
            catch (HidDisconnectedException)
            {
                deck.Lost = true;
                Lose(deck, "unplugged");
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception ex)
            {
                if (++errors >= 5)
                {
                    deck.Lost = true;
                    _log.Warn(LogSource, $"Reading {deck.Name} keeps failing: {ex.Message}");
                    Lose(deck, "read errors");
                    return;
                }
                Thread.Sleep(100);
                continue;
            }
            if (input is null) continue;
            try
            {
                Handle(deck, input);
            }
            catch (Exception ex)
            {
                _log.Error(LogSource, "Handling Stream Deck input failed", ex);
            }
        }
    }

    private void Handle(OpenDeck deck, StreamDeckHidInput input)
    {
        switch (input)
        {
            case KeyStatesInput keys:
                foreach (var (index, pressed) in StreamDeckHidProtocol.Changes(deck.Keys, keys.Pressed))
                    Publish(deck, index, dial: false, pressed ? "keyDown" : "keyUp");
                deck.Keys = keys.Pressed;
                break;
            case DialPressInput dials:
                foreach (var (index, pressed) in StreamDeckHidProtocol.Changes(deck.Dials, dials.Pressed))
                    Publish(deck, index, dial: true, pressed ? "dialDown" : "dialUp", a => a["pressed"] = pressed);
                deck.Dials = dials.Pressed;
                break;
            case DialTurnInput turn:
                for (var i = 0; i < turn.Ticks.Length; i++)
                {
                    if (turn.Ticks[i] == 0) continue;
                    var ticks = turn.Ticks[i];
                    var pressed = i < deck.Dials.Length && deck.Dials[i];
                    Publish(deck, i, dial: true, "dialRotate", a =>
                    {
                        a["ticks"] = ticks;
                        a["pressed"] = pressed;
                    });
                }
                break;
            case TouchInput { Kind: not TouchKind.Drag } touch when deck.Model.DialCount > 0 && deck.Model.TouchStripWidth > 0:
            {
                // The strip is split into one segment per dial, like the Stream Deck app does.
                var segment = Math.Clamp(touch.X * deck.Model.DialCount / deck.Model.TouchStripWidth, 0, deck.Model.DialCount - 1);
                Publish(deck, segment, dial: true, "touchTap", a =>
                {
                    a["tapX"] = touch.X;
                    a["tapY"] = touch.Y;
                    a["hold"] = touch.Kind == TouchKind.Long;
                });
                break;
            }
        }
    }

    private void Publish(OpenDeck deck, int index, bool dial, string buttonEvent, Action<Dictionary<string, object?>>? extra = null)
    {
        var args = InputArgs(deck.Model, deck.Serial, deck.Name, index, dial, buttonEvent,
            StreamDeckHidMapping.ButtonId(Settings, deck.Serial, index, dial), StateOf);
        extra?.Invoke(args);
        _bus.Publish(new BotEvent(StreamDeckHub.ActionEvent, args));
    }

    private int StateOf(string buttonId) => _looks.TryGetValue(buttonId, out var look) ? look.State : 0;

    /// <summary>The Stream Deck Action arguments for direct USB input (same names as the plugin's, plus keyIndex and connection).</summary>
    internal static Dictionary<string, object?> InputArgs(StreamDeckHidModel model, string serial, string name, int index, bool dial,
        string buttonEvent, string buttonId, Func<string, int> state)
    {
        var (row, column) = dial ? (model.Rows, index) : model.Position(index);
        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["buttonId"] = buttonId,
            ["buttonEvent"] = buttonEvent,
            ["controller"] = dial ? "Encoder" : "Keypad",
            ["isDial"] = dial,
            ["context"] = $"usb:{serial}:{(dial ? "dial" : "key")}{index}",
            ["deviceId"] = serial,
            ["deviceName"] = name,
            ["deviceType"] = model.Name,
            ["row"] = row,
            ["column"] = column,
            ["state"] = state(buttonId),
            ["instanceId"] = "usb:" + serial,
            ["keyIndex"] = index,
            ["connection"] = "usb",
        };
    }

    private static Dictionary<string, object?> DeviceArgs(OpenDeck deck) => new(StringComparer.OrdinalIgnoreCase)
    {
        ["instanceId"] = "usb:" + deck.Serial,
        ["remote"] = "usb",
        ["connection"] = "usb",
        ["pluginVersion"] = "",
        ["streamDeckVersion"] = "",
        ["platform"] = OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsWindows() ? "windows" : "mac",
        ["deviceCount"] = 1,
        ["deviceId"] = deck.Serial,
        ["deviceName"] = deck.Name,
        ["deviceType"] = deck.Model.Name,
        ["serial"] = deck.Serial,
        ["firmware"] = deck.Firmware,
    };

    private static Dictionary<string, object?> InfoArgs(OpenDeck deck)
    {
        var args = DeviceArgs(deck);
        var m = deck.Model;
        args["deviceNames"] = deck.Name;
        args["devices"] = JsonSerializer.Serialize(new[]
        {
            new { id = deck.Serial, name = deck.Name, type = m.ElgatoType, typeName = m.Name, columns = m.Columns, rows = m.Rows, connected = true },
        });
        args["device0.id"] = deck.Serial;
        args["device0.name"] = deck.Name;
        args["device0.type"] = m.Name;
        args["data.devices[0].id"] = deck.Serial;
        args["data.devices[0].name"] = deck.Name;
        args["data.devices[0].size.columns"] = (long)m.Columns;
        args["data.devices[0].size.rows"] = (long)m.Rows;
        args["data.devices[0].type"] = (long)m.ElgatoType;
        return args;
    }

    // ---- Key appearance (the Stream Deck sub-actions) --------------------------------------------------------------

    public IEnumerable<string> ButtonIds
    {
        get
        {
            var ids = new List<string>();
            OpenDeck[] decks;
            lock (_gate) decks = [.. _decks.Values];
            foreach (var deck in decks)
            {
                for (var i = 0; i < deck.Model.KeyCount; i++) ids.Add(StreamDeckHidMapping.ButtonId(Settings, deck.Serial, i));
                for (var i = 0; i < deck.Model.DialCount; i++) ids.Add(StreamDeckHidMapping.ButtonId(Settings, deck.Serial, i, dial: true));
            }
            foreach (var device in Settings.Devices)
                ids.AddRange(device.Keys.Concat(device.Dials).Select(k => k.ButtonId.Trim()).Where(id => id.Length > 0));
            return ids.Distinct(StringComparer.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Applies a sub-action to every key with the button id on every open deck, and remembers it per button id so decks
    /// plugged in later show it too. Returns how many keys/dials on open decks have that id.
    /// </summary>
    public async Task<int> ApplyAsync(StreamDeckCommand command, CancellationToken ct = default)
    {
        var id = command.ButtonId.Trim();
        var look = _looks.GetOrAdd(id, _ => new Look());
        var overlay = KeyOverlay.None;
        lock (look)
        {
            switch (command.Command)
            {
                case "setTitle": look.Titles[(command.State ?? look.State) == 1 ? 1 : 0] = Nonempty(command.Title); break;
                case "setImage": look.Images[(command.State ?? look.State) == 1 ? 1 : 0] = Nonempty(command.Image); break;
                case "setValue": look.Titles[look.State] = Nonempty(command.Value); break;
                case "setState": look.State = command.State is 1 ? 1 : 0; break;
                case "toggleState": look.State ^= 1; break;
                case "showAlert": overlay = KeyOverlay.Alert; break;
                case "showOk": overlay = KeyOverlay.Ok; break;
            }
        }

        var targets = new List<(OpenDeck Deck, int Key)>();
        var matched = 0;
        OpenDeck[] decks;
        lock (_gate) decks = [.. _decks.Values];
        foreach (var deck in decks)
        {
            for (var i = 0; i < deck.Model.KeyCount; i++)
            {
                if (!string.Equals(StreamDeckHidMapping.ButtonId(Settings, deck.Serial, i), id, StringComparison.OrdinalIgnoreCase)) continue;
                matched++;
                if (i < deck.Model.ImageKeyCount && deck.Model.HasDisplay) targets.Add((deck, i));
            }
            for (var i = 0; i < deck.Model.DialCount; i++)
            {
                if (string.Equals(StreamDeckHidMapping.ButtonId(Settings, deck.Serial, i, dial: true), id, StringComparison.OrdinalIgnoreCase)) matched++;
            }
        }

        var until = DateTimeOffset.UtcNow + OverlayDuration;
        foreach (var (deck, key) in targets)
        {
            if (overlay != KeyOverlay.None) deck.Overlays[key] = (overlay, until);
            await PaintAsync(deck, key, ct).ConfigureAwait(false);
        }
        if (overlay != KeyOverlay.None && targets.Count > 0) _ = ClearOverlaysAsync(targets, until);
        return matched;
    }

    private async Task ClearOverlaysAsync(List<(OpenDeck Deck, int Key)> targets, DateTimeOffset until)
    {
        try
        {
            await Task.Delay(OverlayDuration).ConfigureAwait(false);
            foreach (var (deck, key) in targets)
            {
                if (!deck.Overlays.TryGetValue(key, out var o) || o.Until != until) continue;   // a newer overlay owns it
                deck.Overlays.TryRemove(key, out _);
                await PaintAsync(deck, key).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, "Clearing a key alert failed", ex);
        }
    }

    /// <summary>Repaints every key of every open deck (after the mapping changed).</summary>
    public async Task RepaintAllAsync()
    {
        OpenDeck[] decks;
        lock (_gate) decks = [.. _decks.Values];
        foreach (var deck in decks)
        {
            for (var key = 0; key < deck.Model.ImageKeyCount; key++) await PaintAsync(deck, key).ConfigureAwait(false);
        }
    }

    /// <summary>What a key shows now: sub-action changes for its button id, else its own title and image.</summary>
    internal async Task<KeyFace> FaceAsync(string serial, int key, KeyOverlay overlay, CancellationToken ct = default)
    {
        var id = StreamDeckHidMapping.ButtonId(Settings, serial, key);
        var config = StreamDeckHidMapping.Key(StreamDeckHidMapping.Device(Settings, serial), key);
        string? title = null, image = null;
        if (_looks.TryGetValue(id, out var look))
        {
            lock (look)
            {
                title = look.Titles[look.State];
                image = look.Images[look.State];
            }
        }
        title ??= config?.Title ?? "";
        image ??= config?.Image;
        var bytes = await _images.LoadAsync(image, ct).ConfigureAwait(false);
        return new KeyFace(title, bytes, overlay);
    }

    /// <summary>Renders and sends one key. Paints on one deck are serialized so the newest look always lands last.</summary>
    private async Task PaintAsync(OpenDeck deck, int key, CancellationToken ct = default)
    {
        if (!deck.Model.HasDisplay || key >= deck.Model.ImageKeyCount || deck.Lost) return;
        await deck.Paint.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var overlay = deck.Overlays.TryGetValue(key, out var o) && o.Until > DateTimeOffset.UtcNow ? o.Overlay : KeyOverlay.None;
            var face = await FaceAsync(deck.Serial, key, overlay, ct).ConfigureAwait(false);
            var encoded = StreamDeckKeyRenderer.Render(deck.Model, face);
            deck.Deck.SetKeyImage(key, encoded);
        }
        catch (HidDisconnectedException)
        {
            deck.Lost = true;
            Lose(deck, "unplugged");
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException or ArgumentException)
        {
            _log.Warn(LogSource, $"Updating key {key} on {deck.Name} failed: {ex.Message}");
        }
        finally
        {
            deck.Paint.Release();
        }
    }

    private static string? Nonempty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    // ---- Settings (the Stream Deck page) ---------------------------------------------------------------------------

    /// <summary>Turns direct USB mode on or off (off closes and blanks the decks so another program can use them).</summary>
    public void SetEnabled(bool enabled)
    {
        Settings.Enabled = enabled;
        _settings.ScheduleSave();
        Wake();
    }

    /// <summary>Sets the default brightness and applies it to decks without their own.</summary>
    public void SetBrightness(int percent)
    {
        Settings.Brightness = Math.Clamp(percent, 0, 100);
        _settings.ScheduleSave();
        OpenDeck[] decks;
        lock (_gate) decks = [.. _decks.Values];
        foreach (var deck in decks)
        {
            try
            {
                deck.Deck.SetBrightness(StreamDeckHidMapping.Device(Settings, deck.Serial)?.Brightness ?? Settings.Brightness);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                _log.Warn(LogSource, $"Setting the brightness of {deck.Name} failed: {ex.Message}");
            }
        }
    }

    /// <summary>Edits (creating if needed) one deck's mapping, saves it and repaints.</summary>
    public async Task UpdateDeviceAsync(string serial, Action<StreamDeckHidDeviceConfig> edit)
    {
        var config = StreamDeckHidMapping.Device(Settings, serial);
        if (config is null)
        {
            config = new StreamDeckHidDeviceConfig { Serial = serial };
            Settings.Devices.Add(config);
        }
        edit(config);
        config.Keys.RemoveAll(k => k.ButtonId.Trim().Length == 0 && k.Title.Length == 0 && k.Image.Trim().Length == 0);
        config.Dials.RemoveAll(k => k.ButtonId.Trim().Length == 0 && k.Title.Length == 0 && k.Image.Trim().Length == 0);
        _settings.ScheduleSave();
        OpenDeck? deck;
        lock (_gate) deck = _decks.Values.FirstOrDefault(d => string.Equals(d.Serial, serial, StringComparison.OrdinalIgnoreCase));
        if (deck is not null)
        {
            deck.Name = config.Name is { Length: > 0 } name ? name : deck.Model.Name;
            for (var key = 0; key < deck.Model.ImageKeyCount; key++) await PaintAsync(deck, key).ConfigureAwait(false);
        }
        UpdateStatus(force: true);
    }

    // ---- Status ----------------------------------------------------------------------------------------------------

    private void UpdateStatus(bool force = false)
    {
        ConnectionState state;
        string message;
        OpenDeck[] decks;
        StreamDeckHidDeviceInfo[] problems;
        lock (_gate)
        {
            decks = [.. _decks.Values.OrderBy(d => d.Serial, StringComparer.Ordinal)];
            problems = [.. _problems.Values.OrderBy(p => p.Key, StringComparer.Ordinal)];
        }
        var problemText = string.Join("; ", problems.Select(p => p.Summary));
        if (UnavailableReason is { } reason)
        {
            (state, message) = (ConnectionState.Disconnected, reason);
        }
        else if (!Settings.Enabled)
        {
            (state, message) = (ConnectionState.Disconnected, "Direct USB mode is off");
        }
        else if (decks.Length > 0)
        {
            message = string.Join(", ", decks.Select(d => $"{d.Name} ({d.Serial})")) + " on USB";
            if (problemText.Length > 0) message += "; " + problemText;
            state = ConnectionState.Connected;
        }
        else if (problems.Length > 0)
        {
            // A deck another program holds is waiting, not broken: no red error chip for it.
            (state, message) = (problems.All(p => p.InUseElsewhere) ? ConnectionState.Disconnected : ConnectionState.Error, problemText);
        }
        else
        {
            (state, message) = (ConnectionState.Disconnected, "No Stream Deck on USB");
        }

        var text = state + message;
        lock (_gate)
        {
            if (!force && text == _lastStatus) return;
            _lastStatus = text;
            State = state;
            StatusMessage = message;
        }
        StatusChanged?.Invoke();
    }

    // ---- State -----------------------------------------------------------------------------------------------------

    /// <summary>What sub-actions set for one button id: titles and images per state, and the current state.</summary>
    private sealed class Look
    {
        public readonly string?[] Titles = new string?[2];
        public readonly string?[] Images = new string?[2];
        public int State;
    }

    private sealed class OpenDeck(HidDeviceInfo device, StreamDeckHidModel model, StreamDeckHidDeck deck, string serial, string firmware, string name)
    {
        private CancellationTokenSource? _stop;
        private Task _reader = Task.CompletedTask;

        public HidDeviceInfo Device { get; } = device;
        public string Key => Device.Key;
        public StreamDeckHidModel Model { get; } = model;
        public StreamDeckHidDeck Deck { get; } = deck;
        public string Serial { get; } = serial;
        public string Firmware { get; } = firmware;
        public string Name { get; set; } = name;
        public bool[] Keys { get; set; } = new bool[model.KeyCount];
        public bool[] Dials { get; set; } = new bool[model.DialCount];
        public SemaphoreSlim Paint { get; } = new(1, 1);
        public ConcurrentDictionary<int, (KeyOverlay Overlay, DateTimeOffset Until)> Overlays { get; } = new();
        public volatile bool Lost;

        public StreamDeckHidDeviceInfo Info() => new(Key, Serial, Name, Model, Firmware, Device.Path ?? "", true, "");

        public void StartReader(Action<OpenDeck, CancellationToken> loop)
        {
            _stop = new CancellationTokenSource();
            var token = _stop.Token;
            _reader = Task.Factory.StartNew(() => loop(this, token), token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        public async Task StopReaderAsync()
        {
            if (_stop is null) return;
            await _stop.CancelAsync().ConfigureAwait(false);
            // The reader may be the caller (a lost deck closes itself); never wait for ourselves.
            if (Task.CurrentId != _reader.Id)
            {
                try
                {
                    await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
                {
                    // Gone or stuck; closing the device ends it.
                }
            }
        }
    }
}
