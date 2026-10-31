using Cuebot.Assistant.Cli;
using Cuebot.Assistant.Providers;
using Cuebot.Core;
using Cuebot.Core.Logging;
using Cuebot.Core.Storage;
using Cuebot.Server;
using Microsoft.Extensions.DependencyInjection;

namespace Cuebot.Assistant;

public static class AssistantServiceCollectionExtensions
{
    /// <summary>
    /// Adds assistant.json, the AI providers and <see cref="AssistantService"/> (the Assistant page). Needs
    /// <c>AddServers()</c>: the assistant's tools are Cuebot's MCP tools.
    /// </summary>
    public static IServiceCollection AddAssistant(this IServiceCollection services)
    {
        services.AddSettingsFile<AssistantSettings>("assistant.json");
        // Answers stream for as long as the model writes; a stopped turn cancels the request instead of a timeout.
        services.AddKeyedSingleton(nameof(AssistantService), (_, _) => new HttpClient { Timeout = Timeout.InfiniteTimeSpan });

        // AI CLIs on this computer, with their own sign-ins (they reach Cuebot through the MCP server).
        services.AddSingleton<IAssistantProvider>(sp => new CodexProvider(Settings(sp, "codex")));
        services.AddSingleton<IAssistantProvider>(sp => new ClaudeCodeProvider(Settings(sp, "claude")));
        services.AddSingleton<IAssistantProvider>(sp => new AcpProvider(AcpProvider.Grok, Settings(sp, "grok")));
        services.AddSingleton<IAssistantProvider>(sp => new AntigravityProvider(Settings(sp, "antigravity"), AntigravityIsolation.CurrentGateCommand));
        // API keys.
        services.AddSingleton<IAssistantProvider>(sp => new AnthropicProvider(Settings(sp, "anthropic"), sp.GetRequiredKeyedService<HttpClient>(nameof(AssistantService))));
        foreach (var profile in new[] { OpenAiCompatibleProvider.OpenAi, OpenAiCompatibleProvider.Xai, OpenAiCompatibleProvider.Google })
            services.AddSingleton<IAssistantProvider>(sp => new OpenAiCompatibleProvider(profile,
                sp.GetRequiredKeyedService<HttpClient>(nameof(AssistantService)), Settings(sp, profile.Id)));
        // A model on this computer (Ollama, LM Studio, llama.cpp): last, so Automatic prefers the stronger AIs.
        services.AddSingleton<IAssistantProvider>(sp => new LocalModelProvider(sp.GetRequiredKeyedService<HttpClient>(nameof(AssistantService)),
            Settings(sp, LocalModelProvider.ProviderId)));

        services.AddSettingsFile<AssistantHistoryFile>("assistant-history.json");
        services.AddSingleton(sp => new AssistantService(sp.GetRequiredService<JsonFile<AssistantSettings>>(), sp.GetServices<IAssistantProvider>(),
            sp.GetRequiredService<ServerManager>(), sp.GetRequiredService<AppPaths>(), sp.GetRequiredService<AppLog>(),
            sp.GetRequiredService<JsonFile<AssistantHistoryFile>>()));
        return services;
    }

    private static Func<AssistantProviderSettings> Settings(IServiceProvider sp, string providerId)
    {
        var file = sp.GetRequiredService<JsonFile<AssistantSettings>>();
        return () => file.Value.For(providerId);
    }
}
