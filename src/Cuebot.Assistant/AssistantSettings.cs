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
}
