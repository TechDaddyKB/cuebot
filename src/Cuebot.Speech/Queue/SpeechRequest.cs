namespace Cuebot.Speech;

/// <summary>One queued utterance and the task its caller may await.</summary>
internal sealed class SpeechRequest
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenRegistration _registration;

    /// <param name="aliasName">Alias to resolve when it is played; ignored when <paramref name="voice"/> is given.</param>
    /// <param name="voice">Explicit voice (voice tests), bypassing alias lookup.</param>
    /// <param name="cancellation">Cancels the request while queued or playing.</param>
    public SpeechRequest(string text, string? aliasName, VoiceAlias? voice, CancellationToken cancellation)
    {
        Text = text;
        AliasName = aliasName;
        Voice = voice;
        Cancellation = cancellation;
        _registration = cancellation.Register(() => _completion.TrySetCanceled(cancellation));
    }

    public string Text { get; }
    public string? AliasName { get; }
    public VoiceAlias? Voice { get; }
    public CancellationToken Cancellation { get; }

    public Task Completion => _completion.Task;

    /// <summary>True once the request completed, failed or was cancelled.</summary>
    public bool IsFinished => _completion.Task.IsCompleted;

    public void Complete() => Finish(() => _completion.TrySetResult());

    public void Cancel() => Finish(() => _completion.TrySetCanceled());

    public void Fail(Exception error) => Finish(() => _completion.TrySetException(error));

    private void Finish(Action setResult)
    {
        _registration.Dispose();
        setResult();
    }
}
