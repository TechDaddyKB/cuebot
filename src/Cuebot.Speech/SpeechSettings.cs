namespace Cuebot.Speech;

/// <summary>Persisted speech configuration (speech.json).</summary>
public sealed class SpeechSettings
{
    /// <summary>ElevenLabs API key; the ElevenLabs engine is available only when this is set. Never log it.</summary>
    public string ElevenLabsApiKey { get; set; } = "";

    /// <summary>Named voices used by actions, chat reading and scripts.</summary>
    public List<VoiceAlias> Aliases { get; set; } = [];

    public SpeakChatSettings SpeakChat { get; set; } = new();

    /// <summary>Replacements applied to every spoken text.</summary>
    public List<WordReplacement> Replacements { get; set; } = [];

    /// <summary>Volume applied on top of every alias volume, 0-100.</summary>
    public int MasterVolume { get; set; } = 100;
}

/// <summary>A named voice: which engine, which voice of it, and how fast and loud it speaks.</summary>
public sealed class VoiceAlias
{
    public string Name { get; set; } = "";
    public string EngineId { get; set; } = "";
    public string VoiceId { get; set; } = "";

    /// <summary>Speaking rate from -10 (slowest) to 10 (fastest); 0 is normal.</summary>
    public int Rate { get; set; }

    /// <summary>Volume 0-100.</summary>
    public int Volume { get; set; } = 100;
}

/// <summary>Settings for reading Twitch chat messages aloud.</summary>
public sealed class SpeakChatSettings
{
    public bool Enabled { get; set; }

    /// <summary>Alias to speak with; empty uses the default voice.</summary>
    public string Alias { get; set; } = "";

    /// <summary>What to say; chat event arguments (%user%, %message%, ...) are substituted.</summary>
    public string Template { get; set; } = "%user% says %message%";

    /// <summary>Skip messages starting with "!".</summary>
    public bool IgnoreCommands { get; set; } = true;

    /// <summary>Replace links with the word "link".</summary>
    public bool SkipLinks { get; set; } = true;

    /// <summary>Messages longer than this are truncated; 0 or less means unlimited.</summary>
    public int MaxLength { get; set; } = 300;

    /// <summary>Users never read aloud (matched case-insensitively by display name or login).</summary>
    public List<string> IgnoredUsers { get; set; } = [];
}

/// <summary>Replaces text before it is spoken, either literally (case-insensitive) or by regular expression.</summary>
public sealed class WordReplacement
{
    public string Find { get; set; } = "";
    public string Replace { get; set; } = "";
    public bool IsRegex { get; set; }
}
