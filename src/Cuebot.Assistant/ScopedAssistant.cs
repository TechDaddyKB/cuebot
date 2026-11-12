using System.Collections.Concurrent;
using Cuebot.Server.Mcp;

namespace Cuebot.Assistant;

/// <summary>
/// Assistant conversations for people other than the streamer (mods in the mod portal). Each has its own key, its own
/// tools (<see cref="McpScope"/>: only what that person may do) and its own conversation with the streamer's AI, apart
/// from the Assistant page's. Turns run in the background and are read with <see cref="ScopedConversation.Since"/>, so a
/// caller that can only ask and answer (the portal's relay link) can follow along.
/// </summary>
public sealed class ScopedAssistant : IAsyncDisposable
{
    /// <summary>Turns running at once across all scoped conversations (each one runs on the streamer's AI account).</summary>
    public const int MaxConcurrentTurns = 2;

    /// <summary>A conversation nobody used for this long is closed (its AI app process ends).</summary>
    public static readonly TimeSpan IdleLimit = TimeSpan.FromMinutes(60);

    private readonly AssistantService _assistant;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<string, ScopedConversation> _conversations = new(StringComparer.Ordinal);

    public ScopedAssistant(AssistantService assistant, TimeProvider? time = null)
    {
        _assistant = assistant;
        _time = time ?? TimeProvider.System;
    }

    internal AssistantService Assistant => _assistant;

    internal TimeProvider Time => _time;

    internal SemaphoreSlim Slots { get; } = new(MaxConcurrentTurns, MaxConcurrentTurns);

    /// <summary>The conversation for <paramref name="key"/>, made with <paramref name="scope"/> and <paramref name="instructions"/> the first time.</summary>
    public ScopedConversation For(string key, Func<McpScope> scope, Func<string> instructions)
    {
        _ = CloseIdleAsync();
        return _conversations.GetOrAdd(key, k => new ScopedConversation(this, k, scope(), instructions()));
    }

    /// <summary>The conversation for <paramref name="key"/>, or null when there is none.</summary>
    public ScopedConversation? Find(string key) => _conversations.GetValueOrDefault(key);

    /// <summary>Ends and forgets a conversation (a removed member).</summary>
    public async Task ForgetAsync(string key)
    {
        if (_conversations.TryRemove(key, out var conversation)) await conversation.DisposeAsync().ConfigureAwait(false);
    }

    private async Task CloseIdleAsync()
    {
        var now = _time.GetUtcNow();
        foreach (var (key, conversation) in _conversations)
        {
            if (!conversation.Busy && now - conversation.LastUsed > IdleLimit && _conversations.TryRemove(key, out _))
                await conversation.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var key in _conversations.Keys.ToList()) await ForgetAsync(key).ConfigureAwait(false);
    }
}

/// <summary>One line of a scoped conversation as its reader sees it.</summary>
/// <param name="Id">Stable for the line; an answer that grows keeps its id.</param>
/// <param name="Seq">Raised every time the line is added or grows: read everything after the last seq seen.</param>
/// <param name="Kind">user, assistant, tool, confirm, notice or error.</param>
/// <param name="Text">The words, the tool's name, or what a confirmation is about.</param>
/// <param name="State">tool: running, done or failed. confirm: waiting, approved or denied.</param>
public sealed record ScopedLine(int Id, long Seq, string Kind, string Text, string State = "");

/// <summary>One person's conversation (see <see cref="ScopedAssistant"/>).</summary>
public sealed class ScopedConversation : IAsyncDisposable
{
    public const int MaxLines = 200;
    public const int MaxMessageLength = 4000;

    private readonly ScopedAssistant _owner;
    private readonly string _key;
    private readonly McpScope _scope;
    private readonly string _instructions;
    private readonly object _gate = new();
    private readonly List<ScopedLine> _lines = [];
    private readonly Dictionary<int, AssistantConfirmation> _waiting = [];
    private readonly Dictionary<string, int> _toolLines = new(StringComparer.Ordinal);
    private int _nextId;
    private long _seq;
    private CancellationTokenSource? _turn;
    private IAssistantSession? _session;
    private (string Provider, string Model)? _sessionKey;
    private IDisposable? _registration;

    internal ScopedConversation(ScopedAssistant owner, string key, McpScope scope, string instructions)
    {
        _owner = owner;
        _key = key;
        _scope = scope;
        _instructions = instructions;
        LastUsed = owner.Time.GetUtcNow();
    }

    public DateTimeOffset LastUsed { get; private set; }

    /// <summary>A turn is running.</summary>
    public bool Busy
    {
        get { lock (_gate) return _turn is not null; }
    }

    /// <summary>The highest <see cref="ScopedLine.Seq"/> so far.</summary>
    public long Seq
    {
        get { lock (_gate) return _seq; }
    }

    /// <summary>The lines added or changed after <paramref name="seq"/> (0: all kept lines).</summary>
    public IReadOnlyList<ScopedLine> Since(long seq)
    {
        lock (_gate)
        {
            LastUsed = _owner.Time.GetUtcNow();
            return [.. _lines.Where(l => l.Seq > seq)];
        }
    }

    /// <summary>
    /// Starts a turn in the background. Returns why it could not start (a turn already runs, the AI is busy with other
    /// conversations, the message is empty or too long), or null when it started.
    /// </summary>
    /// <param name="shown">What the conversation shows as the person's message, when it differs from what the AI is sent.</param>
    public string? Send(string text, string? shown = null)
    {
        text = text.Trim();
        if (text.Length == 0) return "Type a message first.";
        if (text.Length > MaxMessageLength) return $"That message is too long (up to {MaxMessageLength} characters).";
        CancellationTokenSource turn;
        lock (_gate)
        {
            if (_turn is not null) return "The assistant is still answering. Wait for it or press Stop.";
            if (!_owner.Slots.Wait(0)) return "The assistant is busy helping someone else. Try again in a minute.";
            turn = _turn = new CancellationTokenSource();
            LastUsed = _owner.Time.GetUtcNow();
            Add("user", shown?.Trim() is { Length: > 0 } s ? s : text);
        }
        _ = Task.Run(() => RunAsync(text, turn.Token), CancellationToken.None);
        return null;
    }

    /// <summary>Answers a confirmation line; false when it is not waiting.</summary>
    public bool Answer(int lineId, bool approve)
    {
        AssistantConfirmation? confirmation;
        lock (_gate)
        {
            if (!_waiting.Remove(lineId, out confirmation)) return false;
            Update(lineId, approve ? "approved" : "denied");
        }
        if (approve) confirmation.Approve();
        else confirmation.Deny();
        return true;
    }

    /// <summary>Stops the running turn (what was already done stays done).</summary>
    public void Stop()
    {
        lock (_gate) _turn?.Cancel();
    }

    /// <summary>Stops any turn and forgets the conversation; the next message starts a new one.</summary>
    public async Task ResetAsync()
    {
        Stop();
        // The stopped turn ends first, so the session is not closed under it.
        for (var i = 0; i < 100 && Busy; i++) await Task.Delay(100).ConfigureAwait(false);
        await CloseSessionAsync().ConfigureAwait(false);
        lock (_gate)
        {
            _lines.Clear();
            Add("notice", "New conversation.");
        }
    }

    private async Task RunAsync(string text, CancellationToken ct)
    {
        var log = _owner.Assistant.Log;
        try
        {
            var provider = await _owner.Assistant.ResolveProviderAsync(ct).ConfigureAwait(false);
            if (provider is null)
            {
                Line("error", "The streamer has not set up an AI for the assistant yet.");
                return;
            }
            var status = await _owner.Assistant.SafeStatusAsync(provider, ct).ConfigureAwait(false);
            if (!status.Ready)
            {
                Line("error", $"The streamer's AI ({provider.Name}) is not ready: {status.Message}");
                return;
            }
            var session = await SessionForAsync(provider).ConfigureAwait(false);
            await foreach (var update in session.SendAsync(text, ct).ConfigureAwait(false)) Handle(update, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Line("notice", "Stopped.");
        }
        catch (Exception ex)
        {
            log.Warn("Assistant", $"A mod portal conversation failed: {ex.Message}");
            Line("error", ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                foreach (var (id, confirmation) in _waiting)
                {
                    confirmation.Deny();
                    Update(id, "denied");
                }
                _waiting.Clear();
                foreach (var id in _toolLines.Values) if (Find(id) is { State: "running" }) Update(id, "failed");
                _toolLines.Clear();
                _turn?.Dispose();
                _turn = null;
                LastUsed = _owner.Time.GetUtcNow();
            }
            _owner.Slots.Release();
        }
    }

    private void Handle(AssistantUpdate update, CancellationToken ct)
    {
        switch (update)
        {
            case AssistantText t:
                lock (_gate) AppendText(t.Text);
                break;
            case AssistantToolStarted s:
                lock (_gate) _toolLines[s.CallId] = Add("tool", s.Tool, "running").Id;
                break;
            case AssistantToolFinished f:
                lock (_gate)
                {
                    if (_toolLines.Remove(f.CallId, out var id)) Update(id, f.Failed ? "failed" : "done");
                }
                break;
            case AssistantConfirmation c:
                Wait(c, ct);
                break;
            case AssistantError e:
                Line("error", e.Message);
                break;
        }
    }

    /// <summary>A confirmation from an API-key provider's stream, or from an AI app's tool call through the MCP server.</summary>
    private void Wait(AssistantConfirmation confirmation, CancellationToken ct)
    {
        lock (_gate)
        {
            if (ct.IsCancellationRequested)
            {
                confirmation.Deny();
                return;
            }
            var line = Add("confirm", confirmation.Summary, "waiting");
            _waiting[line.Id] = confirmation;
        }
    }

    private async Task<bool> ConfirmAsync(string tool, string summary, CancellationToken ct)
    {
        CancellationToken turn;
        lock (_gate)
        {
            if (_turn is null) return false;
            turn = _turn.Token;
        }
        var confirmation = new AssistantConfirmation(tool, summary);
        Wait(confirmation, turn);
        using var a = ct.Register(confirmation.Deny);
        using var b = turn.Register(confirmation.Deny);
        return await confirmation.Answer.ConfigureAwait(false);
    }

    private async Task<IAssistantSession> SessionForAsync(IAssistantProvider provider)
    {
        var model = _owner.Assistant.Settings.For(provider.Id).Model.Trim();
        if (_session is not null && _sessionKey == (provider.Id, model)) return _session;
        await CloseSessionAsync().ConfigureAwait(false);
        var folder = Path.Combine(_owner.Assistant.WorkingDirectory, "scoped", Safe(_key));
        Directory.CreateDirectory(folder);
        var mcp = provider.Kind == AssistantProviderKind.Cli ? _owner.Assistant.Mcp : null;
        if (mcp is not null)
        {
            var (token, registration) = _owner.Assistant.Servers.Mcp.RegisterAssistant(ConfirmAsync, _scope);
            _registration = registration;
            mcp = mcp with { Token = token };
        }
        var tools = new AssistantTools(_owner.Assistant.Servers.Mcp, _scope);
        _session = provider.StartSession(new AssistantContext(_instructions, tools, mcp, model, folder));
        _sessionKey = (provider.Id, model);
        _owner.Assistant.Log.Info("Assistant", $"New mod portal conversation with {provider.Name}{(model.Length > 0 ? $" ({model})" : "")}");
        return _session;
    }

    private async Task CloseSessionAsync()
    {
        var session = _session;
        _session = null;
        _sessionKey = null;
        _registration?.Dispose();
        _registration = null;
        if (session is null) return;
        try { await session.DisposeAsync().ConfigureAwait(false); }
        catch (Exception ex) { _owner.Assistant.Log.Debug("Assistant", $"Closing a mod portal conversation: {ex.Message}"); }
    }

    // ---- lines (callers hold _gate) ---------------------------------------------------------------------------------

    private void Line(string kind, string text)
    {
        lock (_gate) Add(kind, text);
    }

    private ScopedLine Add(string kind, string text, string state = "")
    {
        var line = new ScopedLine(++_nextId, ++_seq, kind, text, state);
        _lines.Add(line);
        if (_lines.Count > MaxLines) _lines.RemoveRange(0, _lines.Count - MaxLines);
        return line;
    }

    private void AppendText(string text)
    {
        if (_lines.Count > 0 && _lines[^1] is { Kind: "assistant" } last) _lines[^1] = last with { Text = last.Text + text, Seq = ++_seq };
        else Add("assistant", text);
    }

    private ScopedLine? Find(int id) => _lines.FirstOrDefault(l => l.Id == id);

    private void Update(int id, string state)
    {
        var i = _lines.FindIndex(l => l.Id == id);
        if (i >= 0) _lines[i] = _lines[i] with { State = state, Seq = ++_seq };
    }

    private static string Safe(string key) => string.Concat(key.Select(c => char.IsAsciiLetterOrDigit(c) || c == '-' ? c : '_'));

    public async ValueTask DisposeAsync()
    {
        Stop();
        await CloseSessionAsync().ConfigureAwait(false);
    }
}
