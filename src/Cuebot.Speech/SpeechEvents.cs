namespace Cuebot.Speech;

/// <summary>Event types published and consumed on the EventBus by the speech module.</summary>
public static class SpeechEvents
{
    /// <summary>A text started playing. Args: text, alias, engine, voice, source, user, userId, platform, messageId.</summary>
    public const string Started = "speech.started";

    /// <summary>A text finished playing or was skipped. Same args as <see cref="Started"/> plus duration (ms), skipped.</summary>
    public const string Finished = "speech.finished";

    /// <summary>The speech state changed (enabled, paused, events, VoiceGate). Args: enabled, paused, eventsEnabled, voiceGate.</summary>
    public const string StateChanged = "speech.stateChanged";

    /// <summary>Platform-neutral chat messages (from ChatHub). See <see cref="ChatContext"/> for the args read.</summary>
    internal const string ChatMessage = "chat.message";

    /// <summary>Platform-neutral activity (follows, subs...). See <see cref="EventSpeech"/> for the args read.</summary>
    internal const string ChatActivity = "chat.activity";

    /// <summary>
    /// A message was deleted. Expected args: platform, messageId (or targetMessageId), optionally userId/user.
    /// Accepted as "chat.messageDeleted" or "twitch.messageDeleted" (the Twitch module emits it in wave 3).
    /// </summary>
    internal static readonly string[] MessageDeleted = ["chat.messageDeleted", "twitch.messageDeleted"];

    /// <summary>A user was banned. Expected args: platform, userId and/or user/userName (or targetUserId/targetUser).</summary>
    internal static readonly string[] UserBanned = ["chat.userBanned", "twitch.userBanned"];

    /// <summary>A user was timed out. Same args as <see cref="UserBanned"/>, plus duration (seconds, optional).</summary>
    internal static readonly string[] UserTimedOut = ["chat.userTimedOut", "twitch.userTimedOut"];
}
