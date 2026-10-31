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
    }

    public AssistantSettings Settings => _file?.Value ?? _memorySettings;

    public IReadOnlyList<IAssistantProvider> Providers { get; }

    public AssistantTools Tools { get; }

    /// <summary>The provider of the open conversation (null before the first message).</summary>
    public string? ActiveProviderId => _sessionKey?.Provider;

    public void SaveSettings() => _file?.ScheduleSave();

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
                yield return new AssistantError("No AI is set up yet. Sign in to an AI app such as Codex or Claude Code, or add an API key, then try again.");
                yield break;
            }
            var status = await SafeStatusAsync(provider, ct).ConfigureAwait(false);
            if (!status.Ready)
            {
                yield return new AssistantError($"{provider.Name}: {status.Message}");
                yield break;
            }

            var session = await SessionForAsync(provider).ConfigureAwait(false);
            // One stream for the turn: the AI's updates, plus Approve/Deny cards Cuebot's MCP server asks for while an AI
            // app's tool call waits (see ConfirmAsync).
            var turn = Channel.CreateUnbounded<AssistantUpdate>(new UnboundedChannelOptions { SingleReader = true });
            _turnUpdates = turn.Writer;
            var pump = PumpAsync(provider, session.SendAsync(text, ct), turn.Writer, ct);
            await foreach (var update in turn.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false)) yield return update;
            _turnUpdates = null;
            await pump.ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            _turnUpdates = null;
            _turn.Release();
        }
    }

    private async Task PumpAsync(IAssistantProvider provider, IAsyncEnumerable<AssistantUpdate> updates, ChannelWriter<AssistantUpdate> turn,
        CancellationToken ct)
    {
        try
        {
            await foreach (var update in updates.WithCancellation(ct).ConfigureAwait(false)) await turn.WriteAsync(update, CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // stopped: the turn throws once the stream is read
        }
        catch (Exception ex)
        {
            _log.Warn(LogSource, $"{provider.Name}: {ex.Message}");
            await turn.WriteAsync(new AssistantError(ex.Message), CancellationToken.None).ConfigureAwait(false);
        }
        turn.TryComplete();
    }

    /// <summary>
    /// Cuebot's MCP server asks about a destructive tool an AI app called in this conversation: a card in the running
    /// turn, and the call waits for Approve or Deny. Outside a turn, or when the turn or the call ends, it is denied.
    /// </summary>
    private async Task<bool> ConfirmAsync(string tool, string summary, CancellationToken ct)
    {
        if (_turnUpdates is not { } turn) return false;
        var confirmation = new AssistantConfirmation(tool, summary);
        if (!turn.TryWrite(confirmation)) return false;
        using var cancel = ct.Register(confirmation.Deny);
        return await confirmation.Answer.ConfigureAwait(false);
    }

    /// <summary>Forgets the conversation; the next message starts a new one.</summary>
    public async Task NewChatAsync()
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
        await NewChatAsync().ConfigureAwait(false);
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
        await NewChatAsync().ConfigureAwait(false);
        _turn.Dispose();
    }
}
