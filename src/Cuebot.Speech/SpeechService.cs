using System.Diagnostics;
using Cuebot.Core.Actions;
using Cuebot.Core.Chat.Moderation;
using Cuebot.Core.Events;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;

namespace Cuebot.Speech;

/// <summary>Sends chat replies for "!tts" commands.</summary>
public interface ISpeechChatReplier
{
    Task ReplyAsync(string platform, string text, CancellationToken ct);
}

/// <summary>One entry of the session's speech history.</summary>
public sealed record SpeechHistoryEntry(DateTimeOffset Time, string Text, string Alias, string EngineId, string VoiceId,
    string User, string Source, bool Skipped);

/// <summary>
/// Speaks texts one at a time from a queue; reads chat and activity aloud when enabled; handles "!tts" commands,
/// moderation (deleted messages, bans, timeouts) and VoiceGate; reports its state to the UI.
/// </summary>
public sealed class SpeechService : IAsyncDisposable
{
    private const string LogSource = "Speech";
    private const int HistorySize = 100;
    internal static readonly TimeSpan SilentTimeout = TimeSpan.FromSeconds(30);

    private readonly JsonFile<SpeechSettings> _settings;
    private readonly VoiceResolver _voices;
    private readonly IAudioPlayer _player;
    private readonly EventBus _bus;
    private readonly AppLog _log;
    private readonly ISpeechChatReplier? _chat;
    private readonly IReadOnlyList<IEmoteNameProvider> _emoteProviders;
    private readonly IReadOnlyList<IChannelRoles> _roles;
    private readonly TimeProvider _time;
    private readonly SpeechQueue _queue = new();
    private readonly PauseGate _pause = new();
    private readonly VoiceGate _voiceGate;
    private readonly EventSpeech _eventSpeech;
    private readonly TtsCommands _commands;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _lastSpoke = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<SpeechHistoryEntry> _history = new();
    private SpeechRequest? _current;
    private CancellationTokenSource? _currentPlayback;
    private Task? _worker;
    private IDisposable? _subscription;

    public SpeechService(JsonFile<SpeechSettings> settings, IEnumerable<ITtsEngine> engines, IAudioPlayer player,
        EventBus bus, AppLog log, SpeechUserDirectory? users = null, ISpeechChatReplier? chat = null,
        IAudioInput? audioInput = null, IEnumerable<IEmoteNameProvider>? emoteProviders = null, TimeProvider? time = null,
        Random? random = null, IEnumerable<IChannelRoles>? roles = null)
    {
        _settings = settings;
        _roles = roles?.ToArray() ?? [];
        Engines = engines.ToArray();
        _voices = new VoiceResolver(Engines, random);
        _player = player;
        _bus = bus;
        _log = log;
        _chat = chat;
        _emoteProviders = emoteProviders?.ToArray() ?? [];
        _time = time ?? TimeProvider.System;
        Users = users ?? SpeechUserDirectory.InMemory();
        _voiceGate = new VoiceGate(audioInput, log, _time);
        _voiceGate.Changed += () => RaiseStateChanged(publish: true);
        _eventSpeech = new EventSpeech(random);
        _commands = new TtsCommands(this, _time);
    }

    /// <summary>Raised from background threads whenever the queue, current text or pause state changes.</summary>
    public event Action? StateChanged;

    /// <summary>Live settings; call <see cref="SaveSettings"/> after changing them.</summary>
    public SpeechSettings Settings => _settings.Value;

    public IReadOnlyList<ITtsEngine> Engines { get; }

    /// <summary>Per-user voices, nicknames and ignore flags.</summary>
    public SpeechUserDirectory Users { get; }

    /// <summary>Number of texts waiting (not counting the one playing).</summary>
    public int QueueLength => _queue.Count;

    /// <summary>The waiting texts in the order they will play.</summary>
    public IReadOnlyList<QueuedSpeech> Queue =>
        _queue.Snapshot().Select(r => new QueuedSpeech(r.Id, r.Text, r.Options.Alias ?? r.Voice?.Name ?? "",
            r.Options.UserName, r.Options.Priority, r.Options.Source)).ToArray();

    /// <summary>What was spoken this session, newest first.</summary>
    public IReadOnlyList<SpeechHistoryEntry> History
    {
        get
        {
            lock (_history) return _history.ToArray();
        }
    }

    /// <summary>Text currently being synthesized or played, or null when idle.</summary>
    public string? CurrentText
    {
        get
        {
            lock (_gate) return _current?.Text;
        }
    }

    /// <summary>While paused, the queue keeps filling but nothing new starts playing.</summary>
    public bool Paused
    {
        get => _pause.IsPaused;
        set
        {
            if (_pause.Set(value)) RaiseStateChanged(publish: true);
        }
    }

    /// <summary>Master switch; turning it off stops the current text and clears the queue.</summary>
    public bool Enabled
    {
        get => Settings.Enabled;
        set
        {
            if (Settings.Enabled == value) return;
            Settings.Enabled = value;
            SaveSettings();
            if (!value)
            {
                ClearQueue();
                SkipCurrent();
            }
            RaiseStateChanged(publish: true);
        }
    }

    /// <summary>Whether activity (follows, subs...) is spoken.</summary>
    public bool EventsEnabled
    {
        get => Settings.Events.Enabled;
        set
        {
            if (Settings.Events.Enabled == value) return;
            Settings.Events.Enabled = value;
            SaveSettings();
            RaiseStateChanged(publish: true);
        }
    }

    /// <summary>True when an audio input exists, so VoiceGate can work.</summary>
    public bool VoiceGateAvailable => _voiceGate.IsAvailable;

    /// <summary>True while VoiceGate holds the queue because the microphone is loud.</summary>
    public bool VoiceGateHolding => _voiceGate.IsHolding;

    public IReadOnlyList<AudioDevice> OutputDevices => _player.GetOutputDevices();

    internal ISpeechChatReplier? Chat => _chat;

    internal AppLog Log => _log;

    public void SaveSettings() => _settings.ScheduleSave();

    /// <summary>Starts processing the queue and listening to events. Calling it again does nothing.</summary>
    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_worker is not null) return Task.CompletedTask;
            _subscription = _bus.Subscribe(OnEvent);
            _worker = Task.Run(() => RunQueueAsync(_shutdown.Token), CancellationToken.None);
        }
        ActivateVoiceGateProfile(Settings.VoiceGate.ActiveProfile);
        return Task.CompletedTask;
    }

    /// <summary>Queues <paramref name="text"/>.</summary>
    /// <param name="alias">Alias name; null or unknown uses the default voice.</param>
    /// <param name="wait">Complete only once the text has been spoken (or skipped). When false, the task completes
    /// as soon as the text is queued and <paramref name="ct"/> does not affect it afterwards.</param>
    public Task SpeakAsync(string text, string? alias = null, bool wait = false, CancellationToken ct = default) =>
        SpeakAsync(text, new SpeakOptions { Alias = alias }, wait, ct);

    /// <summary>
    /// Queues <paramref name="text"/> (or, with <see cref="SpeakOptions.Silent"/>, renders it right away without
    /// playing). With <paramref name="wait"/> the result carries the duration and saved file; otherwise
    /// <see cref="SpeechResult.None"/> is returned once queued. Disabled speech returns a skipped result.
    /// </summary>
    public Task<SpeechResult> SpeakAsync(string text, SpeakOptions options, bool wait = false, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text)) return Task.FromResult(SpeechResult.None);
        if (!Enabled) return Task.FromResult(SpeechResult.None with { Skipped = true });
        if (options.Silent) return RenderSilentAsync(text, options, ct);
        var request = Enqueue(new SpeechRequest(text, options.Alias, voice: null, wait ? ct : CancellationToken.None, options));
        return wait ? request.Completion : Task.FromResult(SpeechResult.None);
    }

    /// <summary>Queues <paramref name="text"/> with an explicit alias (e.g. one being edited) and waits for it.</summary>
    public Task TestVoiceAsync(VoiceAlias alias, string text, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Enqueue(new SpeechRequest(text, aliasName: null, Json.Clone(alias), ct,
            new SpeakOptions { Alias = alias.Name, Priority = int.MaxValue, Source = "test" })).Completion;
    }

    /// <summary>Stops the text that is playing; the queue continues with the next one.</summary>
    public void SkipCurrent()
    {
        lock (_gate) _currentPlayback?.Cancel();
    }

    /// <summary>Drops every waiting text (the one playing is not affected). Waiting callers see cancellation.</summary>
    public void ClearQueue()
    {
        foreach (var request in _queue.RemoveAll(_ => true)) request.Cancel();
        RaiseStateChanged();
    }

    /// <summary>Drops one waiting text by its <see cref="QueuedSpeech.Id"/>. Returns false when it already started or is gone.</summary>
    public bool RemoveQueued(long id)
    {
        var removed = _queue.RemoveAll(r => r.Id == id);
        foreach (var request in removed) request.Cancel();
        if (removed.Count > 0) RaiseStateChanged();
        return removed.Count > 0;
    }

    /// <summary>Drops a user's waiting texts and stops theirs if it is playing. Returns how many were affected.</summary>
    public int StopUser(string? platform, string? userId, string? userName)
    {
        if (string.IsNullOrWhiteSpace(userId) && string.IsNullOrWhiteSpace(userName)) return 0;
        var removed = _queue.RemoveAll(r => r.IsFrom(platform, userId, userName));
        foreach (var request in removed) request.Cancel();
        var count = removed.Count;
        lock (_gate)
        {
            if (_current is { } current && current.IsFrom(platform, userId, userName))
            {
                _currentPlayback?.Cancel();
                count++;
            }
        }
        if (count > 0) RaiseStateChanged();
        return count;
    }

    /// <summary>Drops (or stops) the text of one chat message. Returns true when it was found.</summary>
    public bool RemoveMessage(string? messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId)) return false;
        bool Matches(SpeechRequest r) => string.Equals(r.Options.MessageId, messageId.Trim(), StringComparison.Ordinal);
        var removed = _queue.RemoveAll(Matches);
        foreach (var request in removed) request.Cancel();
        var found = removed.Count > 0;
        lock (_gate)
        {
            if (_current is { } current && Matches(current))
            {
                _currentPlayback?.Cancel();
                found = true;
            }
        }
        if (found) RaiseStateChanged();
        return found;
    }

    /// <summary>Assigns an alias (empty to reset) to a user, creating the user entry when needed.</summary>
    public SpeechUser SetUserVoice(string platform, string userId, string userName, string alias) =>
        Users.Update(platform, userId, userName, u => u.Alias = alias.Trim());

    /// <summary>Sticky random voices: a user keeps their first random voice until it is reset.</summary>
    public bool StickyRandom
    {
        get => Settings.SpeakChat.StickyRandom;
        set
        {
            if (Settings.SpeakChat.StickyRandom == value) return;
            Settings.SpeakChat.StickyRandom = value;
            SaveSettings();
        }
    }

    /// <summary>
    /// Speaker.bot "assign last": makes the voice a user was last heard with their permanent voice. A random pick
    /// or a multi-voice alias is pinned in a new alias named after the user. Returns the alias, or null when the
    /// user has not spoken yet.
    /// </summary>
    public VoiceAlias? AssignLastVoice(string platform, string userName)
    {
        var user = Users.Find(platform, null, userName.Trim().TrimStart('@'));
        if (user is null || user.LastVoice.Split('|') is not [var aliasName, var engineId, var voiceId]) return null;
        var alias = VoiceResolver.FindAlias(aliasName, Settings.Aliases);
        if (alias is null || alias.EffectiveVoices().Count > 1)
        {
            var name = UniqueAliasName($"{(user.UserName.Length > 0 ? user.UserName : userName.Trim().TrimStart('@'))} voice");
            alias = new VoiceAlias { Name = name, EngineId = engineId, VoiceId = voiceId };
            Settings.Aliases.Add(alias);
            SaveSettings();
        }
        SetUserVoice(platform, user.UserId, user.UserName, alias.Name);
        return alias;
    }

    /// <summary>Whether one event kind (or Speaker.bot event name, see <see cref="EventSpeech.ResolveKind"/>) is spoken; null when unknown.</summary>
    public bool? IsEventEnabled(string name) =>
        EventSpeech.ResolveKind(name) is { } kind ? EventSpeech.FindTemplate(Settings.Events, kind)?.Enabled : null;

    /// <summary>Turns speech for one event kind on or off (Speaker.bot UDP "events"). False when the name is unknown.</summary>
    public bool SetEventEnabled(string name, bool enabled)
    {
        if (EventSpeech.ResolveKind(name) is not { } kind) return false;
        var events = Settings.Events;
        var template = events.Templates.FirstOrDefault(t => string.Equals(t.Kind, kind, StringComparison.OrdinalIgnoreCase));
        if (template is null)
        {
            // Store the built-in default so the switch persists without changing its messages.
            template = EventSpeech.DefaultTemplate(kind);
            if (template is null) return false;
            events.Templates.Add(template);
        }
        template.Enabled = enabled;
        SaveSettings();
        RaiseStateChanged();
        return true;
    }

    private string UniqueAliasName(string baseName)
    {
        var name = baseName;
        for (var i = 2; VoiceResolver.FindAlias(name, Settings.Aliases) is not null; i++) name = $"{baseName} {i}";
        return name;
    }

    /// <summary>Activates a VoiceGate profile by name; empty or "none" switches VoiceGate off. False when unknown.</summary>
    public bool ActivateVoiceGateProfile(string? name)
    {
        var gate = Settings.VoiceGate;
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            SetVoiceGate(gate, "", null);
            return true;
        }
        var profile = gate.Profiles.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        if (profile is null) return false;
        SetVoiceGate(gate, profile.Name, profile);
        return true;
    }

    /// <summary>Activates a voice ignore profile (random voice pool) by name; empty or "none" deselects. False when unknown.</summary>
    public bool ActivateVoiceIgnoreProfile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            Settings.ActiveVoiceIgnoreProfile = "";
        }
        else
        {
            var profile = Settings.VoiceIgnoreProfiles.FirstOrDefault(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (profile is null) return false;
            Settings.ActiveVoiceIgnoreProfile = profile.Name;
        }
        SaveSettings();
        return true;
    }

    /// <summary>Forgets session state such as already-announced follows.</summary>
    public void ResetSession() => _eventSpeech.Reset();

    public async ValueTask DisposeAsync()
    {
        Task? worker;
        lock (_gate)
        {
            _subscription?.Dispose();
            worker = _worker;
        }
        _queue.Complete();
        await _shutdown.CancelAsync().ConfigureAwait(false);
        if (worker is not null) await worker.ConfigureAwait(false);
        ClearQueue();
        _voiceGate.Dispose();
        _shutdown.Dispose();
    }

    /// <summary>Volume of a clip: alias volume scaled by master volume, both 0-100.</summary>
    internal static int CombineVolume(int aliasVolume, int masterVolume) =>
        Math.Clamp(aliasVolume, 0, 100) * Math.Clamp(masterVolume, 0, 100) / 100;

    private void SetVoiceGate(VoiceGateSettings gate, string name, VoiceGateProfile? profile)
    {
        if (gate.ActiveProfile != name)
        {
            gate.ActiveProfile = name;
            SaveSettings();
        }
        _voiceGate.Apply(profile);
        RaiseStateChanged(publish: true);
    }

    private SpeechRequest Enqueue(SpeechRequest request)
    {
        var queue = Settings.Queue;
        if (!_queue.TryEnqueue(request, queue.MaxLength, queue.DropPolicy, out var dropped))
        {
            if (dropped is null) throw new ObjectDisposedException(nameof(SpeechService));
        }
        if (dropped is not null)
        {
            _log.Info(LogSource, $"Speech queue is full; dropped \"{Preview(dropped.Text)}\"");
            dropped.Cancel();
        }
        RaiseStateChanged();
        return request;
    }

    private void OnEvent(BotEvent e)
    {
        switch (e.Type)
        {
            case SpeechEvents.ChatMessage:
                if (ChatContext.FromEvent(e) is { } chat) _ = HandleChatSafelyAsync(chat);
                return;
            case SpeechEvents.ChatActivity:
                HandleActivity(e.Args);
                return;
            case SpeechEvents.Whisper:
                if (Settings.Commands is { Enabled: true, AllowModeratorWhispers: true } && ChatContext.FromEvent(e) is { } whisper)
                    _ = HandleWhisperSafelyAsync(whisper with { Platform = "Twitch" });
                return;
        }
        if (SpeechEvents.Merch.Contains(e.Type, StringComparer.OrdinalIgnoreCase))
        {
            HandleActivity(EventSpeech.MerchArgs(e.Args));
            return;
        }
        var moderation = Settings.Moderation;
        if (SpeechEvents.MessageDeleted.Contains(e.Type, StringComparer.OrdinalIgnoreCase))
        {
            if (moderation.SkipOnMessageDeleted) RemoveMessage(Arg(e, "messageId", "targetMessageId", "msgId"));
        }
        else if (SpeechEvents.UserBanned.Contains(e.Type, StringComparer.OrdinalIgnoreCase))
        {
            if (moderation.SkipOnBan) StopModeratedUser(e);
        }
        else if (SpeechEvents.UserTimedOut.Contains(e.Type, StringComparer.OrdinalIgnoreCase))
        {
            if (moderation.SkipOnTimeout) StopModeratedUser(e);
        }
    }

    private void StopModeratedUser(BotEvent e)
    {
        var platform = Arg(e, "platform");
        if (platform.Length == 0 && e.Type.StartsWith("twitch.", StringComparison.OrdinalIgnoreCase)) platform = "Twitch";
        StopUser(platform, Arg(e, "userId", "targetUserId"), Arg(e, "userName", "user", "targetUserName", "targetUser"));
    }

    private static string Arg(BotEvent e, params string[] keys) =>
        keys.Select(k => VariableResolver.Format(e.Args.GetValueOrDefault(k))).FirstOrDefault(v => v.Length > 0) ?? "";

    private async Task HandleChatSafelyAsync(ChatContext chat)
    {
        try
        {
            await HandleChatAsync(chat).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_shutdown.IsCancellationRequested)
        {
            _log.Error(LogSource, "Could not handle a chat message for speech", ex);
        }
    }

    private async Task HandleWhisperSafelyAsync(ChatContext whisper)
    {
        try
        {
            await HandleWhisperAsync(whisper).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !_shutdown.IsCancellationRequested)
        {
            _log.Error(LogSource, "Could not handle a whispered TTS command", ex);
        }
    }

    /// <summary>
    /// Speaker.bot's "Allow Moderator Whispers": a moderator whispers a <c>!tts</c> (or speaking) command to the
    /// broadcaster account and it runs as if typed in chat by a moderator. Other whispers, and whispers from anyone
    /// who is not a moderator of the channel, are ignored. Whisper events carry no badges, so the platform is asked.
    /// </summary>
    internal async Task HandleWhisperAsync(ChatContext whisper)
    {
        if (!TtsCommands.IsCommand(whisper.Message, Settings.Commands)) return;
        var roles = _roles.FirstOrDefault(r => string.Equals(r.Platform, whisper.Platform, StringComparison.OrdinalIgnoreCase));
        if (roles is null || !await roles.IsModeratorAsync(whisper.UserId, _shutdown.Token).ConfigureAwait(false))
        {
            _log.Debug(LogSource, $"Ignored a whispered TTS command from {whisper.User}: not a moderator");
            return;
        }
        await HandleChatAsync(whisper with { IsModerator = true }).ConfigureAwait(false);
    }

    internal async Task HandleChatAsync(ChatContext chat)
    {
        var user = TouchUser(chat);
        var commands = Settings.Commands;
        if (commands.Enabled && await _commands.TryHandleAsync(chat, user, _shutdown.Token).ConfigureAwait(false)) return;
        if (!Enabled) return;

        var chatSettings = Settings.SpeakChat;
        if (chatSettings.Platforms.Count > 0
            && !chatSettings.Platforms.Any(p => string.Equals(p, chat.Platform, StringComparison.OrdinalIgnoreCase))) return;

        // Speaking commands ("!say text") work in both modes, when chat reading is on.
        var speakCommand = commands.Enabled ? TtsCommands.MatchSpeakCommand(chat.Message, commands) : null;
        if (speakCommand is not null)
        {
            if (!chatSettings.Enabled || user?.Ignored == true) return;
            if (!TtsCommands.Allows(speakCommand.Permission, chat, user))
            {
                await _commands.ReplyNotAllowedAsync(chat, _shutdown.Token).ConfigureAwait(false);
                return;
            }
            await SpeakChatAsync(chat with { Message = speakCommand.Text }, user, speakCommand.Alias, checkPrefixes: false)
                .ConfigureAwait(false);
            return;
        }

        if (!chatSettings.Enabled || chatSettings.Mode == SpeakMode.Command) return;
        if (user is { Ignored: true } or { OptedOut: true }) return;
        var forced = chatSettings.AllowForced && user?.Forced == true;
        if (!forced && !TtsCommands.Allows(chatSettings.Permission, chat, user)) return;
        await SpeakChatAsync(chat, user, alias: null, checkPrefixes: true).ConfigureAwait(false);
    }

    private async Task SpeakChatAsync(ChatContext chat, SpeechUser? user, string? alias, bool checkPrefixes)
    {
        var settings = Settings.SpeakChat;
        var profile = MessageFilter.Resolve(Settings, settings.FilterProfile);
        var key = UserKey(chat.Platform, chat.UserId, chat.User);
        var now = _time.GetUtcNow();
        bool repeat;
        lock (_lastSpoke)
        {
            repeat = settings.NameGraceSeconds > 0 && _lastSpoke.TryGetValue(key, out var last)
                     && now - last < TimeSpan.FromSeconds(settings.NameGraceSeconds);
        }
        var text = ChatSpeech.BuildUtterance(chat, settings, profile, user?.Nickname, repeat, EmoteNames(chat.Platform), checkPrefixes);
        if (text is null) return;
        lock (_lastSpoke) _lastSpoke[key] = now;

        var aliasName = alias;
        if (string.IsNullOrWhiteSpace(aliasName) && user is { Alias.Length: > 0 } && VoiceResolver.FindAlias(user.Alias, Settings.Aliases) is not null)
            aliasName = user.Alias;
        if (string.IsNullOrWhiteSpace(aliasName) && chat.IsHighlighted && settings.HighlightAlias.Length > 0) aliasName = settings.HighlightAlias;

        VoiceAlias? randomVoice = null;
        if (string.IsNullOrWhiteSpace(aliasName) && settings.RandomVoices)
            randomVoice = await PickRandomVoiceAsync(chat, user).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(aliasName)) aliasName = settings.Alias;

        var options = new SpeakOptions
        {
            Alias = randomVoice?.Name ?? aliasName,
            Platform = chat.Platform,
            UserId = chat.UserId,
            UserName = chat.User,
            MessageId = chat.MessageId,
            Rate = user?.Rate,
            Pitch = user?.Pitch,
            Source = "chat",
        };
        Enqueue(new SpeechRequest(text, options.Alias, randomVoice, CancellationToken.None, options));
    }

    private async Task<VoiceAlias?> PickRandomVoiceAsync(ChatContext chat, SpeechUser? user)
    {
        var ignore = Settings.VoiceIgnoreProfiles.FirstOrDefault(p =>
            string.Equals(p.Name, Settings.ActiveVoiceIgnoreProfile, StringComparison.OrdinalIgnoreCase));
        var sticky = Settings.SpeakChat.StickyRandom ? user?.RandomVoice : null;
        var voice = await _voices.ResolveRandomAsync(ignore, sticky, _shutdown.Token).ConfigureAwait(false);
        if (voice is null) return null;
        if (Settings.SpeakChat.StickyRandom && user is not null && user.RandomVoice != voice.Key)
            Users.Update(chat.Platform, chat.UserId, chat.User, u => u.RandomVoice = voice.Key);
        return new VoiceAlias { Name = "Random", EngineId = voice.Engine.Id, VoiceId = voice.VoiceId };
    }

    private IReadOnlySet<string>? EmoteNames(string platform)
    {
        if (_emoteProviders.Count == 0) return null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var provider in _emoteProviders)
        {
            try
            {
                names.UnionWith(provider.GetEmoteNames(platform));
            }
            catch (Exception ex)
            {
                _log.Warn(LogSource, $"{provider.Source} emotes are unavailable: {ex.Message}");
            }
        }
        return names;
    }

    private SpeechUser? TouchUser(ChatContext chat)
    {
        if (chat.User.Length == 0 && chat.UserId.Length == 0) return null;
        var now = _time.GetUtcNow();
        var existing = Users.Find(chat.Platform, chat.UserId, chat.User);
        if (existing is not null && now - existing.LastSeen < TimeSpan.FromMinutes(5)) return existing;
        return Users.Update(chat.Platform, chat.UserId, chat.User, u => u.LastSeen = now);
    }

    private static string UserKey(string platform, string userId, string user) =>
        $"{platform}|{(userId.Length > 0 ? userId : user)}";

    private void HandleActivity(IReadOnlyDictionary<string, object?> args)
    {
        if (!Enabled || !Settings.Events.Enabled) return;
        var events = Settings.Events;
        var utterance = _eventSpeech.Build(args, events, MessageFilter.Resolve(Settings, events.FilterProfile), Settings.SpeakChat);
        if (utterance is null) return;
        var user = VariableResolver.Format(args.GetValueOrDefault("user"));
        var options = new SpeakOptions
        {
            Alias = utterance.Alias.Length > 0 ? utterance.Alias : null,
            Platform = VariableResolver.Format(args.GetValueOrDefault("platform")),
            UserName = user,
            Source = "event",
        };
        Enqueue(new SpeechRequest(utterance.Text, options.Alias, voice: null, CancellationToken.None, options));
    }

    private async Task RunQueueAsync(CancellationToken shutdown)
    {
        try
        {
            while (true)
            {
                await _queue.WaitAsync(shutdown).ConfigureAwait(false);
                while (_pause.IsPaused || _voiceGate.IsHolding)
                {
                    await _pause.WaitAsync(shutdown).ConfigureAwait(false);
                    await _voiceGate.WaitOpenAsync(shutdown).ConfigureAwait(false);
                }
                shutdown.ThrowIfCancellationRequested();
                if (_queue.TryTake() is { IsFinished: false } request) await PlayAsync(request, shutdown).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private sealed record Rendered(SynthesizedAudio Audio, ResolvedVoice Voice, SpeechResult Result);

    private async Task<SpeechResult> RenderSilentAsync(string text, SpeakOptions options, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdown.Token);
        timeout.CancelAfter(SilentTimeout);
        var rendered = await RenderAsync(text, options, explicitVoice: null, saveAlways: true, timeout.Token).ConfigureAwait(false);
        return rendered?.Result ?? SpeechResult.None with { Skipped = true };
    }

    private async Task<Rendered?> RenderAsync(string text, SpeakOptions options, VoiceAlias? explicitVoice, bool saveAlways,
        CancellationToken ct)
    {
        if (options.ApplyFilters)
        {
            var filtered = MessageFilter.Apply(text, MessageFilter.Resolve(Settings, options.FilterProfile), Settings.SpeakChat);
            if (filtered.Text is null) return null;
            text = filtered.Text;
        }
        text = PrepareText(text);
        if (text.Length == 0) return null;

        var voice = explicitVoice is not null
            ? _voices.Resolve(explicitVoice)
            : await _voices.ResolveAsync(options.Alias, Settings.Aliases.ToArray(), ct, Settings.DefaultAlias).ConfigureAwait(false);
        voice = voice with { Rate = Math.Clamp(options.Rate ?? voice.Rate, -10, 10) };
        voice = voice with { Pitch = Math.Clamp(options.Pitch ?? voice.Pitch, -10, 10) };

        var audio = await voice.Engine.SynthesizeAsync(
            new SynthesisRequest(text, voice.VoiceId) { Rate = voice.Rate, Pitch = voice.Pitch, Style = voice.Style }, ct).ConfigureAwait(false);
        var file = await SaveAsync(audio, voice, saveAlways || options.SaveToFile, ct).ConfigureAwait(false);
        RememberVoice(options, voice);
        var result = new SpeechResult(text, voice.AliasName, voice.Engine.Id, voice.VoiceId)
        {
            Duration = AudioDuration.Estimate(audio),
            FilePath = file,
        };
        return new Rendered(audio, voice, result);
    }

    private void RememberVoice(SpeakOptions options, ResolvedVoice voice)
    {
        if (options.UserId.Length == 0 && options.UserName.Length == 0) return;
        if (Users.Find(options.Platform, options.UserId, options.UserName) is null) return;
        var last = $"{voice.AliasName}|{voice.Engine.Id}|{voice.VoiceId}";
        Users.Update(options.Platform, options.UserId, options.UserName, u => u.LastVoice = last);
    }

    private async Task<string?> SaveAsync(SynthesizedAudio audio, ResolvedVoice voice, bool force, CancellationToken ct)
    {
        var folder = Settings.Queue.SaveFolder.Trim();
        if (folder.Length == 0)
        {
            if (!force) return null;
            folder = Path.Combine(Path.GetTempPath(), "cuebot-speech");
        }
        try
        {
            Directory.CreateDirectory(folder);
            var name = SanitizeFileName(voice.AliasName.Length > 0 ? voice.AliasName : voice.VoiceId);
            var path = Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss-fff}-{name}{audio.FileExtension}");
            await File.WriteAllBytesAsync(path, audio.Data, ct).ConfigureAwait(false);
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.Warn(LogSource, $"Could not save speech to {folder}: {ex.Message}");
            return null;
        }
    }

    internal static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars().Concat(['/', '\\', ':', '*', '?', '"', '<', '>', '|']).ToHashSet();
        var clean = new string(name.Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (clean.Length > 60) clean = clean[..60];
        return clean.Length == 0 ? "speech" : clean;
    }

    private async Task PlayAsync(SpeechRequest request, CancellationToken shutdown)
    {
        using var playback = CancellationTokenSource.CreateLinkedTokenSource(shutdown, request.Cancellation);
        SetCurrent(request, playback);
        Rendered? rendered = null;
        var started = false;
        var skipped = false;
        Exception? failure = null;
        var cancelled = false;
        var watch = new Stopwatch();
        try
        {
            rendered = await RenderAsync(request.Text, request.Options, request.Voice, saveAlways: false, playback.Token).ConfigureAwait(false);
            if (rendered is null) return;
            started = true;
            Publish(SpeechEvents.Started, request, rendered.Result, skipped: false);
            var voice = rendered.Voice;
            var device = voice.DeviceId.Length > 0 ? voice.DeviceId : Settings.OutputDeviceId.Length > 0 ? Settings.OutputDeviceId : null;
            watch.Start();
            await _player.PlayAsync(rendered.Audio.Data, new PlaybackOptions(device, CombineVolume(voice.Volume, Settings.MasterVolume)),
                playback.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (playback.IsCancellationRequested
            && !shutdown.IsCancellationRequested && !request.Cancellation.IsCancellationRequested)
        {
            skipped = true; // skipped by the user or moderation: counts as done
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
            var result = rendered?.Result ?? SpeechResult.None;
            if (result.Duration == TimeSpan.Zero && watch.IsRunning) result = result with { Duration = watch.Elapsed };
            result = result with { Played = started && !skipped && !cancelled && failure is null, Skipped = skipped };
            if (started)
            {
                Publish(SpeechEvents.Finished, request, result, skipped);
                AddHistory(request, result);
            }
            if (failure is not null) request.Fail(failure);
            else if (cancelled) request.Cancel();
            else request.Complete(result);
        }
    }

    private void AddHistory(SpeechRequest request, SpeechResult result)
    {
        lock (_history)
        {
            _history.AddFirst(new SpeechHistoryEntry(DateTimeOffset.Now, result.Text, result.Alias, result.EngineId, result.VoiceId,
                request.Options.UserName, request.Options.Source, result.Skipped));
            while (_history.Count > HistorySize) _history.RemoveLast();
        }
    }

    private string PrepareText(string text) =>
        TextFilter.CollapseWhitespace(TextFilter.ApplyReplacements(text, Settings.Replacements.ToArray()));

    private void SetCurrent(SpeechRequest? request, CancellationTokenSource? playback)
    {
        lock (_gate)
        {
            _current = request;
            _currentPlayback = playback;
        }
        RaiseStateChanged();
    }

    private void Publish(string type, SpeechRequest request, SpeechResult result, bool skipped)
    {
        var args = new Dictionary<string, object?>
        {
            ["text"] = request.Text,
            ["alias"] = result.Alias,
            ["engine"] = result.EngineId,
            ["voice"] = result.VoiceId,
            ["source"] = request.Options.Source,
            ["user"] = request.Options.UserName,
            ["userId"] = request.Options.UserId,
            ["platform"] = request.Options.Platform,
            ["messageId"] = request.Options.MessageId,
        };
        if (type == SpeechEvents.Finished)
        {
            args["duration"] = (long)result.Duration.TotalMilliseconds;
            args["skipped"] = skipped;
        }
        _bus.Publish(new BotEvent(type, args));
    }

    private void RaiseStateChanged(bool publish = false)
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _log.Error(LogSource, "A speech state listener failed", ex);
        }
        if (!publish) return;
        _bus.Publish(new BotEvent(SpeechEvents.StateChanged, new Dictionary<string, object?>
        {
            ["enabled"] = Enabled,
            ["paused"] = Paused,
            ["eventsEnabled"] = EventsEnabled,
            ["voiceGate"] = VoiceGateHolding,
        }));
    }

    private static string Preview(string text) => text.Length <= 60 ? text : text[..60] + "...";
}
