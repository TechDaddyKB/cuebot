using Cuebot.Core.Actions;

namespace Cuebot.Speech;

internal static class SpeechCategories
{
    public const string Speech = "Speech";
    public const string Audio = "Audio";
}

/// <summary>Speaks a text with a voice alias.</summary>
internal sealed class SpeakSubAction(SpeechService speech) : ISubActionHandler
{
    public string TypeId => "speech.speak";
    public string Category => SpeechCategories.Speech;
    public string DisplayName => "Speak";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("alias", "Voice alias")
        {
            Suggestions = () => speech.Settings.Aliases.Select(a => a.Name),
            Help = "Leave empty for the default voice",
        },
        new("text", "Text", ParameterKind.Multiline),
        new("wait", "Wait until spoken", ParameterKind.Boolean) { DefaultValue = "false" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        await speech.SpeakAsync(parameters["text"], parameters["alias"], parameters.GetBool("wait"), ct).ConfigureAwait(false);
        return SubActionResult.Next;
    }
}

/// <summary>Skips the text currently being spoken.</summary>
internal sealed class SkipSpeechSubAction(SpeechService speech) : ISubActionHandler
{
    public string TypeId => "speech.skip";
    public string Category => SpeechCategories.Speech;
    public string DisplayName => "Skip current speech";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        speech.SkipCurrent();
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Drops every text waiting in the speech queue.</summary>
internal sealed class ClearSpeechQueueSubAction(SpeechService speech) : ISubActionHandler
{
    public string TypeId => "speech.clearQueue";
    public string Category => SpeechCategories.Speech;
    public string DisplayName => "Clear speech queue";
    public IReadOnlyList<ParameterDescriptor> Parameters { get; } = [];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        speech.ClearQueue();
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Pauses, resumes or toggles the speech queue.</summary>
internal sealed class PauseSpeechSubAction(SpeechService speech) : ISubActionHandler
{
    internal const string Pause = "Pause";
    internal const string Resume = "Resume";
    internal const string Toggle = "Toggle";

    public string TypeId => "speech.pause";
    public string Category => SpeechCategories.Speech;
    public string DisplayName => "Pause speech queue";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [new("mode", "Mode", ParameterKind.Choice) { DefaultValue = Pause, Choices = [Pause, Resume, Toggle] }];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        speech.Paused = ApplyMode(parameters["mode"], speech.Paused);
        return Task.FromResult(SubActionResult.Next);
    }

    /// <summary>The paused state after applying <paramref name="mode"/>; unknown modes keep it unchanged.</summary>
    internal static bool ApplyMode(string mode, bool paused) => mode.Trim().ToLowerInvariant() switch
    {
        "pause" => true,
        "resume" => false,
        "toggle" => !paused,
        _ => paused,
    };
}
