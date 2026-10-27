using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Integrations.Common;

namespace Cuebot.Integrations.Voicemod;

/// <summary>Bot event types published by the Voicemod integration.</summary>
public static class VoicemodEvents
{
    public const string Prefix = "voicemod";
    public const string Connected = Prefix + ".connected";
    public const string Disconnected = Prefix + ".disconnected";
    public const string VoiceChanged = Prefix + ".voiceChanged";
    public const string VoiceChangerToggled = Prefix + ".voiceChangerToggled";
    public const string HearMyselfToggled = Prefix + ".hearMyselfToggled";
    public const string BackgroundEffectsToggled = Prefix + ".backgroundEffectsToggled";
    public const string MuteToggled = Prefix + ".muteToggled";
    public const string MemesMutedForMeToggled = Prefix + ".memesMutedForMeToggled";
    public const string SoundboardChanged = Prefix + ".soundboardChanged";
    public const string LicenseChanged = Prefix + ".licenseChanged";
}

/// <summary>The four Voicemod switches the Control API can toggle.</summary>
public enum VoicemodSwitch { VoiceChanger, HearMyself, BackgroundEffects, MuteMic }

/// <summary>
/// The Voicemod Control API connection (ws://localhost:59129/v1, cycling through the documented fallback ports):
/// registerClient with the user's client key, cached voices/sounds/switch states, change events and commands.
/// </summary>
public sealed class VoicemodService : IntegrationConnection
{
    private readonly JsonFile<IntegrationsSettings> _settings;
    private readonly PendingRequests _pending = new();
    private readonly object _gate = new();
    private VoicemodVoice[] _voices = [];
    private VoicemodSound[] _sounds = [];
    private string? _currentVoiceId;
    private readonly bool?[] _switches = new bool?[4];

    public VoicemodService(JsonFile<IntegrationsSettings> settings, EventBus bus, AppLog log)
        : this(settings, bus, log, () => new ClientWebSocketTransport(), IntegrationsTimings.Default)
    {
    }

    internal VoicemodService(JsonFile<IntegrationsSettings> settings, EventBus bus, AppLog log,
        Func<IWebSocketTransport> transports, IntegrationsTimings timings)
        : base("Voicemod", bus, log, transports, timings)
    {
        _settings = settings;
    }

    public VoicemodSettings Settings => _settings.Value.Voicemod;

    public void SaveSettings() => _settings.ScheduleSave();

    public IReadOnlyList<VoicemodVoice> Voices => _voices;
    public IReadOnlyList<VoicemodSound> Sounds => _sounds;
    public string? CurrentVoiceId => _currentVoiceId;

    public string CurrentVoiceName => _currentVoiceId is null ? ""
        : _voices.FirstOrDefault(v => v.Id == _currentVoiceId)?.DisplayName ?? _currentVoiceId;

    public bool? GetSwitch(VoicemodSwitch which) => _switches[(int)which];

    /// <summary>The port of the current or last session.</summary>
    public int? ConnectedPort { get; private set; }

    /// <summary>Raised (background thread) when voices, sounds, the current voice or a switch changes.</summary>
    public event Action? DataChanged;

    protected override string EventPrefix => VoicemodEvents.Prefix;
    protected override bool AutoConnect => Settings.AutoConnect;
    protected override IEnumerable<string?> SecretValues => [Settings.ClientKey];

    protected override string? MissingSettings() =>
        Settings.ClientKey.Trim().Length == 0 ? "Enter your Voicemod Control API client key first." : null;

    private protected override IReadOnlyList<IntegrationEndpoint> Endpoints()
    {
        var host = Settings.Host.Trim().Length > 0 ? Settings.Host.Trim() : "localhost";
        var ports = new List<int>();
        if (Settings.Port is > 0 and < 65536) ports.Add(Settings.Port);
        if (Settings.Port <= 0 || Settings.TryAllPorts)
        {
            if (Settings.LastPort is > 0 and < 65536) ports.Add(Settings.LastPort);
            ports.AddRange(VoicemodProtocol.Ports);
        }
        return ports.Distinct().Select(p => new IntegrationEndpoint(new Uri($"ws://{host}:{p}/v1"), $"{host}:{p}")).ToArray();
    }

    private protected override void OnEndpointOpened(IntegrationEndpoint endpoint)
    {
        ConnectedPort = endpoint.Url.Port;
        if (Settings.LastPort != endpoint.Url.Port)
        {
            Settings.LastPort = endpoint.Url.Port;
            SaveSettings();
        }
    }

    // ---- Session ----

    private protected override async Task RunSessionAsync(IWebSocketTransport transport, IntegrationEndpoint endpoint, CancellationToken ct)
    {
        using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pump = PumpAsync(transport, session.Token);
        try
        {
            var register = await RequestCoreAsync((t, c) => transport.SendAsync(t, c), "registerClient",
                new { clientKey = Settings.ClientKey.Trim() }, ct).ConfigureAwait(false);
            var (ok, description) = VoicemodProtocol.RegisterResult(register);
            if (!ok) throw new IntegrationAuthException($"Voicemod rejected the client key ({description}). Check the key and click Connect.");
            MarkConnected($"Connected to Voicemod on port {endpoint.Url.Port}", new Dictionary<string, object?> { ["port"] = endpoint.Url.Port });
            await LoadStateAsync(ct).ConfigureAwait(false);
            await pump.ConfigureAwait(false);
        }
        finally
        {
            await session.CancelAsync().ConfigureAwait(false);
            try
            {
                await pump.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Session over.
            }
        }
    }

    private async Task LoadStateAsync(CancellationToken ct)
    {
        foreach (var action in new[] { "getVoices", "getCurrentVoice", "getMemes", "getVoiceChangerStatus", "getHearMyselfStatus",
                     "getBackgroundEffectStatus", "getMuteMicStatus" })
        {
            try
            {
                await RequestAsync(action, null, ct).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                Log.Debug(Name, ex.Message);
            }
        }
    }

    private async Task PumpAsync(IWebSocketTransport transport, CancellationToken ct)
    {
        try
        {
            while (await transport.ReceiveAsync(ct).ConfigureAwait(false) is { } text) Handle(text);
        }
        finally
        {
            _pending.FailAll(new IntegrationNotConnectedException(Name));
        }
    }

    private void Handle(string text)
    {
        if (VoicemodProtocol.Parse(text) is not { } message) return;
        var solicited = _pending.Complete(message.Id, message.Kind, message.Root);
        Apply(message, solicited);
    }

    private protected override void OnSessionEnded()
    {
        lock (_gate)
        {
            _currentVoiceId = null;
            Array.Clear(_switches);
        }
        DataChanged?.Invoke();
    }

    /// <summary>Updates the caches from a message and publishes change events (not for answers to our own status requests
    /// unless the state really changed).</summary>
    private void Apply(VoicemodMessage message, bool solicited)
    {
        switch (message.Kind)
        {
            case "getVoices":
                _voices = VoicemodProtocol.ParseVoices(message);
                if (VoicemodProtocol.CurrentVoiceOf(message) is { Length: > 0 } current) SetVoice(current, solicited);
                break;
            case "getCurrentVoice" or "voiceChangedEvent" or "voiceLoadedEvent" or "setCurrentVoiceParameter":
                if (message.Body.Str("voiceID", "voiceId") is { Length: > 0 } voice) SetVoice(voice, solicited);
                break;
            case "toggleVoiceChanger":
                SetSwitch(VoicemodSwitch.VoiceChanger, VoicemodProtocol.Value(message), solicited);
                break;
            case "voiceChangerEnabledEvent":
                SetSwitch(VoicemodSwitch.VoiceChanger, true, solicited);
                break;
            case "voiceChangerDisabledEvent":
                SetSwitch(VoicemodSwitch.VoiceChanger, false, solicited);
                break;
            case "toggleHearMyVoice":
                SetSwitch(VoicemodSwitch.HearMyself, VoicemodProtocol.Value(message), solicited);
                break;
            case "toggleBackground":
                SetSwitch(VoicemodSwitch.BackgroundEffects, VoicemodProtocol.Value(message), solicited);
                break;
            case "toggleMuteMic":
                SetSwitch(VoicemodSwitch.MuteMic, VoicemodProtocol.Value(message), solicited);
                break;
            case "toggleMuteMemeForMe":
                if (VoicemodProtocol.Value(message) is { } memesMuted && !solicited)
                    Publish("memesMutedForMeToggled", new Dictionary<string, object?> { ["enabled"] = memesMuted });
                break;
            case "getMemes":
                _sounds = VoicemodProtocol.ParseMemes(message);
                if (!solicited) Publish("soundboardChanged", new Dictionary<string, object?> { ["soundCount"] = _sounds.Length });
                break;
            case "getAllSoundboard":
                if (!solicited) Publish("soundboardChanged", new Dictionary<string, object?>
                {
                    ["soundCount"] = VoicemodProtocol.ParseSoundboards(message).Length,
                });
                break;
            case "getActiveSoundboardProfile":
                Publish("soundboardChanged", new Dictionary<string, object?> { ["profileId"] = message.Body.Str("profileId") });
                break;
            case "licenseTypeChanged":
                Publish("licenseChanged", new Dictionary<string, object?> { ["licenseType"] = message.Body.Str("licenseType") });
                break;
            default:
                return;
        }
        DataChanged?.Invoke();
    }

    private void SetVoice(string voiceId, bool solicited)
    {
        string? previous;
        lock (_gate)
        {
            previous = _currentVoiceId;
            if (previous == voiceId) return;
            _currentVoiceId = voiceId;
        }
        if (previous is null && solicited) return;
        var previousName = previous is null ? "" : _voices.FirstOrDefault(v => v.Id == previous)?.DisplayName ?? previous;
        Publish("voiceChanged", new Dictionary<string, object?>
        {
            ["voiceId"] = voiceId,
            ["voiceName"] = CurrentVoiceName,
            ["previousVoiceId"] = previous ?? "",
            ["previousVoiceName"] = previousName,
        });
    }

    private void SetSwitch(VoicemodSwitch which, bool? value, bool solicited)
    {
        if (value is not { } on) return;
        bool? previous;
        lock (_gate)
        {
            previous = _switches[(int)which];
            _switches[(int)which] = on;
        }
        if (previous == on || (previous is null && solicited)) return;
        var (eventName, arg) = which switch
        {
            VoicemodSwitch.VoiceChanger => ("voiceChangerToggled", "enabled"),
            VoicemodSwitch.HearMyself => ("hearMyselfToggled", "enabled"),
            VoicemodSwitch.BackgroundEffects => ("backgroundEffectsToggled", "enabled"),
            _ => ("muteToggled", "muted"),
        };
        var args = new Dictionary<string, object?> { [arg] = on };
        if (arg != "enabled") args["enabled"] = on;
        Publish(eventName, args);
    }

    // ---- Requests ----

    /// <summary>Sends an action and waits for its answer (status/list actions) or just sends it (commands).</summary>
    private Task<VoicemodMessage> RequestAsync(string action, object? payload, CancellationToken ct)
    {
        EnsureConnected();
        return RequestCoreAsync(SendAsync, action, payload, ct);
    }

    private async Task<VoicemodMessage> RequestCoreAsync(Func<string, CancellationToken, Task> send, string action, object? payload, CancellationToken ct)
    {
        var id = Guid.NewGuid().ToString();
        var kind = VoicemodProtocol.ResponseKind(action) ?? throw new ArgumentException($"{action} has no response.");
        var response = _pending.Add(id, kind);
        try
        {
            await send(VoicemodProtocol.Request(action, id, payload), ct).ConfigureAwait(false);
            var root = await response.WaitAsync(Timings.RequestTimeout, ct).ConfigureAwait(false);
            return VoicemodProtocol.Parse(root.GetRawText()) ?? throw new IntegrationRequestException($"Voicemod sent an unreadable answer to {action}.");
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Voicemod did not answer {action} in time.");
        }
        finally
        {
            _pending.Remove(id);
        }
    }

    private Task SendCommandAsync(string action, object? payload, CancellationToken ct) =>
        SendAsync(VoicemodProtocol.Request(action, Guid.NewGuid().ToString(), payload), ct);

    // ---- Commands ----

    public async Task<IReadOnlyList<VoicemodVoice>> GetVoicesAsync(CancellationToken ct = default)
    {
        await RequestAsync("getVoices", null, ct).ConfigureAwait(false);
        return _voices;
    }

    public async Task<IReadOnlyList<VoicemodSound>> GetSoundsAsync(CancellationToken ct = default)
    {
        await RequestAsync("getMemes", null, ct).ConfigureAwait(false);
        return _sounds;
    }

    /// <summary>Asks Voicemod for the current voice; returns (id, name).</summary>
    public async Task<(string Id, string Name)> GetCurrentVoiceAsync(CancellationToken ct = default)
    {
        var message = await RequestAsync("getCurrentVoice", null, ct).ConfigureAwait(false);
        var id = message.Body.Str("voiceID", "voiceId");
        if (id.Length == 0) id = _currentVoiceId ?? "";
        if (_voices.Length == 0) await GetVoicesAsync(ct).ConfigureAwait(false);
        return (id, _voices.FirstOrDefault(v => v.Id == id)?.DisplayName ?? id);
    }

    /// <summary>Loads a voice by name or id; returns it.</summary>
    public async Task<VoicemodVoice> SelectVoiceAsync(string nameOrId, CancellationToken ct = default)
    {
        nameOrId = Steps.Require(nameOrId, "Voice");
        var voice = FindVoice(nameOrId);
        if (voice is null)
        {
            await GetVoicesAsync(ct).ConfigureAwait(false);
            voice = FindVoice(nameOrId) ?? throw new ArgumentException($"Voicemod has no voice named '{nameOrId}'.");
        }
        await SendCommandAsync("loadVoice", new { voiceID = voice.Id }, ct).ConfigureAwait(false);
        return voice;
    }

    private VoicemodVoice? FindVoice(string nameOrId) =>
        _voices.FirstOrDefault(v => v.Id == nameOrId)
        ?? _voices.FirstOrDefault(v => string.Equals(v.Name, nameOrId, StringComparison.OrdinalIgnoreCase))
        ?? _voices.FirstOrDefault(v => string.Equals(v.Id, nameOrId, StringComparison.OrdinalIgnoreCase));

    /// <summary>Mode: AllVoices, FreeVoices, FavoriteVoices or CustomVoices.</summary>
    public Task SelectRandomVoiceAsync(string mode = "AllVoices", CancellationToken ct = default) =>
        SendCommandAsync("selectRandomVoice", new { mode = mode.Trim().Length > 0 ? mode.Trim() : "AllVoices" }, ct);

    /// <summary>Sets a switch; Voicemod can only toggle, so the current state is asked first. Returns the new state.</summary>
    public async Task<bool> SetSwitchAsync(VoicemodSwitch which, SwitchMode mode, CancellationToken ct = default)
    {
        var (status, toggle) = which switch
        {
            VoicemodSwitch.VoiceChanger => ("getVoiceChangerStatus", "toggleVoiceChanger"),
            VoicemodSwitch.HearMyself => ("getHearMyselfStatus", "toggleHearMyVoice"),
            VoicemodSwitch.BackgroundEffects => ("getBackgroundEffectStatus", "toggleBackground"),
            _ => ("getMuteMicStatus", "toggleMuteMic"),
        };
        var current = VoicemodProtocol.Value(await RequestAsync(status, null, ct).ConfigureAwait(false)) ?? GetSwitch(which) ?? false;
        var desired = Steps.Desired(mode, current);
        if (desired != current) await SendCommandAsync(toggle, null, ct).ConfigureAwait(false);
        return desired;
    }

    /// <summary>Plays a soundboard sound (meme) by name or file name; returns it.</summary>
    public async Task<VoicemodSound> PlaySoundAsync(string nameOrFile, CancellationToken ct = default)
    {
        nameOrFile = Steps.Require(nameOrFile, "Sound");
        var sound = FindSound(nameOrFile);
        if (sound is null)
        {
            await GetSoundsAsync(ct).ConfigureAwait(false);
            sound = FindSound(nameOrFile) ?? throw new ArgumentException($"Voicemod has no sound named '{nameOrFile}'.");
        }
        await SendCommandAsync("playMeme", new { FileName = sound.FileName, IsKeyDown = true }, ct).ConfigureAwait(false);
        return sound;
    }

    private VoicemodSound? FindSound(string nameOrFile) =>
        _sounds.FirstOrDefault(s => s.FileName == nameOrFile)
        ?? _sounds.FirstOrDefault(s => string.Equals(s.Name, nameOrFile, StringComparison.OrdinalIgnoreCase));

    public Task StopAllSoundsAsync(CancellationToken ct = default) => SendCommandAsync("stopAllMemeSounds", null, ct);

    /// <summary>Starts (true) or ends (false) the censor beep.</summary>
    public Task SetBeepAsync(bool on, CancellationToken ct = default) => SendCommandAsync("setBeepSound", new { badLanguage = on ? 1 : 0 }, ct);
}
