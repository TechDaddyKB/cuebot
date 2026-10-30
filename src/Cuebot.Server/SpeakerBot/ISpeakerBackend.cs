using Cuebot.Speech;

namespace Cuebot.Server.SpeakerBot;

public sealed record SpeakerSpeakResult(bool Success, long DurationMs, string SpeechFile);

public sealed record SpeakerVoice(string Engine, string Id, string Name, string? Language);

/// <summary>What the Speaker.bot-compatible APIs drive. <see cref="SpeechServiceBackend"/> maps it onto Cuebot's speech.</summary>
public interface ISpeakerBackend
{
    bool Enabled { get; set; }
    bool Paused { get; set; }
    bool EventsEnabled { get; set; }
    int QueueLength { get; }
    string? CurrentText { get; }
    IReadOnlyList<string> Aliases { get; }

    /// <summary>Speaks (or with <paramref name="silent"/> only renders) a text; waits when <paramref name="wait"/>.</summary>
    Task<SpeakerSpeakResult> SpeakAsync(string text, string? alias, bool badWordFilter, bool wait, bool silent, CancellationToken ct);

    void Stop();
    void Clear();

    /// <summary>Stops the text playing and, while the queue is paused, lets one waiting text play; false when nothing happened.</summary>
    bool SpeakNext();

    /// <summary>"all" or "command" (read only !say-style commands). False for an unknown mode.</summary>
    bool SetMode(string mode);

    bool ActivateVoiceGateProfile(string profile);
    bool ActivateIgnoreProfile(string profile);
    void SetNickname(string platform, string userName, string nickname);
    void SetUserVoice(string platform, string userId, string userName, string alias);

    /// <summary>Marks a user as a "regular" (Speaker.bot reg add/del).</summary>
    void SetRegular(string platform, string userId, string userName, bool regular);

    /// <summary>Sticky random voices (Speaker.bot "set sticky").</summary>
    bool StickyRandom { get; set; }

    /// <summary>Speaker.bot "assign last": pins the voice a user was last heard with. The alias, or null when the user has not spoken.</summary>
    string? AssignLastVoice(string platform, string userName);

    /// <summary>Whether one event (kind or Speaker.bot event name) is spoken; null when the name is unknown.</summary>
    bool? IsEventEnabled(string name);

    /// <summary>Turns one event on or off; false when the name is unknown.</summary>
    bool SetEventEnabled(string name, bool enabled);
    Task<IReadOnlyList<SpeakerVoice>> GetVoicesAsync(CancellationToken ct);
}

/// <summary>Speaker.bot API → <see cref="SpeechService"/>.</summary>
public sealed class SpeechServiceBackend(SpeechService speech) : ISpeakerBackend
{
    public bool Enabled { get => speech.Enabled; set => speech.Enabled = value; }
    public bool Paused { get => speech.Paused; set => speech.Paused = value; }
    public bool EventsEnabled { get => speech.EventsEnabled; set => speech.EventsEnabled = value; }
    public int QueueLength => speech.QueueLength;
    public string? CurrentText => speech.CurrentText;
    public IReadOnlyList<string> Aliases => speech.Settings.Aliases.Select(a => a.Name).ToArray();

    public async Task<SpeakerSpeakResult> SpeakAsync(string text, string? alias, bool badWordFilter, bool wait, bool silent,
        CancellationToken ct)
    {
        var options = new SpeakOptions
        {
            Alias = string.IsNullOrWhiteSpace(alias) ? null : alias.Trim(),
            ApplyFilters = badWordFilter,
            Silent = silent,
            Source = "speakerbot-api",
        };
        var result = await speech.SpeakAsync(text, options, wait, ct).ConfigureAwait(false);
        return new SpeakerSpeakResult(!result.Skipped, (long)result.Duration.TotalMilliseconds, result.FilePath ?? "");
    }

    public void Stop() => speech.SkipCurrent();
    public bool SpeakNext() => speech.SpeakNext();
    public void Clear() => speech.ClearQueue();

    public bool SetMode(string mode)
    {
        if (!Enum.TryParse<SpeakMode>(mode, ignoreCase: true, out var parsed)) return false;
        speech.Settings.SpeakChat.Mode = parsed;
        speech.SaveSettings();
        return true;
    }

    public bool ActivateVoiceGateProfile(string profile) => speech.ActivateVoiceGateProfile(profile);
    public bool ActivateIgnoreProfile(string profile) => speech.ActivateVoiceIgnoreProfile(profile);

    public void SetNickname(string platform, string userName, string nickname)
    {
        var existing = speech.Users.Find(platform, null, userName);
        speech.Users.Update(platform, existing?.UserId ?? "", userName, u => u.Nickname = nickname.Trim());
    }

    public void SetUserVoice(string platform, string userId, string userName, string alias) =>
        speech.SetUserVoice(platform, userId, userName, alias);

    public void SetRegular(string platform, string userId, string userName, bool regular) =>
        speech.Users.Update(platform, userId, userName, u => u.Regular = regular);

    public bool StickyRandom { get => speech.StickyRandom; set => speech.StickyRandom = value; }
    public string? AssignLastVoice(string platform, string userName) => speech.AssignLastVoice(platform, userName)?.Name;
    public bool? IsEventEnabled(string name) => speech.IsEventEnabled(name);
    public bool SetEventEnabled(string name, bool enabled) => speech.SetEventEnabled(name, enabled);

    public async Task<IReadOnlyList<SpeakerVoice>> GetVoicesAsync(CancellationToken ct)
    {
        var voices = new List<SpeakerVoice>();
        foreach (var engine in speech.Engines.Where(e => e.IsAvailable))
        {
            try
            {
                foreach (var v in await engine.GetVoicesAsync(ct).ConfigureAwait(false))
                {
                    voices.Add(new SpeakerVoice(engine.Id, v.Id, v.Name, v.Language));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // An engine that cannot list voices (offline cloud engine) is left out.
            }
        }
        return voices;
    }
}
