namespace Cuebot.Core.Actions;

/// <summary>What the "Get input" sub-action asks the user.</summary>
public sealed record UserPromptRequest(string Title, string Prompt, string OriginalText);

/// <summary>
/// Asks the person at the computer for a line of text (the "Get input" dialog). The desktop app sets
/// <see cref="Handler"/> to show its dialog; without one (headless, tests) every prompt counts as cancelled.
/// </summary>
public sealed class UserPromptService
{
    /// <summary>Shows the prompt and returns the text, or null when cancelled or closed.</summary>
    public Func<UserPromptRequest, CancellationToken, Task<string?>>? Handler { get; set; }

    public bool IsAvailable => Handler is not null;

    public async Task<string?> AskAsync(UserPromptRequest request, CancellationToken ct)
    {
        var handler = Handler;
        if (handler is null) return null;
        return await handler(request, ct).ConfigureAwait(false);
    }
}
