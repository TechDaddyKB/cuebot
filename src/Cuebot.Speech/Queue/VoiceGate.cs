using Cuebot.Core.Logging;

namespace Cuebot.Speech;

/// <summary>
/// Holds the speech queue while the microphone is louder than a threshold, releasing once it has stayed below it
/// for the profile's release time, or when the input stops reporting levels (e.g. a muted or unplugged mic).
/// Does nothing without an <see cref="IAudioInput"/>. Only the start of new texts is held; the current one plays on.
/// </summary>
internal sealed class VoiceGate : IDisposable
{
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(1);

    private readonly IAudioInput? _input;
    private readonly AppLog _log;
    private readonly TimeProvider _time;
    private readonly PauseGate _gate = new();
    private readonly object _lock = new();
    private IDisposable? _monitor;
    private ITimer? _staleTimer;
    private VoiceGateProfile? _profile;
    private DateTimeOffset _lastLoud;
    private DateTimeOffset _lastLevel;

    public VoiceGate(IAudioInput? input, AppLog log, TimeProvider? time = null)
    {
        _input = input;
        _log = log;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Raised when the gate starts or stops holding.</summary>
    public event Action? Changed;

    public bool IsAvailable => _input is not null;

    public bool IsHolding => _gate.IsPaused;

    /// <summary>Name of the profile being monitored, or null when off.</summary>
    public string? ActiveProfile
    {
        get
        {
            lock (_lock) return _profile?.Name;
        }
    }

    /// <summary>Monitors with <paramref name="profile"/>, or stops (and opens) for null.</summary>
    public void Apply(VoiceGateProfile? profile)
    {
        lock (_lock)
        {
            _monitor?.Dispose();
            _monitor = null;
            _staleTimer?.Dispose();
            _staleTimer = null;
            _profile = profile is null || _input is null ? null : Cuebot.Core.Storage.Json.Clone(profile);
            if (_profile is not null && _input is not null) // without an input VoiceGate stays off
            {
                try
                {
                    _lastLevel = _time.GetUtcNow();
                    _monitor = _input.MonitorLevel(_profile.DeviceId.Length == 0 ? null : _profile.DeviceId, OnLevel);
                    _staleTimer = _time.CreateTimer(_ => CheckStale(), null, StaleAfter, StaleAfter);
                }
                catch (Exception ex)
                {
                    _log.Error("Speech", "VoiceGate could not monitor the microphone", ex);
                    _profile = null;
                }
            }
        }
        SetHolding(false);
    }

    public Task WaitOpenAsync(CancellationToken ct) => _gate.WaitAsync(ct);

    internal void OnLevel(double levelDb)
    {
        bool? hold = null;
        lock (_lock)
        {
            if (_profile is null) return;
            var now = _time.GetUtcNow();
            _lastLevel = now;
            if (levelDb >= _profile.ThresholdDb)
            {
                _lastLoud = now;
                hold = true;
            }
            else if (IsHolding && now - _lastLoud >= TimeSpan.FromMilliseconds(Math.Max(0, _profile.ReleaseMilliseconds)))
            {
                hold = false;
            }
        }
        if (hold is { } value) SetHolding(value);
    }

    /// <summary>Opens the gate when no level has been reported for <see cref="StaleAfter"/>.</summary>
    internal void CheckStale()
    {
        bool stale;
        lock (_lock) stale = IsHolding && _time.GetUtcNow() - _lastLevel >= StaleAfter;
        if (stale) SetHolding(false);
    }

    private void SetHolding(bool hold)
    {
        if (!_gate.Set(hold)) return;
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error("Speech", "A VoiceGate listener failed", ex);
        }
    }

    public void Dispose() => Apply(null);
}
