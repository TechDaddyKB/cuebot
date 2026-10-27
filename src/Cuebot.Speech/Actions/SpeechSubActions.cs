using Cuebot.Core.Actions;

namespace Cuebot.Speech;

internal static class SpeechCategories
{
    public const string Speech = "Speech";
    public const string Audio = "Audio";
}

/// <summary>Enabled/Disabled/Toggle style modes shared by several sub-actions.</summary>
internal static class SwitchMode
{
    public const string On = "Enable";
    public const string Off = "Disable";
    public const string Toggle = "Toggle";
    public static readonly IReadOnlyList<string> Choices = [On, Off, Toggle];

    /// <summary>The state after applying <paramref name="mode"/>; unknown modes keep it unchanged.</summary>
    public static bool Apply(string mode, bool current) => mode.Trim().ToLowerInvariant() switch
    {
        "enable" or "enabled" or "on" or "true" => true,
        "disable" or "disabled" or "off" or "false" => false,
        "toggle" => !current,
        _ => current,
    };
}

/// <summary>
/// Speaks a text with a voice alias. Silent renders and saves the audio without playing it; wait (Speaker.bot
/// "Delay") returns once spoken. Both write speechFile and duration (ms) into the action's arguments.
/// </summary>
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
        new("silent", "Silent (render to a file only)", ParameterKind.Boolean) { DefaultValue = "false" },
        new("badWordFilter", "Apply filters (bad words, links, length)", ParameterKind.Boolean) { DefaultValue = "false" },
        new("filterProfile", "Filter profile")
        {
            Suggestions = () => speech.Settings.FilterProfiles.Select(p => p.Name),
            Help = "Leave empty for the default filters",
        },
        new("priority", "Priority", ParameterKind.Number) { DefaultValue = "0", Help = "Higher is spoken first" },
    ];

    public async Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        var options = new SpeakOptions
        {
            Alias = parameters["alias"].Length == 0 ? null : parameters["alias"],
            Silent = parameters.GetBool("silent"),
            ApplyFilters = parameters.GetBool("badWordFilter"),
            FilterProfile = parameters["filterProfile"],
            Priority = parameters.GetInt("priority"),
            Source = "action",
        };
        var wait = parameters.GetBool("wait");
        var result = await speech.SpeakAsync(parameters["text"], options, wait, ct).ConfigureAwait(false);
        context.Args["success"] = !result.Skipped;
        if (wait || options.Silent)
        {
            context.Args["speechFile"] = result.FilePath ?? "";
            context.Args["duration"] = (long)result.Duration.TotalMilliseconds;
        }
        return SubActionResult.Next;
    }
}

/// <summary>Skips the text currently being spoken.</summary>
internal sealed class SkipSpeechSubAction(SpeechService speech) : ISubActionHandler
{
    public string TypeId => "speech.skip";
    public string Category => SpeechCategories.Speech;
    public string DisplayName => "Stop current speech";
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

/// <summary>Turns speech on or off (off also stops and clears), like Speaker.bot's "Set Enabled State".</summary>
internal sealed class SetSpeechEnabledSubAction(SpeechService speech) : ISubActionHandler
{
    public string TypeId => "speech.setEnabled";
    public string Category => SpeechCategories.Speech;
    public string DisplayName => "Set speech enabled";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [new("mode", "Mode", ParameterKind.Choice) { DefaultValue = SwitchMode.On, Choices = SwitchMode.Choices }];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        speech.Enabled = SwitchMode.Apply(parameters["mode"], speech.Enabled);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Turns event speech (follows, subs...) on or off.</summary>
internal sealed class SetEventSpeechSubAction(SpeechService speech) : ISubActionHandler
{
    public string TypeId => "speech.setEvents";
    public string Category => SpeechCategories.Speech;
    public string DisplayName => "Set event speech";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
        [new("mode", "Mode", ParameterKind.Choice) { DefaultValue = SwitchMode.On, Choices = SwitchMode.Choices }];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        speech.EventsEnabled = SwitchMode.Apply(parameters["mode"], speech.EventsEnabled);
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Changes which alias chat reading, event speech or the default voice uses.</summary>
internal sealed class SetSpeechAliasSubAction(SpeechService speech) : ISubActionHandler
{
    internal const string Chat = "Chat";
    internal const string Events = "Events";
    internal const string Default = "Default";
    internal const string Highlight = "Highlight";

    public string TypeId => "speech.setAlias";
    public string Category => SpeechCategories.Speech;
    public string DisplayName => "Set voice alias";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("target", "Used for", ParameterKind.Choice) { DefaultValue = Chat, Choices = [Chat, Events, Default, Highlight] },
        new("alias", "Voice alias") { Suggestions = () => speech.Settings.Aliases.Select(a => a.Name), Help = "Empty resets it" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        Apply(speech.Settings, parameters["target"], parameters["alias"].Trim());
        speech.SaveSettings();
        return Task.FromResult(SubActionResult.Next);
    }

    internal static void Apply(SpeechSettings settings, string target, string alias)
    {
        switch (target.Trim().ToLowerInvariant())
        {
            case "events": settings.Events.Alias = alias; break;
            case "default": settings.DefaultAlias = alias; break;
            case "highlight": settings.SpeakChat.HighlightAlias = alias; break;
            default: settings.SpeakChat.Alias = alias; break;
        }
    }
}

/// <summary>Assigns a voice alias to a chatter (empty resets it).</summary>
internal sealed class SetUserVoiceSubAction(SpeechService speech) : ISubActionHandler
{
    public string TypeId => "speech.setUserVoice";
    public string Category => SpeechCategories.Speech;
    public string DisplayName => "Set user voice";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("platform", "Platform") { DefaultValue = "%platform%" },
        new("user", "User name") { DefaultValue = "%user%" },
        new("userId", "User id") { DefaultValue = "%userId%", Help = "Optional; matches by name when empty" },
        new("alias", "Voice alias") { Suggestions = () => speech.Settings.Aliases.Select(a => a.Name), Help = "Empty resets it" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        speech.SetUserVoice(Clean(parameters["platform"]), Clean(parameters["userId"]), Clean(parameters["user"]), parameters["alias"]);
        return Task.FromResult(SubActionResult.Next);
    }

    /// <summary>An unresolved %variable% counts as empty.</summary>
    internal static string Clean(string value) =>
        value.Length > 1 && value.StartsWith('%') && value.EndsWith('%') ? "" : value.Trim();
}

/// <summary>Drops a chatter's queued texts and stops theirs if it is playing.</summary>
internal sealed class StopUserSpeechSubAction(SpeechService speech) : ISubActionHandler
{
    public string TypeId => "speech.stopUser";
    public string Category => SpeechCategories.Speech;
    public string DisplayName => "Stop user's speech";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("platform", "Platform") { DefaultValue = "%platform%" },
        new("user", "User name") { DefaultValue = "%user%" },
        new("userId", "User id") { DefaultValue = "%userId%" },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        var count = speech.StopUser(SetUserVoiceSubAction.Clean(parameters["platform"]),
            SetUserVoiceSubAction.Clean(parameters["userId"]), SetUserVoiceSubAction.Clean(parameters["user"]));
        context.Args["stoppedCount"] = count;
        return Task.FromResult(SubActionResult.Next);
    }
}

/// <summary>Activates a VoiceGate profile ("none" or empty switches VoiceGate off). Writes success.</summary>
internal sealed class ActivateVoiceGateSubAction(SpeechService speech) : ISubActionHandler
{
    public string TypeId => "speech.activateVoiceGate";
    public string Category => SpeechCategories.Speech;
    public string DisplayName => "Activate VoiceGate profile";

    public IReadOnlyList<ParameterDescriptor> Parameters { get; } =
    [
        new("profile", "Profile")
        {
            Suggestions = () => speech.Settings.VoiceGate.Profiles.Select(p => p.Name).Append("none"),
            Help = "\"none\" switches VoiceGate off",
        },
    ];

    public Task<SubActionResult> ExecuteAsync(ActionRunContext context, ParameterValues parameters, CancellationToken ct)
    {
        context.Args["success"] = speech.ActivateVoiceGateProfile(parameters["profile"]);
        return Task.FromResult(SubActionResult.Next);
    }
}

