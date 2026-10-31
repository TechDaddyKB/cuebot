using Cuebot.Core.Storage;
namespace Cuebot.Assistant;

/// <summary>
/// assistant.json: which AI the Assistant page uses and each provider's options. API keys are stored here like the other
/// modules' keys (data folder, never returned by the REST API or MCP, never logged).
/// </summary>
public sealed class AssistantSettings
{
    /// <summary>Provider id ("codex", "claude", "anthropic", ...); empty picks the first ready one in <see cref="AssistantService.Providers"/> order.</summary>
    public string Provider { get; set; } = "";

    public Dictionary<string, AssistantProviderSettings> Providers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public AssistantProviderSettings For(string providerId)
    {
        if (!Providers.TryGetValue(providerId, out var settings)) Providers[providerId] = settings = new AssistantProviderSettings();
        return settings;
    }
}

public sealed class AssistantProviderSettings
{
    /// <summary>Model id; empty uses the provider's default (an AI CLI's own default, or the newest suitable model for an API key).</summary>
    public string Model { get; set; } = "";

    /// <summary>API-key providers only. Never logged.</summary>
    [Secret]
    public string ApiKey { get; set; } = "";

    /// <summary>AI CLI providers only: the program to run when it is not on PATH (e.g. "/opt/bin/codex").</summary>
    public string Command { get; set; } = "";

    /// <summary>Local model only: the model server's address; empty is Ollama on this computer (http://127.0.0.1:11434).</summary>
    public string ServerUrl { get; set; } = "";

    /// <summary>Local model only: let the model think before it answers (slower; off is faster and about as accurate for small models).</summary>
    public bool Thinking { get; set; }

    /// <summary>Local model only: how many tokens the model may keep in mind (Ollama's num_ctx); Cuebot's tools need at least 16K.</summary>
    public int ContextLength { get; set; } = 32768;

    /// <summary>Local model only: send every Cuebot tool instead of the focused set (only for big models: it is about 14K tokens).</summary>
    public bool AllTools { get; set; }
}
