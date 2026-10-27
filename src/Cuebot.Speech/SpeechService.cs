using System.Threading.Channels;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Speech;

/// <summary>
/// Speaks texts one at a time from a queue, reads chat aloud when enabled, and reports its state to the UI.
/// </summary>
public sealed class SpeechService : IAsyncDisposable
{
    private const string LogSource = "Speech";

    private readonly JsonFile<SpeechSettings> _settings;
    private readonly VoiceResolver _voices;
    private readonly IAudioPlayer _player;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    // Not SingleReader: that variant cannot report Count, and ClearQueue reads from another thread anyway.
    private readonly Channel<SpeechRequest> _queue = Channel.CreateUnbounded<SpeechRequest>();
    private readonly PauseGate _pause = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private CancellationTokenSource? _currentPlayback;
    private string? _currentText;
    private Task? _worker;
    private IDisposable? _chatSubscription;

    public SpeechService(JsonFile<SpeechSettings> settings, IEnumerable<ITtsEngine> engines, IAudioPlayer player,
        EventBus bus, AppLog log)
    {
        _settings = settings;
        Engines = engines.ToArray();
        _voices = new VoiceResolver(Engines);
        _player = player;
        _bus = bus;
        _log = log;
    }

    /// <summary>Raised from background threads whenever the queue, current text or pause state changes.</summary>
    public event Action? StateChanged;

    /// <summary>Live settings; call <see cref="SaveSettings"/> after changing them.</summary>
    public SpeechSettings Settings => _settings.Value;

    public IReadOnlyList<ITtsEngine> Engines { get; }

    /// <summary>Number of texts waiting (not counting the one playing).</summary>
    public int QueueLength => _queue.Reader.Count;

    /// <summary>Text currently being synthesized or played, or null when idle.</summary>
    public string? CurrentText
    {
        get
        {
            lock (_gate) return _currentText;
        }
    }

    /// <summary>While paused, the queue keeps filling but nothing new starts playing.</summary>
    public bool Paused
    {
        get => _pause.IsPaused;
        set
        {
            if (_pause.Set(value)) RaiseStateChanged();
        }
    }

    public void SaveSettings() => _settings.ScheduleSave();

    /// <summary>Starts processing the queue and listening to chat. Calling it again does nothing.</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_worker is not null) return Task.CompletedTask;
            _chatSubscription = _bus.Subscribe(OnEvent);
            _worker = Task.Run(() => RunQueueAsync(_shutdown.Token), CancellationToken.None);
        }
        return Task.CompletedTask;
    }

    /// <summary>Queues <paramref name="text"/>.</summary>
    /// <param name="alias">Alias name; null or unknown uses the default voice.</param>
    /// <param name="wait">Complete only once the text has been spoken (or skipped). When false, the task completes
    /// as soon as the text is queued and <paramref name="ct"/> does not affect it afterwards.</param>
    public Task SpeakAsync(string text, string? alias = null, bool wait = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) return Task.CompletedTask;
        var request = Enqueue(new SpeechRequest(text, alias, voice: null, wait ? ct : CancellationToken.None));
        return wait ? request.Completion : Task.CompletedTask;
    }

    /// <summary>Queues <paramref name="text"/> with an explicit alias (e.g. one being edited) and waits for it.</summary>
    public Task TestVoiceAsync(VoiceAlias alias, string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Enqueue(new SpeechRequest(text, aliasName: null, Json.Clone(alias), ct)).Completion;
    }

    /// <summary>Stops the text that is playing; the queue continues with the next one.</summary>
    public void SkipCurrent()
    {
        lock (_gate) _currentPlayback?.Cancel();
    }

    /// <summary>Drops every waiting text (the one playing is not affected). Waiting callers see cancellation.</summary>
    public void ClearQueue()
    {
        while (_queue.Reader.TryRead(out var request)) request.Cancel();
        RaiseStateChanged();
    }

    public async ValueTask DisposeAsync()
    {
        Task? worker;
        lock (_gate)
        {
            _chatSubscription?.Dispose();
            worker = _worker;
        }
        _queue.Writer.TryComplete();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (worker is not null) await worker.ConfigureAwait(false);
        ClearQueue();
        _shutdown.Dispose();
    }

    /// <summary>Volume applied to a WAV: alias volume scaled by master volume, both 0-100.</summary>
    internal static int CombineVolume(int aliasVolume, int masterVolume) =>
        Math.Clamp(aliasVolume, 0, 100) * Math.Clamp(masterVolume, 0, 100) / 100;

    private SpeechRequest Enqueue(SpeechRequest request)
    {
        if (!_queue.Writer.TryWrite(request)) throw new ObjectDisposedException(nameof(SpeechService));
        RaiseStateChanged();
        return request;
    }

    private void OnEvent(BotEvent e)
    {
        if (e.Type != SpeechEvents.TwitchChatMessage) return;
        var chat = Settings.SpeakChat;
        if (!chat.Enabled) return;
        if (ChatSpeech.BuildUtterance(e, chat) is { } text) _ = SpeakAsync(text, chat.Alias);
    }

    private async Task RunQueueAsync(CancellationToken shutdown)
    {
        try
        {
            while (await _queue.Reader.WaitToReadAsync(shutdown).ConfigureAwait(false))
            {
                await _pause.WaitAsync(shutdown).ConfigureAwait(false);
                if (_queue.Reader.TryRead(out var request) && !request.IsFinished)
                    await PlayAsync(request, shutdown).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task PlayAsync(SpeechRequest request, CancellationToken shutdown)
    {
        using var playback = CancellationTokenSource.CreateLinkedTokenSource(shutdown, request.Cancellation);
        SetCurrent(request.Text, playback);
        var aliasName = "";
        var started = false;
        Exception? failure = null;
        var cancelled = false;
        try
        {
            var text = PrepareText(request.Text);
            if (text.Length == 0) return;
            var voice = request.Voice is { } explicitVoice
                ? _voices.Resolve(explicitVoice)
                : await _voices.ResolveAsync(request.AliasName, Settings.Aliases.ToArray(), playback.Token).ConfigureAwait(false);
            aliasName = voice.AliasName;
            var wav = await voice.Engine.SynthesizeAsync(text, voice.VoiceId, voice.Rate, playback.Token).ConfigureAwait(false);
            wav = Wav.ApplyVolume(wav, CombineVolume(voice.Volume, Settings.MasterVolume));
            started = true;
            Publish(SpeechEvents.Started, request.Text, aliasName);
            await _player.PlayAsync(wav, playback.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (playback.IsCancellationRequested
            && !shutdown.IsCancellationRequested && !request.Cancellation.IsCancellationRequested)
        {
            // Skipped by the user: counts as done.
        }
        catch (OperationCanceledException) when (playback.IsCancellationRequested)
        {
            cancelled = true;
        }
        catch (Exception ex)
        {
            failure = ex;
            _log.Error(LogSource, $"Could not speak \"{Preview(request.Text)}\"", ex);
        }
        finally
        {
            SetCurrent(null, null);
            if (started) Publish(SpeechEvents.Finished, request.Text, aliasName);
            if (failure is not null) request.Fail(failure);
            else if (cancelled) request.Cancel();
            else request.Complete();
        }
    }

    private string PrepareText(string text) =>
        TextFilter.CollapseWhitespace(TextFilter.ApplyReplacements(text, Settings.Replacements.ToArray()));

    private void SetCurrent(string? text, CancellationTokenSource? playback)
    {
        lock (_gate)
        {
            _currentText = text;
            _currentPlayback = playback;
        }
        RaiseStateChanged();
    }

    private void Publish(string type, string text, string alias) =>
        _bus.Publish(new BotEvent(type, new Dictionary<string, object?> { ["text"] = text, ["alias"] = alias }));

    private void RaiseStateChanged()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, "A speech state listener failed", ex);
        }
    }

    private static string Preview(string text) => text.Length <= 60 ? text : text[..60] + "...";
}
