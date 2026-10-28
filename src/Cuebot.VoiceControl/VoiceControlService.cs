using Cuebot.Core;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Speech;
using Cuebot.VoiceControl.Audio;
using Cuebot.VoiceControl.Engines;
using Cuebot.VoiceControl.Matching;
using Cuebot.VoiceControl.Models;

namespace Cuebot.VoiceControl;

/// <summary>One line of the live dictation log.</summary>
/// <param name="Commands">Names of the commands the utterance fired.</param>
/// <param name="Ignored">Below the global confidence threshold (no Dictation trigger).</param>
public sealed record DictationEntry(DateTimeOffset Time, string Text, int ConfidencePercent, IReadOnlyList<string> Commands, bool Ignored);

/// <summary>On / Off / Toggle for command states and listening.</summary>
public enum VoiceSwitch { Toggle, On, Off }

/// <summary>
/// Voice control: listens to the microphone through the selected engine, matches what was said against the
/// commands and publishes <see cref="VoiceControlEvents.Command"/> / <see cref="VoiceControlEvents.Dictation"/>.
/// Nothing listens until <see cref="StartListeningAsync"/> (or Auto Start); without a microphone it reports why.
/// </summary>
public sealed class VoiceControlService : IConnectionStatus, IAsyncDisposable
{
    private const string LogSource = "Voice Control";
    private const int LogSize = 200;
    private readonly JsonFile<VoiceControlSettings> _settings;
    private readonly IReadOnlyList<ISpeechRecognizerEngine> _engines;
    private readonly IAudioInput? _input;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private readonly object _gate = new();
    private readonly LinkedList<DictationEntry> _dictation = new();
    private readonly Dictionary<long, List<CommandHit>> _fired = [];
    private readonly List<float> _resampled = [];
    private IRecognitionSession? _session;
    private IAudioInputStream? _capture;
    private Resampler? _resampler;
    private string _engineName = "";

    public VoiceControlService(JsonFile<VoiceControlSettings> settings, IEnumerable<ISpeechRecognizerEngine> engines, IAudioInput? input,
        ModelStore models, EventBus bus, AppLog log)
    {
        _settings = settings;
        _engines = engines.ToArray();
        _input = input;
        Models = models;
        _bus = bus;
        _log = log;
        Normalize(settings.Value);
    }

    public string Name => "Voice Control";
    public ConnectionState State { get; private set; } = ConnectionState.Disconnected;
    public string StatusMessage { get; private set; } = "Not listening";
    public event Action? StatusChanged;

    /// <summary>Raised when a command is added, removed, renamed, reordered or switched on/off.</summary>
    public event Action? CommandsChanged;

    /// <summary>Raised (on a background thread) for every final utterance.</summary>
    public event Action<DictationEntry>? Dictated;

    /// <summary>What the engine thinks is being said right now (interim results), or "" after the pause.</summary>
    public event Action<string>? Hearing;

    public VoiceControlSettings Settings => _settings.Value;
    public ModelStore Models { get; }
    public IReadOnlyList<ISpeechRecognizerEngine> Engines => _engines;
    public bool IsListening => _session is not null;

    /// <summary>Last microphone level while listening, dBFS (-96 when silent or stopped).</summary>
    public double LevelDb { get; private set; } = -96;

    public bool HasMicrophone => _input is not null;

    public IReadOnlyList<AudioDevice> InputDevices => _input?.GetInputDevices() ?? [];

    public IReadOnlyList<DictationEntry> DictationLog
    {
        get { lock (_dictation) return _dictation.ToArray(); }
    }

    public IReadOnlyList<VoiceCommand> Commands
    {
        get { lock (_gate) return Settings.Commands.ToArray(); }
    }

    public ISpeechRecognizerEngine CurrentEngine =>
        _engines.FirstOrDefault(e => string.Equals(e.Id, Settings.Engine, StringComparison.OrdinalIgnoreCase))
        ?? _engines.FirstOrDefault(e => e.IsAvailable)
        ?? _engines.First();

    /// <summary>Starts listening when Auto Start is on. Never throws.</summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        if (!Settings.AutoStart) return;
        try { await StartListeningAsync(ct).ConfigureAwait(false); }
        catch (Exception ex) { _log.Error(LogSource, "Could not start listening", ex); }
    }

    public async Task StartListeningAsync(CancellationToken ct = default)
    {
        await _lifecycle.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_session is not null) return;
            var engine = CurrentEngine;
            SetStatus(ConnectionState.Connecting, $"Starting {engine.DisplayName}...");
            try
            {
                if (_input is null) throw new InvalidOperationException("No audio input is available, so voice control cannot hear a microphone.");
                if (!engine.IsAvailable) throw new InvalidOperationException(engine.UnavailableReason ?? $"{engine.DisplayName} is not available.");
                var session = await engine.StartAsync(BuildOptions(), ct).ConfigureAwait(false);
                session.Recognized += OnRecognized;
                session.Failed += OnFailed;
                lock (_gate)
                {
                    _session = session;
                    _engineName = engine.Id;
                    _fired.Clear();
                }
                if (!OpenCapture(session))
                {
                    await StopCoreAsync().ConfigureAwait(false);
                    throw new InvalidOperationException("The microphone could not be opened (no audio capture backend). See the log.");
                }
                var device = string.IsNullOrWhiteSpace(Settings.InputDevice) ? "the default microphone" : Settings.InputDevice;
                SetStatus(ConnectionState.Connected, $"Listening with {session.Description} on {device}");
                _log.Info(LogSource, StatusMessage);
            }
            catch (Exception ex)
            {
                SetStatus(ConnectionState.Error, ex.Message);
                throw;
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopListeningAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_session is null && State != ConnectionState.Error) return;
            await StopCoreAsync().ConfigureAwait(false);
            SetStatus(ConnectionState.Disconnected, "Not listening");
            _log.Info(LogSource, "Stopped listening");
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public Task SetListeningAsync(VoiceSwitch mode) => (mode == VoiceSwitch.On || (mode == VoiceSwitch.Toggle && !IsListening))
        ? StartListeningAsync()
        : StopListeningAsync();

    /// <summary>Restarts with the current engine/model/locale (after the user changed them).</summary>
    public async Task RestartAsync()
    {
        if (!IsListening) return;
        await StopListeningAsync().ConfigureAwait(false);
        await StartListeningAsync().ConfigureAwait(false);
    }

    /// <summary>Switches the microphone; takes effect immediately while listening. Empty = default device.</summary>
    public void SetInputDevice(string device)
    {
        Settings.InputDevice = device.Trim();
        Save();
        IRecognitionSession? session;
        lock (_gate)
        {
            session = _session;
            _capture?.Dispose();
            _capture = null;
        }
        if (session is null) return;
        if (OpenCapture(session))
        {
            var name = string.IsNullOrWhiteSpace(Settings.InputDevice) ? "the default microphone" : Settings.InputDevice;
            SetStatus(ConnectionState.Connected, $"Listening with {session.Description} on {name}");
        }
        else
        {
            SetStatus(ConnectionState.Error, "The microphone could not be opened.");
        }
    }

    // ---- Commands ---------------------------------------------------------------------------------------------

    public VoiceCommand? FindCommand(string nameOrId)
    {
        var key = nameOrId.Trim();
        lock (_gate)
        {
            return Settings.Commands.FirstOrDefault(c => Guid.TryParse(key, out var id) && c.Id == id)
                ?? Settings.Commands.FirstOrDefault(c => string.Equals(c.Name, key, StringComparison.OrdinalIgnoreCase))
                ?? Settings.Commands.FirstOrDefault(c => string.Equals(PhraseText.Normalize(c.Phrase), PhraseText.Normalize(key), StringComparison.Ordinal)
                                                        && PhraseText.Normalize(key).Length > 0);
        }
    }

    public VoiceCommand AddCommand(string name, string phrase, CommandLocation location = CommandLocation.Exact)
    {
        var command = new VoiceCommand { Name = UniqueName(name.Trim().Length > 0 ? name.Trim() : "Voice command"), Phrase = phrase.Trim(), Location = location };
        lock (_gate) Settings.Commands.Add(command);
        OnCommandsChanged();
        return command;
    }

    public void RemoveCommand(VoiceCommand command)
    {
        lock (_gate) Settings.Commands.Remove(command);
        OnCommandsChanged();
    }

    /// <summary>Moves a command to <paramref name="index"/> (commands are checked in list order).</summary>
    public void MoveCommand(VoiceCommand command, int index)
    {
        lock (_gate)
        {
            if (!Settings.Commands.Remove(command)) return;
            Settings.Commands.Insert(Math.Clamp(index, 0, Settings.Commands.Count), command);
        }
        OnCommandsChanged();
    }

    /// <summary>Saves edits made directly on a <see cref="VoiceCommand"/> and refreshes the engine's phrase list.</summary>
    public void CommandEdited()
    {
        lock (_gate)
        {
            foreach (var c in Settings.Commands) c.ConfidenceThreshold = Math.Clamp(c.ConfidenceThreshold, 0, 100);
        }
        OnCommandsChanged();
    }

    /// <summary>Enables, disables or toggles a command by name or id; returns its new state.</summary>
    public bool SetCommandState(string nameOrId, VoiceSwitch mode)
    {
        var command = FindCommand(nameOrId) ?? throw new ArgumentException($"No voice command named '{nameOrId}'.");
        lock (_gate) command.Enabled = mode switch { VoiceSwitch.On => true, VoiceSwitch.Off => false, _ => !command.Enabled };
        OnCommandsChanged();
        return command.Enabled;
    }

    /// <summary>Changes a command's phrase (and optionally its location); creates the command when asked to.</summary>
    public VoiceCommand SetCommand(string nameOrId, string phrase, CommandLocation? location = null, bool create = false)
    {
        if (string.IsNullOrWhiteSpace(phrase)) throw new ArgumentException("The command phrase is empty.");
        var command = FindCommand(nameOrId);
        if (command is null)
        {
            if (!create) throw new ArgumentException($"No voice command named '{nameOrId}'.");
            return AddCommand(nameOrId, phrase, location ?? CommandLocation.Exact);
        }
        lock (_gate)
        {
            command.Phrase = phrase.Trim();
            if (location is { } l) command.Location = l;
        }
        OnCommandsChanged();
        return command;
    }

    public void Save() => _settings.ScheduleSave();

    // ---- Recognition ------------------------------------------------------------------------------------------

    internal RecognitionOptions BuildOptions()
    {
        lock (_gate)
        {
            var enabled = Settings.Commands.Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Phrase)).ToArray();
            return new RecognitionOptions(
                Settings.Locale,
                enabled.Select(c => c.Phrase.Trim()).ToArray(),
                enabled.Where(c => c.Location == CommandLocation.Exact).Select(c => c.Phrase.Trim()).ToArray(),
                Settings.Whisper.InterimResults && CommandEvaluator.NeedsInterimResults(enabled));
        }
    }

    private bool OpenCapture(IRecognitionSession session)
    {
        var capture = _input?.CaptureSamples(string.IsNullOrWhiteSpace(Settings.InputDevice) ? null : Settings.InputDevice, OnSamples);
        if (capture is null) return false;
        lock (_gate)
        {
            _resampler = new Resampler(capture.SampleRate, session.SampleRate);
            _capture = capture;
        }
        return true;
    }

    /// <summary>Audio thread: resample to the engine's rate and hand over.</summary>
    private void OnSamples(ReadOnlySpan<float> samples)
    {
        IRecognitionSession? session;
        float[] converted;
        lock (_gate)
        {
            session = _session;
            if (session is null || _resampler is null) return;
            _resampled.Clear();
            _resampler.Process(samples, _resampled);
            converted = [.. _resampled];
        }
        double sum = 0;
        foreach (var s in samples) sum += (double)s * s;
        LevelDb = samples.Length == 0 || sum <= 0 ? -96 : Math.Max(-96, 10 * Math.Log10(sum / samples.Length));
        try { session.Accept(converted); }
        catch (Exception ex) { OnFailed(ex); }
    }

    internal void OnRecognized(RecognitionResult result)
    {
        try
        {
            List<CommandHit> fired;
            Guid[] firedIds;
            VoiceCommand[] commands;
            string engine;
            lock (_gate)
            {
                if (!_fired.TryGetValue(result.UtteranceId, out fired!)) _fired[result.UtteranceId] = fired = [];
                foreach (var old in _fired.Keys.Where(k => k < result.UtteranceId - 8).ToArray()) _fired.Remove(old);
                firedIds = fired.Select(h => h.Command.Id).ToArray();
                commands = [.. Settings.Commands];
                engine = _engineName;
            }

            var hits = CommandEvaluator.Evaluate(result, commands, Settings.ConfidenceThreshold, Settings.MatchTolerance, firedIds);
            foreach (var hit in hits)
            {
                lock (_gate) fired.Add(hit);
                _bus.Publish(new BotEvent(VoiceControlEvents.Command, VoiceControlArgs.ForCommand(result, hit, engine)));
                _log.Info(LogSource, $"Command \"{hit.Command.Name}\" ({VoiceControlArgs.Percent(hit.Confidence)}%){(result.IsFinal ? "" : " while speaking")}");
            }

            if (!result.IsFinal)
            {
                Hearing?.Invoke(result.Text);
                return;
            }
            Hearing?.Invoke("");

            var percent = VoiceControlArgs.Percent(result.Confidence);
            var ignored = percent < Math.Clamp(Settings.ConfidenceThreshold, 0, 100);
            CommandHit[] all;
            lock (_gate)
            {
                all = [.. fired];
                _fired.Remove(result.UtteranceId);
            }
            var names = all.Select(h => h.Command.Name).ToArray();
            var entry = new DictationEntry(DateTimeOffset.Now, result.Text, percent, names, ignored);
            lock (_dictation)
            {
                _dictation.AddLast(entry);
                while (_dictation.Count > LogSize) _dictation.RemoveFirst();
            }
            if (Settings.LogDictation)
                _log.Info(LogSource, $"Heard \"{result.Text}\" ({percent}%{(ignored ? ", below threshold" : "")})");
            Dictated?.Invoke(entry);
            if (!ignored)
            {
                _bus.Publish(new BotEvent(VoiceControlEvents.Dictation, VoiceControlArgs.ForDictation(result, all, engine)));
            }
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, "Could not handle a recognised phrase", ex);
        }
    }

    private void OnFailed(Exception ex)
    {
        _log.Error(LogSource, "The speech engine stopped", ex);
        _ = Task.Run(async () =>
        {
            await _lifecycle.WaitAsync().ConfigureAwait(false);
            try { await StopCoreAsync().ConfigureAwait(false); }
            finally { _lifecycle.Release(); }
            SetStatus(ConnectionState.Error, $"The speech engine stopped: {ex.Message}");
        });
    }

    private async Task StopCoreAsync()
    {
        IRecognitionSession? session;
        lock (_gate)
        {
            _capture?.Dispose();
            _capture = null;
            session = _session;
            _session = null;
            _resampler = null;
        }
        LevelDb = -96;
        if (session is null) return;
        session.Recognized -= OnRecognized;
        session.Failed -= OnFailed;
        try { await session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _log.Error(LogSource, "Could not stop the speech engine cleanly", ex); }
    }

    private void OnCommandsChanged()
    {
        Save();
        IRecognitionSession? session;
        lock (_gate) session = _session;
        try { session?.UpdateOptions(BuildOptions()); }
        catch (Exception ex) { _log.Error(LogSource, "Could not update the engine's command list", ex); }
        CommandsChanged?.Invoke();
    }

    private string UniqueName(string name)
    {
        lock (_gate)
        {
            if (Settings.Commands.All(c => !string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))) return name;
            for (var i = 2; ; i++)
            {
                var candidate = $"{name} ({i})";
                if (Settings.Commands.All(c => !string.Equals(c.Name, candidate, StringComparison.OrdinalIgnoreCase))) return candidate;
            }
        }
    }

    private static void Normalize(VoiceControlSettings s)
    {
        s.ConfidenceThreshold = Math.Clamp(s.ConfidenceThreshold, 0, 100);
        s.MatchTolerance = Math.Clamp(s.MatchTolerance, 0, 50);
        s.Commands ??= [];
        s.Whisper ??= new WhisperSettings();
        s.Vad ??= new VoiceActivitySettings();
    }

    private void SetStatus(ConnectionState state, string message)
    {
        State = state;
        StatusMessage = message;
        StatusChanged?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        await _lifecycle.WaitAsync().ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _lifecycle.Release(); }
        _lifecycle.Dispose();
    }
}
