namespace Cuebot.VoiceControl;

/// <summary>Where a command phrase may appear in what was said (Streamer.bot's "Location").</summary>
public enum CommandLocation
{
    /// <summary>The whole utterance is the phrase (a pause before and after).</summary>
    Exact,

    /// <summary>The utterance starts with the phrase; the rest becomes <c>spokenTextInput</c>.</summary>
    Start,

    /// <summary>The phrase appears anywhere in the utterance.</summary>
    Anywhere,
}

/// <summary>One voice command (Voice Control → Commands).</summary>
public sealed class VoiceCommand
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public bool Enabled { get; set; } = true;

    /// <summary>The phrase to listen for ("Command" in Streamer.bot).</summary>
    public string Phrase { get; set; } = "";

    public CommandLocation Location { get; set; } = CommandLocation.Exact;

    /// <summary>
    /// Fire as soon as the phrase is heard (from interim results while still speaking) and stop checking the other
    /// commands for this utterance, instead of waiting for the pause.
    /// </summary>
    public bool StopAfter { get; set; }

    /// <summary>Use <see cref="ConfidenceThreshold"/> instead of the global threshold.</summary>
    public bool OverrideGlobal { get; set; }

    /// <summary>0-100; only used with <see cref="OverrideGlobal"/>.</summary>
    public int ConfidenceThreshold { get; set; }
}

/// <summary>Persisted voice-control configuration (voicecontrol.json). Holds no secrets.</summary>
public sealed class VoiceControlSettings
{
    /// <summary>Start listening when Cuebot starts ("Auto Start Listen").</summary>
    public bool AutoStart { get; set; }

    /// <summary>Recognition engine id: "whisper" (Windows and Linux) or "windows" (System.Speech, Windows only).</summary>
    public string Engine { get; set; } = WhisperDefaults.EngineId;

    /// <summary>Locale such as "en-US"; empty = the system language; "auto" = detect (Whisper only).</summary>
    public string Locale { get; set; } = "";

    /// <summary>Global minimum confidence 0-100; quieter or less certain phrases are ignored.</summary>
    public int ConfidenceThreshold { get; set; } = 50;

    /// <summary>Write every recognised phrase to the application log ("Log Speech to Text Dictation").</summary>
    public bool LogDictation { get; set; }

    /// <summary>Microphone id or name; empty = the system default device.</summary>
    public string InputDevice { get; set; } = "";

    /// <summary>
    /// How different a recognised phrase may be from a command phrase and still match, 0-50 % of its characters
    /// (Whisper writes "count down" for "countdown", drops words...). 0 = exact words only.
    /// </summary>
    public int MatchTolerance { get; set; } = 20;

    public List<VoiceCommand> Commands { get; set; } = [];

    public WhisperSettings Whisper { get; set; } = new();

    public VoiceActivitySettings Vad { get; set; } = new();
}

/// <summary>Settings of the local Whisper engine.</summary>
public sealed class WhisperSettings
{
    /// <summary>Catalog id of the model to use (see <see cref="WhisperModelCatalog"/>).</summary>
    public string Model { get; set; } = WhisperModelCatalog.DefaultFor(System.Globalization.CultureInfo.CurrentUICulture).Id;

    /// <summary>Use a GPU runtime (CUDA/Vulkan) when one is installed next to Cuebot; otherwise the CPU is used.</summary>
    public bool UseGpu { get; set; }

    /// <summary>CPU threads for decoding; 0 = automatic (half the logical cores, 1-8).</summary>
    public int Threads { get; set; }

    /// <summary>Give Whisper the command phrases as context, which makes it spell them the way they are written.</summary>
    public bool PromptWithCommands { get; set; } = true;

    /// <summary>Transcribe while still speaking so "Stop After" commands can fire early (costs extra CPU).</summary>
    public bool InterimResults { get; set; } = true;
}

/// <summary>How speech is cut into utterances before recognition (energy-based voice activity detection).</summary>
public sealed class VoiceActivitySettings
{
    /// <summary>How far above the measured background noise speech must be, in dB.</summary>
    public double SpeechAboveNoiseDb { get; set; } = 12;

    /// <summary>Never treat anything quieter than this as speech, in dBFS.</summary>
    public double MinimumSpeechDb { get; set; } = -50;

    /// <summary>Pause length that ends an utterance.</summary>
    public int SilenceMs { get; set; } = 600;

    /// <summary>Shorter sounds (clicks, coughs) are ignored.</summary>
    public int MinSpeechMs { get; set; } = 250;

    /// <summary>Long monologues are cut at this length so recognition keeps up.</summary>
    public int MaxUtteranceSeconds { get; set; } = 15;
}

internal static class WhisperDefaults
{
    public const string EngineId = "whisper";
}
