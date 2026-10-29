namespace Cuebot.Assistant;

public enum AssistantProviderKind
{
    /// <summary>An AI CLI installed on this computer (Codex, Claude Code, Grok, Antigravity), with its own sign-in.</summary>
    Cli,

    /// <summary>A provider's API, with an API key the streamer enters.</summary>
    ApiKey,
}

/// <summary>Whether a provider can be used now, and why not.</summary>
public sealed record AssistantProviderStatus(bool Ready, string Message)
{
    public static AssistantProviderStatus Available(string message) => new(true, message);

    public static AssistantProviderStatus Unavailable(string message) => new(false, message);
}

/// <summary>One AI the Assistant page can talk to.</summary>
public interface IAssistantProvider
{
    /// <summary>Stable id used in assistant.json ("codex", "claude", "openai", ...).</summary>
    string Id { get; }

    string Name { get; }

    AssistantProviderKind Kind { get; }

    /// <summary>True when <see cref="SignInAsync"/> can start the provider's own sign-in (Codex: Sign in with ChatGPT).</summary>
    bool CanSignIn => false;

    Task<AssistantProviderStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>Starts signing in; returns a URL the streamer opens in a browser, or null when nothing needs opening.</summary>
    Task<string?> SignInAsync(CancellationToken ct = default) => Task.FromResult<string?>(null);

    /// <summary>Models to offer in the picker (may be empty: the field is free text).</summary>
    Task<IReadOnlyList<string>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<string>>([]);

    IAssistantSession StartSession(AssistantContext context);
}

/// <summary>One conversation. Each <see cref="SendAsync"/> is one user turn; the session keeps the history.</summary>
public interface IAssistantSession : IAsyncDisposable
{
    IAsyncEnumerable<AssistantUpdate> SendAsync(string text, CancellationToken ct = default);
}

/// <summary>What a session needs from Cuebot.</summary>
/// <param name="Instructions">The system prompt (Cuebot's role and rules for the AI).</param>
/// <param name="Tools">Cuebot's MCP tools, in process (API-key providers).</param>
/// <param name="Mcp">Cuebot's MCP endpoint for AI CLIs; null when the MCP server is off.</param>
/// <param name="Model">The model to use; empty for the provider's default.</param>
/// <param name="WorkingDirectory">A folder in Cuebot's data folder the AI CLIs run in (they get no other files).</param>
public sealed record AssistantContext(
    string Instructions,
    AssistantTools Tools,
    McpEndpoint? Mcp,
    string Model,
    string WorkingDirectory);

/// <summary>Cuebot's MCP server as an AI CLI reaches it.</summary>
public sealed record McpEndpoint(string Url, string Token)
{
    public const string ServerName = "cuebot";

    /// <summary>The environment variable the AI CLI processes get the token in (never on a command line).</summary>
    public const string TokenVariable = "CUEBOT_MCP_TOKEN";

    /// <summary>How long an AI app waits for one of Cuebot's tools: a destructive one waits for the streamer's Approve.</summary>
    public static readonly TimeSpan ToolTimeout = TimeSpan.FromMinutes(15);
}

/// <summary>What happens during a turn, in order.</summary>
public abstract record AssistantUpdate;

/// <summary>More of the AI's answer.</summary>
public sealed record AssistantText(string Text) : AssistantUpdate;

/// <summary>The AI called one of Cuebot's tools.</summary>
public sealed record AssistantToolStarted(string CallId, string Tool) : AssistantUpdate;

/// <summary>A tool call ended.</summary>
public sealed record AssistantToolFinished(string CallId, string Tool, bool Failed) : AssistantUpdate;

/// <summary>
/// A destructive tool (delete, import, chat send) waits for the streamer: answer with <see cref="Approve"/> or
/// <see cref="Deny"/>. API-key providers only; an AI CLI asks in its reply instead.
/// </summary>
public sealed record AssistantConfirmation(string Tool, string Summary) : AssistantUpdate
{
    private readonly TaskCompletionSource<bool> _answer = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task<bool> Answer => _answer.Task;

    public void Approve() => _answer.TrySetResult(true);

    public void Deny() => _answer.TrySetResult(false);
}

/// <summary>Something went wrong; the turn ends.</summary>
public sealed record AssistantError(string Message) : AssistantUpdate;
