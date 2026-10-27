namespace Cuebot.Speech;

/// <summary>Event types published on the EventBus by the speech module.</summary>
public static class SpeechEvents
{
    /// <summary>A text started playing. Args: text, alias.</summary>
    public const string Started = "speech.started";

    /// <summary>A text finished playing or was skipped. Args: text, alias.</summary>
    public const string Finished = "speech.finished";

    /// <summary>Chat message event consumed by "speak chat".</summary>
    internal const string TwitchChatMessage = "twitch.chatMessage";
}
