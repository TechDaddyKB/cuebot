namespace Cuebot.Speech;

/// <summary>How to speak one text. Everything is optional.</summary>
public sealed record SpeakOptions
{
    /// <summary>Alias name; null or unknown uses the default voice.</summary>
    public string? Alias { get; init; }

    /// <summary>Render the audio (and save it) without playing it; skips the queue.</summary>
    public bool Silent { get; init; }

    /// <summary>Higher numbers are spoken before lower ones; equal priorities keep their order.</summary>
    public int Priority { get; init; }

    /// <summary>Who caused the text, so moderation can drop it.</summary>
    public string Platform { get; init; } = "";
    public string UserId { get; init; } = "";
    public string UserName { get; init; } = "";
    public string MessageId { get; init; } = "";

    /// <summary>Rate/pitch overrides (-10..10) replacing the alias values.</summary>
    public int? Rate { get; init; }
    public int? Pitch { get; init; }

    /// <summary>Run the text through a filter profile first (bad words, links, length...).</summary>
    public bool ApplyFilters { get; init; }

    /// <summary>Filter profile for <see cref="ApplyFilters"/>; empty uses the default.</summary>
    public string FilterProfile { get; init; } = "";

    /// <summary>Save the audio even when no save folder is configured (to the temp folder).</summary>
    public bool SaveToFile { get; init; }

    /// <summary>"chat", "event", "action", "script", "api"...; published with the speech events.</summary>
    public string Source { get; init; } = "api";
}

/// <summary>What happened to one spoken text.</summary>
public sealed record SpeechResult(string Text, string Alias, string EngineId, string VoiceId)
{
    /// <summary>Length of the audio (estimated from its header for compressed formats).</summary>
    public TimeSpan Duration { get; init; }

    /// <summary>Where the audio was saved, or null.</summary>
    public string? FilePath { get; init; }

    /// <summary>True when the audio played to the end (false for silent texts and skips).</summary>
    public bool Played { get; init; }

    public bool Skipped { get; init; }

    public static readonly SpeechResult None = new("", "", "", "");
}

/// <summary>One queued utterance and the task its caller may await.</summary>
internal sealed class SpeechRequest
{
    private static long _sequence;
    private readonly TaskCompletionSource<SpeechResult> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _registration;

    /// <param name="aliasName">Alias to resolve when it is played; ignored when <paramref name="voice"/> is given.</param>
    /// <param name="voice">Explicit voice (voice tests), bypassing alias lookup.</param>
    /// <param name="cancellation">Cancels the request while queued or playing.</param>
    public SpeechRequest(string text, string? aliasName, VoiceAlias? voice, CancellationToken cancellation,
        SpeakOptions? options = null)
    {
        Text = text;
        AliasName = aliasName;
        Voice = voice;
        Cancellation = cancellation;
        Options = options ?? new SpeakOptions { Alias = aliasName };
        Id = Interlocked.Increment(ref _sequence);
        _registration = cancellation.Register(() => _completion.TrySetCanceled(cancellation));
    }

    public long Id { get; }
    public string Text { get; }
    public string? AliasName { get; }
    public VoiceAlias? Voice { get; }
    public CancellationToken Cancellation { get; }
    public SpeakOptions Options { get; }
    public DateTimeOffset Queued { get; } = DateTimeOffset.Now;

    public Task<SpeechResult> Completion => _completion.Task;

    /// <summary>True once the request completed, failed or was cancelled.</summary>
    public bool IsFinished => _completion.Task.IsCompleted;

    public bool IsFrom(string? platform, string? userId, string? userName)
    {
        if (!string.IsNullOrEmpty(platform) && Options.Platform.Length > 0
            && !string.Equals(platform, Options.Platform, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(userId) && Options.UserId.Length > 0)
            return string.Equals(userId, Options.UserId, StringComparison.OrdinalIgnoreCase);
        return !string.IsNullOrWhiteSpace(userName) && Options.UserName.Length > 0
               && string.Equals(userName.Trim().TrimStart('@'), Options.UserName, StringComparison.OrdinalIgnoreCase);
    }

    public void Complete(SpeechResult result) => Finish(() => _completion.TrySetResult(result));

    public void Cancel() => Finish(() => _completion.TrySetCanceled());

    public void Fail(Exception error) => Finish(() => _completion.TrySetException(error));

    private void Finish(Action setResult)
    {
        _registration.Dispose();
        setResult();
    }
}

/// <summary>A waiting text as the UI shows it.</summary>
public sealed record QueuedSpeech(long Id, string Text, string Alias, string User, int Priority, string Source);
