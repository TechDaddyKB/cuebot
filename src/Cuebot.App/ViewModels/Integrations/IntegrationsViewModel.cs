using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core;
using Cuebot.Integrations;
using Cuebot.Integrations.Common;
using Cuebot.Integrations.CrowdControl;
using Cuebot.Integrations.StreamLoots;
using Cuebot.Integrations.HeartRate;
using Cuebot.Integrations.HeartRate.HypeRate;
using Cuebot.Integrations.HeartRate.Pulsoid;
using Cuebot.Integrations.VTubeStudio;
using Cuebot.Integrations.Voicemod;

namespace Cuebot.App.ViewModels.Integrations;

/// <summary>
/// Integrations page: a card per app (VTube Studio, Voicemod, Pulsoid, HypeRate, StreamLoots, Crowd Control) with connection,
/// credentials and a live preview. The StreamLoots and Crowd Control cards appear when those services are passed in.
/// </summary>
public sealed class IntegrationsViewModel : PageViewModel
{
    public IntegrationsViewModel(VTubeStudioService vts, VoicemodService voicemod, PulsoidService pulsoid, HypeRateService hypeRate,
        StreamLootsService? streamLoots = null, CrowdControlService? crowdControl = null)
        : base("Integrations")
    {
        VTubeStudio = new VtsCardViewModel(vts);
        Voicemod = new VoicemodCardViewModel(voicemod);
        Pulsoid = new PulsoidCardViewModel(pulsoid);
        HypeRate = new HypeRateCardViewModel(hypeRate);
        StreamLoots = streamLoots is null ? null : new StreamLootsCardViewModel(streamLoots);
        CrowdControl = crowdControl is null ? null : new CrowdControlCardViewModel(crowdControl);
    }

    public VtsCardViewModel VTubeStudio { get; }
    public VoicemodCardViewModel Voicemod { get; }
    public PulsoidCardViewModel Pulsoid { get; }
    public HypeRateCardViewModel HypeRate { get; }
    public StreamLootsCardViewModel? StreamLoots { get; }
    public CrowdControlCardViewModel? CrowdControl { get; }
    public bool HasStreamLoots => StreamLoots is not null;
    public bool HasCrowdControl => CrowdControl is not null;
}

/// <summary>Connection part shared by every card: status, connect/disconnect, error and info lines.</summary>
public abstract partial class IntegrationCardViewModel : ObservableObject
{
    private readonly IntegrationConnection _connection;

    protected IntegrationCardViewModel(IntegrationConnection connection)
    {
        _connection = connection;
        connection.StatusChanged += () => OnUi(Refresh);
    }

    public string Name => _connection.Name;
    public string State => _connection.State.ToString();
    public string StatusMessage => _connection.StatusMessage;
    public bool IsConnected => _connection.State == ConnectionState.Connected;

    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _info = "";

    /// <summary>Re-reads every property (status and live data).</summary>
    protected void Refresh() => OnPropertyChanged(string.Empty);

    [RelayCommand]
    private async Task Connect()
    {
        Error = "";
        try { await _connection.ConnectAsync(); }
        catch (Exception ex) { Error = ex.Message; }
        Refresh();
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        Error = "";
        await _connection.DisconnectAsync();
        Refresh();
    }

    /// <summary>Runs a live command, showing failures in <see cref="Error"/>.</summary>
    protected async Task RunAsync(Func<Task<string>> command)
    {
        Error = "";
        Info = "";
        try { Info = await command(); }
        catch (Exception ex) { Error = ex.Message; }
    }

    protected static string Masked(string secret, string what) =>
        secret.Length == 0 ? $"No {what} saved" : $"Saved {what}: {Secrets.Mask(secret)}";

    protected static void OnUi(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
        else Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }
}

public sealed partial class VtsCardViewModel : IntegrationCardViewModel
{
    private readonly VTubeStudioService _vts;

    public VtsCardViewModel(VTubeStudioService vts) : base(vts)
    {
        _vts = vts;
        vts.DataChanged += () => OnUi(RefreshData);
        RefreshData();
    }

    private VTubeStudioSettings S => _vts.Settings;

    public string Host
    {
        get => S.Host;
        set { S.Host = value.Trim(); _vts.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal? Port
    {
        get => S.Port;
        set { S.Port = (int)(value ?? 8001); _vts.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; _vts.SaveSettings(); OnPropertyChanged(); }
    }

    /// <summary>The plugin token is issued by VTube Studio and only ever shown masked.</summary>
    public string MaskedToken => S.AuthToken.Length == 0
        ? "Not authorized yet: Connect, then click \"Allow\" in VTube Studio."
        : $"Authorized (plugin token {Secrets.Mask(S.AuthToken)})";

    public bool AwaitingApproval => _vts.AwaitingApproval;

    public string CurrentModel => _vts.CurrentModel is { Loaded: true } m
        ? string.Create(CultureInfo.InvariantCulture, $"{m.Name}  (x {m.Position.PositionX:0.00}, y {m.Position.PositionY:0.00}, size {m.Position.Size:0}, rotation {m.Position.Rotation:0}°)")
        : _vts.IsConnected ? "No model loaded" : "—";

    public ObservableCollection<VtsHotkeyRowViewModel> Hotkeys { get; } = [];
    public bool HasNoHotkeys => Hotkeys.Count == 0;
    public ObservableCollection<string> Models { get; } = [];

    [ObservableProperty] private string? _selectedModel;

    [RelayCommand]
    private void ResetToken()
    {
        _vts.ResetAuthorization();
        Info = "Token removed; the next connection asks VTube Studio for a new one.";
        Refresh();
    }

    [RelayCommand]
    private Task LoadModel() => RunAsync(async () =>
    {
        var name = SelectedModel ?? throw new InvalidOperationException("Pick a model first.");
        await _vts.LoadModelAsync(name);
        return $"Loading {name}...";
    });

    [RelayCommand]
    private Task RefreshLive() => RunAsync(async () =>
    {
        await _vts.RefreshAllAsync();
        return "Refreshed.";
    });

    internal Task TriggerAsync(VtsHotkey hotkey) => RunAsync(async () =>
    {
        await _vts.TriggerHotkeyAsync(hotkey.Id);
        return $"Triggered {hotkey.Name}.";
    });

    private void RefreshData()
    {
        Hotkeys.Clear();
        foreach (var h in _vts.Hotkeys) Hotkeys.Add(new VtsHotkeyRowViewModel(this, h));
        var selected = SelectedModel;
        Models.Clear();
        foreach (var m in _vts.Models) Models.Add(m.Name);
        SelectedModel = selected is not null && Models.Contains(selected) ? selected : null;
        Refresh();
    }
}

public sealed partial class VtsHotkeyRowViewModel(VtsCardViewModel card, VtsHotkey hotkey) : ObservableObject
{
    public string Name => hotkey.Name;
    public string Type => hotkey.Type;

    [RelayCommand]
    private Task Test() => card.TriggerAsync(hotkey);
}

public sealed partial class VoicemodCardViewModel : IntegrationCardViewModel
{
    private readonly VoicemodService _voicemod;

    public VoicemodCardViewModel(VoicemodService voicemod) : base(voicemod)
    {
        _voicemod = voicemod;
        voicemod.DataChanged += () => OnUi(RefreshData);
        RefreshData();
    }

    private VoicemodSettings S => _voicemod.Settings;

    /// <summary>The client key; the view masks it (PasswordChar).</summary>
    public string ClientKey
    {
        get => S.ClientKey;
        set
        {
            if (value.Trim() == S.ClientKey) return;
            S.ClientKey = value.Trim();
            _voicemod.SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaskedKey));
        }
    }

    public string MaskedKey => Masked(S.ClientKey, "client key");

    public decimal? Port
    {
        get => S.Port;
        set { S.Port = (int)(value ?? 0); _voicemod.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; _voicemod.SaveSettings(); OnPropertyChanged(); }
    }

    public string CurrentVoice => _voicemod.CurrentVoiceName is { Length: > 0 } name ? name : "—";

    public string Switches => _voicemod.IsConnected
        ? $"Voice changer {OnOff(VoicemodSwitch.VoiceChanger)} · Hear myself {OnOff(VoicemodSwitch.HearMyself)} · Background {OnOff(VoicemodSwitch.BackgroundEffects)} · Mic {(_voicemod.GetSwitch(VoicemodSwitch.MuteMic) == true ? "muted" : "live")}"
        : "";

    private string OnOff(VoicemodSwitch which) => _voicemod.GetSwitch(which) switch { true => "on", false => "off", null => "?" };

    public ObservableCollection<string> Voices { get; } = [];

    [ObservableProperty] private string? _selectedVoice;

    [RelayCommand]
    private Task ApplyVoice() => RunAsync(async () =>
    {
        var voice = await _voicemod.SelectVoiceAsync(SelectedVoice ?? throw new InvalidOperationException("Pick a voice first."));
        return $"Selected {voice.DisplayName}.";
    });

    [RelayCommand]
    private Task RandomVoice() => RunAsync(async () =>
    {
        await _voicemod.SelectRandomVoiceAsync();
        return "Random voice selected.";
    });

    [RelayCommand]
    private Task ToggleVoiceChanger() => RunAsync(async () =>
        $"Voice changer {(await _voicemod.SetSwitchAsync(VoicemodSwitch.VoiceChanger, SwitchMode.Toggle) ? "on" : "off")}.");

    private void RefreshData()
    {
        var selected = SelectedVoice;
        var names = _voicemod.Voices.Select(v => v.DisplayName).ToArray();
        if (!names.SequenceEqual(Voices))
        {
            Voices.Clear();
            foreach (var n in names) Voices.Add(n);
        }
        SelectedVoice = selected is not null && Voices.Contains(selected) ? selected : null;
        Refresh();
    }
}

/// <summary>Shared by both heart rate cards: the live BPM.</summary>
public abstract class HeartRateCardViewModel : IntegrationCardViewModel
{
    private readonly HeartRateConnection _source;

    protected HeartRateCardViewModel(HeartRateConnection source) : base(source)
    {
        _source = source;
        source.Hub.Updated += r =>
        {
            if (r.Source == source.Name) OnUi(() => OnPropertyChanged(nameof(Bpm)));
        };
    }

    public string Bpm => _source.Latest is { } r ? $"{r.Bpm} BPM" : "— BPM";
}

public sealed class PulsoidCardViewModel(PulsoidService pulsoid) : HeartRateCardViewModel(pulsoid)
{
    private PulsoidSettings S => pulsoid.Settings;

    /// <summary>The access token; the view masks it.</summary>
    public string AccessToken
    {
        get => S.AccessToken;
        set
        {
            if (value.Trim() == S.AccessToken) return;
            S.AccessToken = value.Trim();
            pulsoid.SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaskedToken));
        }
    }

    public string MaskedToken => Masked(S.AccessToken, "token");

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; pulsoid.SaveSettings(); OnPropertyChanged(); }
    }

    public bool UseLegacyV1
    {
        get => S.UseLegacyV1;
        set { S.UseLegacyV1 = value; pulsoid.SaveSettings(); OnPropertyChanged(); }
    }
}

public sealed class HypeRateCardViewModel(HypeRateService hypeRate) : HeartRateCardViewModel(hypeRate)
{
    private HypeRateSettings S => hypeRate.Settings;

    /// <summary>The API key; the view masks it.</summary>
    public string ApiKey
    {
        get => S.ApiKey;
        set
        {
            if (value.Trim() == S.ApiKey) return;
            S.ApiKey = value.Trim();
            hypeRate.SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaskedKey));
        }
    }

    public string MaskedKey => Masked(S.ApiKey, "API key");

    public string SessionId
    {
        get => S.SessionId;
        set { S.SessionId = value.Trim(); hypeRate.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; hypeRate.SaveSettings(); OnPropertyChanged(); }
    }

    public bool SubscribeClips
    {
        get => S.SubscribeClips;
        set { S.SubscribeClips = value; hypeRate.SaveSettings(); OnPropertyChanged(); }
    }
}

/// <summary>StreamLoots: the Alerts widget URL (masked), the newest alert and local test alerts.</summary>
public sealed partial class StreamLootsCardViewModel : IntegrationCardViewModel
{
    private readonly StreamLootsService _streamLoots;

    public StreamLootsCardViewModel(StreamLootsService streamLoots) : base(streamLoots)
    {
        _streamLoots = streamLoots;
        streamLoots.AlertReceived += () => OnUi(() => OnPropertyChanged(nameof(LastAlert)));
        _selectedTestKind = streamLoots.TestEventKinds.FirstOrDefault();
    }

    private StreamLootsSettings S => _streamLoots.Settings;

    /// <summary>The Alerts widget URL; the view masks it (it contains the alert id).</summary>
    public string AlertsUrl
    {
        get => S.AlertsUrl;
        set
        {
            if (value.Trim() == S.AlertsUrl) return;
            S.AlertsUrl = value.Trim();
            _streamLoots.SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaskedUrl));
        }
    }

    public string MaskedUrl => _streamLoots.MaskedSetting;

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; _streamLoots.SaveSettings(); OnPropertyChanged(); }
    }

    public string LastAlert => _streamLoots.LastAlert is { Length: > 0 } a ? a : "No alert yet";

    public IReadOnlyList<string> TestKinds => _streamLoots.TestEventKinds;

    [ObservableProperty] private string? _selectedTestKind;

    [RelayCommand]
    private void SendTest()
    {
        Error = "";
        Info = SelectedTestKind is { } kind && _streamLoots.SendTestEvent(kind)
            ? $"Sent a local test {kind} alert (isTest = true)."
            : "Pick a test alert first.";
        OnPropertyChanged(nameof(LastAlert));
    }
}

/// <summary>Crowd Control: the Overlay URL (masked), the current game session, running timed effects and test events.</summary>
public sealed partial class CrowdControlCardViewModel : IntegrationCardViewModel
{
    private readonly CrowdControlService _cc;

    public CrowdControlCardViewModel(CrowdControlService cc) : base(cc)
    {
        _cc = cc;
        cc.DataChanged += () => OnUi(Refresh);
        _selectedTestKind = cc.TestEventKinds.FirstOrDefault(k => k == "effectSuccess") ?? cc.TestEventKinds.FirstOrDefault();
    }

    private CrowdControlSettings S => _cc.Settings;

    /// <summary>The Overlay URL; the view masks it (it contains the overlay key).</summary>
    public string OverlayUrl
    {
        get => S.OverlayUrl;
        set
        {
            if (value.Trim() == S.OverlayUrl) return;
            S.OverlayUrl = value.Trim();
            _cc.SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaskedUrl));
        }
    }

    public string MaskedUrl => _cc.MaskedSetting;

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; _cc.SaveSettings(); OnPropertyChanged(); }
    }

    public string SessionText => _cc.Session is { } s
        ? $"{s.GameName}{(s.GamePackName.Length > 0 && s.GamePackName != s.GameName ? $" ({s.GamePackName})" : "")}{(s.Paused ? " · paused" : "")}"
        : "No game session seen since connecting";

    public string TimedEffectsText => _cc.TimedEffects is { Count: > 0 } timed
        ? string.Join(" · ", timed.Select(t => string.Create(CultureInfo.InvariantCulture, $"{t.EffectName} ({t.Status}, {t.TimeRemainingMs / 1000.0:0} s, {t.User})")))
        : "No timed effects running";

    public string LastEvent => _cc.LastEvent is { Length: > 0 } e ? e : "No event yet";

    public IReadOnlyList<string> TestKinds => _cc.TestEventKinds;

    [ObservableProperty] private string? _selectedTestKind;

    [RelayCommand]
    private void SendTest()
    {
        Error = "";
        Info = SelectedTestKind is { } kind && _cc.SendTestEvent(kind)
            ? $"Sent a local test {kind} event (isTest = true)."
            : "Pick a test event first.";
        Refresh();
    }
}
