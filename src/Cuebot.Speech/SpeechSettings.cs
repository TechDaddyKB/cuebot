using System.Text.Json.Serialization;

namespace Cuebot.Speech;

/// <summary>Persisted speech configuration (speech.json). Every addition is optional so older files keep loading.</summary>
public sealed class SpeechSettings
{
    /// <summary>Master switch. Disabling stops the current text, clears the queue and ignores new texts.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>ElevenLabs API key; the ElevenLabs engine is available only when this is set. Never log it.</summary>
    public string ElevenLabsApiKey { get; set; } = "";

    /// <summary>Credentials and options of the cloud engines. Never log the secrets in here.</summary>
    public EngineSettings Engines { get; set; } = new();

    /// <summary>Named voices used by actions, chat reading and scripts.</summary>
    public List<VoiceAlias> Aliases { get; set; } = [];

    /// <summary>Alias used when none is given (empty: the first alias).</summary>
    public string DefaultAlias { get; set; } = "";

    public SpeakChatSettings SpeakChat { get; set; } = new();

    /// <summary>Replacements applied to every spoken text, last in the pipeline.</summary>
    public List<WordReplacement> Replacements { get; set; } = [];

    /// <summary>Volume applied on top of every alias volume, 0-100.</summary>
    public int MasterVolume { get; set; } = 100;

    /// <summary>Output device for aliases that do not pick one (empty: system default).</summary>
    public string OutputDeviceId { get; set; } = "";

    /// <summary>The default filter profile, used when no named profile is selected.</summary>
    public FilterProfile Filters { get; set; } = new() { Name = FilterProfile.DefaultName };

    /// <summary>Additional named filter profiles, selectable per source (chat, events, sub-actions).</summary>
    public List<FilterProfile> FilterProfiles { get; set; } = [];

    /// <summary>Voices and locales left out of random voice picks (Speaker.bot "ignore profiles").</summary>
    public List<VoiceIgnoreProfile> VoiceIgnoreProfiles { get; set; } = [];

    /// <summary>Name of the active <see cref="VoiceIgnoreProfiles"/> entry; empty for none.</summary>
    public string ActiveVoiceIgnoreProfile { get; set; } = "";

    public TtsCommandSettings Commands { get; set; } = new();

    public EventSpeechSettings Events { get; set; } = new();

    public ModerationSettings Moderation { get; set; } = new();

    public SpeechQueueSettings Queue { get; set; } = new();

    public VoiceGateSettings VoiceGate { get; set; } = new();
}

/// <summary>How an alias with several voices picks one per text.</summary>
public enum VoiceSelection { Random, RoundRobin }

/// <summary>
/// A named voice. Old files describe one voice with <see cref="EngineId"/>/<see cref="VoiceId"/>; newer ones may list
/// several in <see cref="Voices"/>, in which case the alias-level rate and pitch are added to each voice's own and the
/// alias volume scales each voice's volume.
/// </summary>
public sealed class VoiceAlias
{
    public string Name { get; set; } = "";
    public string EngineId { get; set; } = "";
    public string VoiceId { get; set; } = "";

    /// <summary>Speaking rate from -10 (slowest) to 10 (fastest); 0 is normal.</summary>
    public int Rate { get; set; }

    /// <summary>Pitch from -10 (lowest) to 10 (highest); 0 is normal.</summary>
    public int Pitch { get; set; }

    /// <summary>Volume 0-100.</summary>
    public int Volume { get; set; } = 100;

    /// <summary>Speaking style where the engine supports one (Azure "cheerful", ...). Empty for none.</summary>
    public string Style { get; set; } = "";

    /// <summary>Several voices to choose from; when empty, the single voice above is used.</summary>
    public List<AliasVoice> Voices { get; set; } = [];

    public VoiceSelection Selection { get; set; } = VoiceSelection.Random;

    /// <summary>Output device id from <see cref="IAudioPlayer.GetOutputDevices"/>; empty uses the global device.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>The voices this alias speaks with, folding the single-voice form into a list.</summary>
    public IReadOnlyList<AliasVoice> EffectiveVoices() =>
        Voices.Count > 0
            ? Voices
            : [new AliasVoice { EngineId = EngineId, VoiceId = VoiceId, Style = Style }];
}

/// <summary>One voice inside an alias.</summary>
public sealed class AliasVoice
{
    public string EngineId { get; set; } = "";
    public string VoiceId { get; set; } = "";

    /// <summary>-10..10, added to the alias rate.</summary>
    public int Rate { get; set; }

    /// <summary>-10..10, added to the alias pitch.</summary>
    public int Pitch { get; set; }

    /// <summary>0-100, scaled by the alias volume.</summary>
    public int Volume { get; set; } = 100;

    public string Style { get; set; } = "";

    /// <summary>Relative chance in random selection (1 when 0 or less).</summary>
    public int Weight { get; set; } = 1;
}

/// <summary>Credentials and options per cloud engine.</summary>
public sealed class EngineSettings
{
    public AzureSettings Azure { get; set; } = new();
    public PollySettings Polly { get; set; } = new();
    public GoogleSettings Google { get; set; } = new();
    public WatsonSettings Watson { get; set; } = new();
    public UberDuckSettings UberDuck { get; set; } = new();
    public TtsMonsterSettings TtsMonster { get; set; } = new();
    public AcapelaSettings Acapela { get; set; } = new();
    public CereProcSettings CereProc { get; set; } = new();
}

public sealed class AzureSettings
{
    /// <summary>Speech resource key ("KEY 1").</summary>
    public string Key { get; set; } = "";

    /// <summary>Resource region, e.g. "eastus".</summary>
    public string Region { get; set; } = "";
}

public sealed class PollySettings
{
    public string AccessKeyId { get; set; } = "";
    public string SecretAccessKey { get; set; } = "";
    public string Region { get; set; } = "us-east-1";

    /// <summary>Engine used for voice ids without one: "standard", "neural", "long-form" or "generative".</summary>
    public string Engine { get; set; } = "neural";
}

public sealed class GoogleSettings
{
    /// <summary>API key restricted to the Text-to-Speech API.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>Path to a service-account JSON key file, used when no API key is set.</summary>
    public string ServiceAccountFile { get; set; } = "";
}

public sealed class WatsonSettings
{
    public string ApiKey { get; set; } = "";

    /// <summary>Service instance URL, e.g. https://api.us-south.text-to-speech.watson.cloud.ibm.com/instances/...</summary>
    public string Url { get; set; } = "";

    /// <summary>Path to the ibm-credentials.env file IBM Cloud offers; read when the key or URL is empty.</summary>
    public string CredentialsFile { get; set; } = "";
}

public sealed class UberDuckSettings
{
    /// <summary>API key (public key).</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>API secret.</summary>
    public string ApiSecret { get; set; } = "";
}

public sealed class TtsMonsterSettings
{
    /// <summary>API key from the TTS Monster console, or the overlay URL that contains it.</summary>
    public string ApiKey { get; set; } = "";
}

public sealed class AcapelaSettings
{
    public string Email { get; set; } = "";
    public string Password { get; set; } = "";
}

public sealed class CereProcSettings
{
    /// <summary>CereVoice Cloud account email.</summary>
    public string Email { get; set; } = "";

    /// <summary>CereVoice Cloud account password.</summary>
    public string Password { get; set; } = "";
}

/// <summary>Who may do something in chat. The broadcaster always may.</summary>
[Flags]
public enum ChatAudience
{
    None = 0,
    Everyone = 1,
    Regulars = 2,
    Subscribers = 4,
    Vips = 8,
    Moderators = 16,
}

/// <summary>Whether chat reading speaks every message or only speaking commands.</summary>
public enum SpeakMode { All, Command }

/// <summary>Settings for reading chat messages aloud (any platform, from "chat.message").</summary>
public sealed class SpeakChatSettings
{
    public bool Enabled { get; set; }

    /// <summary>Alias to speak with; empty uses the default voice.</summary>
    public string Alias { get; set; } = "";

    /// <summary>What to say; chat event arguments (%user%, %message%, ...) are substituted.</summary>
    public string Template { get; set; } = "%user% says %message%";

    /// <summary>Used instead of <see cref="Template"/> when the same user spoke within <see cref="NameGraceSeconds"/>.</summary>
    public string RepeatTemplate { get; set; } = "%message%";

    /// <summary>Seconds after a user's last spoken message during which the shorter repeat template is used; 0 is off.</summary>
    public int NameGraceSeconds { get; set; }

    /// <summary>Speak %user% as the user's nickname when they have one.</summary>
    public bool UseNicknames { get; set; } = true;

    public SpeakMode Mode { get; set; } = SpeakMode.All;

    /// <summary>Who is read aloud. Users marked "forced" always are when <see cref="AllowForced"/> is on.</summary>
    public ChatAudience Permission { get; set; } = ChatAudience.Everyone;

    public bool AllowForced { get; set; } = true;

    /// <summary>Platforms read aloud; empty means all.</summary>
    public List<string> Platforms { get; set; } = [];

    /// <summary>Named filter profile for chat; empty uses the default.</summary>
    public string FilterProfile { get; set; } = "";

    /// <summary>Alias for highlighted messages (channel-point highlights); empty uses the normal alias.</summary>
    public string HighlightAlias { get; set; } = "";

    /// <summary>Pick a random voice per user (from all engines minus the active ignore profile) when they have none.</summary>
    public bool RandomVoices { get; set; }

    /// <summary>With random voices, keep a user's first random voice until it is reset.</summary>
    public bool StickyRandom { get; set; } = true;

    /// <summary>Legacy: skip messages starting with "!". Newer files use <see cref="FilterProfile.IgnoredPrefixes"/>.</summary>
    public bool IgnoreCommands { get; set; } = true;

    /// <summary>Legacy: replace links with the word "link". Newer files use <see cref="FilterProfile.UrlMode"/>.</summary>
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

/// <summary>What happens to a message containing a bad word.</summary>
public enum BadWordAction { Disabled, Strip, Skip, Replace }

/// <summary>What happens to links.</summary>
public enum UrlMode { Keep, Replace, Strip, Skip }

/// <summary>A named set of text filters and ignore rules.</summary>
public sealed class FilterProfile
{
    public const string DefaultName = "Default";

    public string Name { get; set; } = "";

    public List<string> BadWords { get; set; } = [];
    public BadWordAction BadWordAction { get; set; } = BadWordAction.Disabled;

    /// <summary>Spoken in place of a bad word with <see cref="BadWordAction.Replace"/>.</summary>
    public string BadWordReplacement { get; set; } = "beep";

    /// <summary>Link handling; null follows the legacy <see cref="SpeakChatSettings.SkipLinks"/> switch.</summary>
    public UrlMode? UrlMode { get; set; }

    public string UrlWord { get; set; } = TextFilter.LinkWord;

    /// <summary>Messages with more words are cut; 0 is unlimited.</summary>
    public int MaxWords { get; set; }

    /// <summary>Messages with more characters are cut; 0 uses the legacy <see cref="SpeakChatSettings.MaxLength"/>.</summary>
    public int MaxCharacters { get; set; }

    /// <summary>Runs of the same character longer than this are shortened to it ("noooooo" → "nooo"); 0 is off.</summary>
    public int MaxRepeatedCharacters { get; set; } = 3;

    /// <summary>Runs of the same word (emote spam) longer than this are shortened to it; 0 is off.</summary>
    public int MaxRepeatedWords { get; set; } = 2;

    /// <summary>Remove platform emotes (positions provided by the platform, plus names from emote providers).</summary>
    public bool StripEmotes { get; set; } = true;

    /// <summary>Keep the first occurrence of each emote when stripping.</summary>
    public bool AllowFirstEmote { get; set; }

    /// <summary>Emotes never stripped.</summary>
    public List<string> AllowedEmotes { get; set; } = [];

    /// <summary>Remove Unicode emoji.</summary>
    public bool StripEmoji { get; set; }

    /// <summary>Messages starting with one of these are ignored; null follows the legacy <see cref="SpeakChatSettings.IgnoreCommands"/>.</summary>
    public List<string>? IgnoredPrefixes { get; set; }

    /// <summary>Users never read aloud.</summary>
    public List<string> IgnoredUsers { get; set; } = [];

    /// <summary>Ignore well-known chat bots (<see cref="KnownBots"/>) and <see cref="BotNames"/>.</summary>
    public bool IgnoreBots { get; set; } = true;

    public List<string> BotNames { get; set; } = [];

    /// <summary>Ignore Twitch shared-chat messages that come from another channel.</summary>
    public bool IgnoreSharedChat { get; set; }

    /// <summary>Common chat bots ignored with <see cref="IgnoreBots"/>.</summary>
    [JsonIgnore]
    public static IReadOnlyList<string> KnownBots { get; } =
    [
        "nightbot", "streamelements", "streamlabs", "moobot", "fossabot", "wizebot", "sery_bot", "soundalerts",
        "pokemoncommunitygame", "kofistreambot", "botrixoficial", "commanderroot", "streamerbot",
    ];
}

/// <summary>Voices and locales excluded from random voice picks.</summary>
public sealed class VoiceIgnoreProfile
{
    public string Name { get; set; } = "";

    /// <summary>"engineId|voiceId" or bare voice ids.</summary>
    public List<string> Voices { get; set; } = [];

    /// <summary>Locale prefixes such as "de" or "en-GB".</summary>
    public List<string> Locales { get; set; } = [];
}

/// <summary>Chat commands (the built-in "!tts" command and speaking commands).</summary>
public sealed class TtsCommandSettings
{
    public bool Enabled { get; set; } = true;

    /// <summary>The command word, renameable.</summary>
    public string Prefix { get; set; } = "!tts";

    /// <summary>Suppress chat replies.</summary>
    public bool SilenceOutput { get; set; }

    /// <summary>Reply when a user lacks permission; empty for no reply.</summary>
    public string NotAllowedText { get; set; } = "@%user%, you are not allowed to use that.";

    /// <summary>Prefixes whose remaining text is spoken (also in command mode).</summary>
    public List<string> SpeakCommands { get; set; } = ["!say", "!\""];

    /// <summary>Who may use <see cref="SpeakCommands"/>.</summary>
    public ChatAudience SpeakCommandPermission { get; set; } = ChatAudience.Everyone;

    /// <summary>Custom speaking commands with their own alias and permission.</summary>
    public List<TtsCustomCommand> CustomCommands { get; set; } = [];

    /// <summary>Seconds a viewer waits between viewer commands; moderators are exempt.</summary>
    public int ViewerCooldownSeconds { get; set; } = 5;

    /// <summary>Per sub-command cooldown overrides in seconds, keyed by sub-command ("voice", "speed", ...).</summary>
    public Dictionary<string, int> Cooldowns { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Per sub-command permission overrides, keyed by sub-command.</summary>
    public Dictionary<string, ChatAudience> Permissions { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Aliases viewers may pick with "!tts voice"; empty allows every alias.</summary>
    public List<string> ViewerAliases { get; set; } = [];

    /// <summary>Let viewers set their own nickname with "!tts nick".</summary>
    public bool AllowViewerNicknames { get; set; } = true;
}

public sealed class TtsCustomCommand
{
    public string Command { get; set; } = "";
    public string Alias { get; set; } = "";
    public ChatAudience Permission { get; set; } = ChatAudience.Everyone;
}

/// <summary>Speaking activity (follows, subs, raids...) from "chat.activity".</summary>
public sealed class EventSpeechSettings
{
    public bool Enabled { get; set; }

    /// <summary>Alias for every event that has none of its own; empty uses the default voice.</summary>
    public string Alias { get; set; } = "";

    public string FilterProfile { get; set; } = "";

    /// <summary>Templates per feed kind ("Follow", "Subscription", ...). Missing kinds use the built-in defaults.</summary>
    public List<EventTemplate> Templates { get; set; } = [];

    public int MinRaidViewers { get; set; }
    public int MinCheerBits { get; set; }
    public double MinTipAmount { get; set; }

    /// <summary>Speak at most one follow per user per session.</summary>
    public bool SuppressDuplicateFollows { get; set; } = true;
}

/// <summary>What is said for one kind of activity.</summary>
public sealed class EventTemplate
{
    /// <summary>A FeedKind name ("Follow", "Subscription", "GiftSubscription", "Tip", "Cheer", "Raid", ...).</summary>
    public string Kind { get; set; } = "";

    public bool Enabled { get; set; } = true;

    /// <summary>Alias for this event; empty uses the events alias.</summary>
    public string Alias { get; set; } = "";

    /// <summary>One message is picked at random by weight.</summary>
    public List<EventMessage> Messages { get; set; } = [];
}

public sealed class EventMessage
{
    public string Text { get; set; } = "";
    public int Weight { get; set; } = 1;
    public bool Enabled { get; set; } = true;
}

/// <summary>Stopping speech when moderators act.</summary>
public sealed class ModerationSettings
{
    public bool SkipOnMessageDeleted { get; set; } = true;
    public bool SkipOnTimeout { get; set; } = true;
    public bool SkipOnBan { get; set; } = true;
}

/// <summary>What to do when the queue is full.</summary>
public enum QueueDropPolicy { DropNewest, DropOldest }

public sealed class SpeechQueueSettings
{
    /// <summary>Most texts waiting at once; 0 is unlimited.</summary>
    public int MaxLength { get; set; }

    public QueueDropPolicy DropPolicy { get; set; } = QueueDropPolicy.DropNewest;

    /// <summary>When set, every spoken text is saved into this folder (WAV, or the engine's format).</summary>
    public string SaveFolder { get; set; } = "";
}

/// <summary>Holds speech while the microphone is louder than a threshold.</summary>
public sealed class VoiceGateSettings
{
    /// <summary>Name of the active profile; empty switches VoiceGate off.</summary>
    public string ActiveProfile { get; set; } = "";

    public List<VoiceGateProfile> Profiles { get; set; } = [];
}

public sealed class VoiceGateProfile
{
    public string Name { get; set; } = "";

    /// <summary>Input device id; empty uses the default microphone.</summary>
    public string DeviceId { get; set; } = "";

    /// <summary>Level in dBFS above which speech is held.</summary>
    public double ThresholdDb { get; set; } = -35;

    /// <summary>How long the level must stay below the threshold before speech resumes.</summary>
    public int ReleaseMilliseconds { get; set; } = 800;
}
