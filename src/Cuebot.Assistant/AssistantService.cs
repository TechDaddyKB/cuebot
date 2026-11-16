using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Cuebot.Core;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Server;

namespace Cuebot.Assistant;

/// <summary>
/// The Assistant page's backend: the providers, which one is used, and the current conversation. One conversation at a
/// time; changing the provider or model, or <see cref="NewChatAsync"/>, starts a new one.
/// </summary>
public sealed class AssistantService : IAsyncDisposable
{
    private const string LogSource = "Assistant";

    /// <summary>Automatic choice order: AI CLIs the streamer already signed in to first, then API keys.</summary>
    public static readonly string[] ProviderOrder = ["codex", "claude", "grok", "antigravity", "anthropic", "openai", "xai", "google"];

    private readonly JsonFile<AssistantSettings>? _file;
    private readonly JsonFile<AssistantHistoryFile>? _history;
    private readonly AssistantHistoryFile _memoryHistory = new();
    private readonly AssistantSettings _memorySettings = new();
    private readonly ServerManager _servers;
    private readonly AppLog _log;
    private readonly string _workingDirectory;
    private readonly SemaphoreSlim _turn = new(1, 1);
    private IAssistantSession? _session;
    private IDisposable? _mcpRegistration;
    private volatile ChannelWriter<AssistantUpdate>? _turnUpdates;
    private (string Provider, string Model)? _sessionKey;

    public AssistantService(JsonFile<AssistantSettings>? file, IEnumerable<IAssistantProvider> providers, ServerManager servers, AppPaths? paths,
        AppLog log, JsonFile<AssistantHistoryFile>? history = null)
    {
        _file = file;
        _history = history;
        _servers = servers;
        _log = log;
        Providers = [.. providers.OrderBy(p => Array.IndexOf(ProviderOrder, p.Id) is var i and >= 0 ? i : int.MaxValue).ThenBy(p => p.Name)];
        Tools = new AssistantTools(servers.Mcp);
        _workingDirectory = Path.Combine(paths?.Root ?? Path.GetTempPath(), "assistant");
        // "Allow everything in this chat" ends with the conversation, and an app restart ends that.
        if (Settings.Approvals == AssistantApprovals.ThisChat) Settings.Approvals = AssistantApprovals.Ask;
    }

    public AssistantSettings Settings => _file?.Value ?? _memorySettings;

    /// <summary>Cuebot's servers (the MCP server conversations register with).</summary>
    internal ServerManager Servers => _servers;

    /// <summary>The folder AI CLIs run in; other conversations get folders inside it.</summary>
    internal string WorkingDirectory => _workingDirectory;

    internal AppLog Log => _log;

    public IReadOnlyList<IAssistantProvider> Providers { get; }

    public AssistantTools Tools { get; }

    /// <summary>The provider of the open conversation (null before the first message).</summary>
    public string? ActiveProviderId => _sessionKey?.Provider;

    public void SaveSettings() => _file?.ScheduleSave();

    // ---- Approvals: which tool calls run without an Approve card ----------------------------------------------------

    private readonly Lock _approvals = new();

    /// <summary>How tools that need approval are handled (see <see cref="AssistantSettings.Approvals"/>).</summary>
    public AssistantApprovals Approvals
    {
        get => Settings.Approvals;
        set
        {
            if (Settings.Approvals == value) return;
            Settings.Approvals = value;
            SaveSettings();
            _log.Info(LogSource, $"Approvals: {value}");
        }
    }

    /// <summary>
    /// True for tools that publish something outside Cuebot (a public GitHub issue): they ask every time, whatever
    /// <see cref="Approvals"/> or <see cref="AlwaysAllowedTools"/> say.
    /// </summary>
    public bool AlwaysAsks(string tool) => _servers.Mcp.Tools.Any(t => t.AlwaysConfirm && t.Name.Equals(tool, StringComparison.Ordinal));

    /// <summary>Tools remembered with "Always allow", in the order they were added.</summary>
    public IReadOnlyList<string> AlwaysAllowedTools
    {
        get
        {
            lock (_approvals) return [.. Settings.AlwaysAllowedTools];
        }
    }

    /// <summary>Whether a call of <paramref name="tool"/> runs without asking right now.</summary>
    public bool AllowsWithoutAsking(string tool)
    {
        if (AlwaysAsks(tool)) return false;
        if (Settings.Approvals != AssistantApprovals.Ask) return true;
        lock (_approvals) return Settings.AlwaysAllowedTools.Contains(tool, StringComparer.Ordinal);
    }

    /// <summary>Remembers <paramref name="tool"/> so it no longer asks. False for tools that always ask.</summary>
    public bool AlwaysAllow(string tool)
    {
        if (AlwaysAsks(tool) || tool.Length == 0) return false;
        lock (_approvals)
        {
            if (Settings.AlwaysAllowedTools.Contains(tool, StringComparer.Ordinal)) return true;
            Settings.AlwaysAllowedTools.Add(tool);
        }
        SaveSettings();
        _log.Info(LogSource, $"Tool {tool} is now allowed without asking");
        return true;
    }

    /// <summary>Asks again before <paramref name="tool"/>.</summary>
    public void Forget(string tool)
    {
        lock (_approvals)
        {
            if (Settings.AlwaysAllowedTools.RemoveAll(t => t.Equals(tool, StringComparison.Ordinal)) == 0) return;
        }
        SaveSettings();
        _log.Info(LogSource, $"Tool {tool} asks again");
    }

    /// <summary>Asks again before every remembered tool.</summary>
    public void ForgetAll()
    {
        lock (_approvals)
        {
            if (Settings.AlwaysAllowedTools.Count == 0) return;
            Settings.AlwaysAllowedTools.Clear();
        }
        SaveSettings();
        _log.Info(LogSource, "All tools ask again");
    }

    /// <summary>A call that runs without a card: logged (the tool name only) and shown as a line in the turn.</summary>
    private AssistantAllowed Allowed(string tool, string summary)
    {
        _log.Info(LogSource, $"Tool {tool} allowed without asking ({(Settings.Approvals == AssistantApprovals.Ask ? "always allowed" : Settings.Approvals.ToString())})");
        return new AssistantAllowed(tool, summary);
    }

    /// <summary>The conversation as last saved (see <see cref="AssistantHistoryFile"/>).</summary>
    public IReadOnlyList<AssistantHistoryEntry> History => (_history?.Value ?? _memoryHistory).Entries;

    /// <summary>Saves the conversation as the page shows it (the newest <see cref="AssistantHistoryFile.MaxEntries"/> lines).</summary>
    public void SaveHistory(IEnumerable<AssistantHistoryEntry> entries)
    {
        var list = entries.ToList();
        (_history?.Value ?? _memoryHistory).Entries = list.Count > AssistantHistoryFile.MaxEntries ? list[^AssistantHistoryFile.MaxEntries..] : list;
        _history?.ScheduleSave();
    }

    public IAssistantProvider? Find(string id) => Providers.FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Cuebot's MCP endpoint for AI CLIs, or null while the MCP server is off or not listening.</summary>
    public McpEndpoint? Mcp =>
        _servers.Settings.Mcp.Enabled && _servers.Api.IsRunning
            ? new McpEndpoint(_servers.ApiBaseUrl + Cuebot.Server.Api.ApiServer.McpPath, _servers.EnsureApiToken())
            : null;

    /// <summary>The chosen provider, or (automatic) the first one that is ready; null when none is.</summary>
    public async Task<IAssistantProvider?> ResolveProviderAsync(CancellationToken ct = default)
    {
        if (Settings.Provider.Length > 0) return Find(Settings.Provider);
        foreach (var provider in Providers)
        {
            if ((await SafeStatusAsync(provider, ct).ConfigureAwait(false)).Ready) return provider;
        }
        return null;
    }

    /// <summary>A provider's status; a failing check reads as unavailable instead of throwing.</summary>
    public async Task<AssistantProviderStatus> SafeStatusAsync(IAssistantProvider provider, CancellationToken ct = default)
    {
        try
        {
            return await provider.GetStatusAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return AssistantProviderStatus.Unavailable(ex.Message);
        }
    }

    /// <summary>One turn: sends <paramref name="text"/> and streams what happens. Errors end the turn as <see cref="AssistantError"/>.</summary>
    public async IAsyncEnumerable<AssistantUpdate> SendAsync(string text, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await _turn.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var provider = await ResolveProviderAsync(ct).ConfigureAwait(false);
            if (provider is null)
            {
                yield return Logged(null, new AssistantError("No AI is set up yet. Sign in to an AI app such as Codex or Claude Code, or add an API key, then try again."));
                yield break;
            }
            var status = await SafeStatusAsync(provider, ct).ConfigureAwait(false);
            if (!status.Ready)
            {
                yield return Logged(null, new AssistantError($"{provider.Name}: {status.Message}"));
                yield break;
            }

            var session = await SessionForAsync(provider).ConfigureAwait(false);
            var before = AssistantUndo.Capture(_servers.Services.Profile, _servers.Services.Commands);
            // One stream for the turn: the AI's updates, plus Approve/Deny cards Cuebot's MCP server asks for while an AI
            // app's tool call waits (see ConfirmAsync).
            var turn = Channel.CreateUnbounded<AssistantUpdate>(new UnboundedChannelOptions { SingleReader = true });
            _turnUpdates = turn.Writer;
            var pump = PumpAsync(provider, session.SendAsync(text, ct), turn.Writer, ct);
            await foreach (var update in turn.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                yield return update is AssistantError error ? Logged(provider, error) : update;
            _turnUpdates = null;
            await pump.ConfigureAwait(false);
            // Offer to undo what the turn changed (also after Stop: what was done is done).
            if (AssistantUndo.Since(before, _servers.Services.Profile, _servers.Services.Commands) is { } undo)
                yield return new AssistantChanges(undo);
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            _turnUpdates = null;
            _turn.Release();
        }
    }

    /// <summary>
    /// What the chat shows as an error also goes to the log, so "Report a problem" carries it: an AI app's own error
    /// (Codex, Claude Code...) otherwise only lived in the chat.
    /// </summary>
    private AssistantError Logged(IAssistantProvider? provider, AssistantError error)
    {
        var name = provider?.Name;
        _log.Warn(LogSource, name is null || error.Message.StartsWith(name, StringComparison.Ordinal) ? error.Message : $"{name}: {error.Message}");
        return error;
    }

    private async Task PumpAsync(IAssistantProvider provider, IAsyncEnumerable<AssistantUpdate> updates, ChannelWriter<AssistantUpdate> turn,
        CancellationToken ct)
    {
        try
        {
            await foreach (var update in updates.WithCancellation(ct).ConfigureAwait(false))
            {
                // API-key and local models ask through their own stream: an allowed tool goes ahead without a card.
                var shown = update;
                if (update is AssistantConfirmation card && AllowsWithoutAsking(card.Tool))
                {
                    shown = Allowed(card.Tool, card.Summary);
                    card.Approve();
                }
                await turn.WriteAsync(shown, CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // stopped: the turn throws once the stream is read
        }
        catch (Exception ex)
        {
            await turn.WriteAsync(new AssistantError(ex.Message), CancellationToken.None).ConfigureAwait(false);   // logged as it is shown
        }
        turn.TryComplete();
    }

    /// <summary>
    /// Cuebot's MCP server asks about a destructive tool an AI app called in this conversation: a card in the running
    /// turn, and the call waits for Approve or Deny. Outside a turn, or when the turn or the call ends, it is denied.
    /// A tool the streamer allowed (<see cref="AllowsWithoutAsking"/>) goes ahead with a line in the turn instead.
    /// </summary>
    private async Task<bool> ConfirmAsync(string tool, string summary, CancellationToken ct)
    {
        if (_turnUpdates is not { } turn) return false;
        if (AllowsWithoutAsking(tool)) return turn.TryWrite(Allowed(tool, summary));
        var confirmation = new AssistantConfirmation(tool, summary);
        if (!turn.TryWrite(confirmation)) return false;
        using var cancel = ct.Register(confirmation.Deny);
        return await confirmation.Answer.ConfigureAwait(false);
    }

    /// <summary>Forgets the conversation; the next message starts a new one. "Allow everything in this chat" ends too.</summary>
    public async Task NewChatAsync()
    {
        if (Settings.Approvals == AssistantApprovals.ThisChat) Approvals = AssistantApprovals.Ask;
        await EndConversationAsync().ConfigureAwait(false);
    }

    private async Task EndConversationAsync()
    {
        var session = _session;
        _session = null;
        _sessionKey = null;
        _mcpRegistration?.Dispose();
        _mcpRegistration = null;
        if (session is not null) await DisposeQuietlyAsync(session).ConfigureAwait(false);
    }

    private async Task<IAssistantSession> SessionForAsync(IAssistantProvider provider)
    {
        var model = Settings.For(provider.Id).Model.Trim();
        if (_session is not null && _sessionKey == (provider.Id, model)) return _session;
        await EndConversationAsync().ConfigureAwait(false);
        Directory.CreateDirectory(_workingDirectory);
        var mcp = provider.Kind == AssistantProviderKind.Cli ? Mcp : null;
        if (mcp is not null)
        {
            // The conversation's own MCP token: Cuebot asks the streamer (a card here) before destructive tools run.
            var (token, registration) = _servers.Mcp.RegisterAssistant(ConfirmAsync);
            _mcpRegistration = registration;
            mcp = mcp with { Token = token };
        }
        var instructions = provider.Kind == AssistantProviderKind.Local
            ? AssistantPrompt.BuildLocal()
            : AssistantPrompt.Build(toolsOverMcp: provider.Kind == AssistantProviderKind.Cli);
        var context = new AssistantContext(instructions, Tools, mcp, model, _workingDirectory);
        _session = provider.StartSession(context);
        _sessionKey = (provider.Id, model);
        _log.Info(LogSource, $"New conversation with {provider.Name}{(model.Length > 0 ? $" ({model})" : "")}");
        return _session;
    }

    private async Task DisposeQuietlyAsync(IAssistantSession session)
    {
        try
        {
            await session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Debug(LogSource, $"Closing the conversation: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await EndConversationAsync().ConfigureAwait(false);
        _turn.Dispose();
    }
}
