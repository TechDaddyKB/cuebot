using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Speech;

namespace Cuebot.App.ViewModels.Speech;

/// <summary>
/// One credential or option of an engine. Secret values are masked unless revealed, never appear in
/// <see cref="ToString"/>, and are only ever read through <see cref="Value"/> by the text box.
/// </summary>
public sealed partial class CredentialFieldViewModel : ObservableObject
{
    private readonly Func<string> _get;
    private readonly Action<string> _set;
    private readonly Action _changed;

    public CredentialFieldViewModel(string label, Func<string> get, Action<string> set, Action changed,
        bool secret = false, string placeholder = "", IReadOnlyList<string>? choices = null)
    {
        Label = label;
        _get = get;
        _set = set;
        _changed = changed;
        IsSecret = secret;
        Placeholder = placeholder;
        Choices = choices ?? [];
    }

    public string Label { get; }
    public string Placeholder { get; }
    public bool IsSecret { get; }
    public IReadOnlyList<string> Choices { get; }
    public bool IsChoice => Choices.Count > 0;
    public bool IsText => !IsChoice;

    /// <summary>Bullet while masked; '\0' shows the text.</summary>
    public char PasswordChar => IsSecret ? '•' : '\0';

    [ObservableProperty] private bool _revealed;

    public bool HasValue => _get().Length > 0;

    public string Value
    {
        get => _get();
        set
        {
            var trimmed = (value ?? "").Trim();
            if (trimmed == _get()) return;
            _set(trimmed);
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasValue));
            OnPropertyChanged(nameof(SelectedChoice));
            _changed();
        }
    }

    public string? SelectedChoice
    {
        get => Choices.FirstOrDefault(c => string.Equals(c, _get(), StringComparison.OrdinalIgnoreCase));
        set
        {
            if (value is not null) Value = value;
        }
    }

    /// <summary>The label only: the value (possibly a secret) must not leak into logs, tooltips or automation names.</summary>
    public override string ToString() => IsSecret ? $"{Label} (secret)" : Label;
}

/// <summary>A row of the engine's voice list after "Test".</summary>
public sealed record EngineVoiceRow(string Name, string Id, string Detail, string Styles);

/// <summary>An engine's status, credentials and voice test.</summary>
public sealed partial class EngineCardViewModel : ObservableObject
{
    private readonly SpeechPageContext _ctx;
    private readonly ITtsEngine _engine;

    public EngineCardViewModel(ITtsEngine engine, SpeechPageContext ctx)
    {
        _engine = engine;
        _ctx = ctx;
        Fields = BuildFields(engine.Id);
        Hint = HintFor(engine.Id);
    }

    public string Id => _engine.Id;
    public string Name => _engine.DisplayName;
    public bool IsAvailable => _engine.IsAvailable;
    public bool IsCloud => Fields.Count > 0;
    public string Status => _engine.IsAvailable ? "Available" : _engine.UnavailableReason ?? "Unavailable";
    public string Hint { get; }
    public bool HasHint => Hint.Length > 0;
    public IReadOnlyList<CredentialFieldViewModel> Fields { get; }
    public bool HasFields => Fields.Count > 0;

    public IReadOnlyList<EngineVoiceRow> Voices { get; private set; } = [];
    public bool HasVoices => Voices.Count > 0;

    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private string _testError = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(TestCommand))] private bool _isTesting;

    public override string ToString() => Name;

    private bool CanTest() => !IsTesting;

    /// <summary>Lists the engine's voices, proving the credentials work.</summary>
    [RelayCommand(CanExecute = nameof(CanTest))]
    private async Task Test()
    {
        TestError = "";
        TestResult = "";
        Voices = [];
        OnPropertyChanged(nameof(Voices));
        OnPropertyChanged(nameof(HasVoices));
        RefreshStatus();
        if (!_engine.IsAvailable)
        {
            TestError = Status;
            return;
        }
        IsTesting = true;
        try
        {
            _ctx.Voices.Invalidate(_engine.Id);
            var voices = await _ctx.Voices.GetAsync(_engine.Id);
            Voices = voices.Select(v => new EngineVoiceRow(v.Name, v.Id, v.Detail, string.Join(", ", v.Styles))).ToArray();
            TestResult = voices.Count == 1 ? "1 voice" : $"{voices.Count} voices";
            OnPropertyChanged(nameof(Voices));
            OnPropertyChanged(nameof(HasVoices));
        }
        catch (Exception ex)
        {
            TestError = Redact(ex.Message);
        }
        finally
        {
            IsTesting = false;
        }
    }

    public void RefreshStatus()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(Status));
    }

    /// <summary>Removes any configured secret from an error message before it is shown.</summary>
    internal string Redact(string message)
    {
        foreach (var field in Fields.Where(f => f.IsSecret && f.Value.Length >= 4))
            message = message.Replace(field.Value, "•••", StringComparison.Ordinal);
        return message;
    }

    private void OnCredentialChanged()
    {
        _ctx.Save();
        _ctx.Voices.Invalidate(_engine.Id);
        Voices = [];
        TestResult = "";
        TestError = "";
        OnPropertyChanged(nameof(Voices));
        OnPropertyChanged(nameof(HasVoices));
        RefreshStatus();
        _ctx.NotifyChoicesChanged(); // engine names in voice pickers show availability
    }

    private IReadOnlyList<CredentialFieldViewModel> BuildFields(string id)
    {
        var s = _ctx.Settings;
        var e = s.Engines;
        CredentialFieldViewModel F(string label, Func<string> get, Action<string> set, bool secret = false, string placeholder = "",
            IReadOnlyList<string>? choices = null) => new(label, get, set, OnCredentialChanged, secret, placeholder, choices);

        return id switch
        {
            "elevenlabs" => [F("API key", () => s.ElevenLabsApiKey, v => s.ElevenLabsApiKey = v, secret: true)],
            "azure" =>
            [
                F("Speech resource key", () => e.Azure.Key, v => e.Azure.Key = v, secret: true, placeholder: "KEY 1"),
                F("Region", () => e.Azure.Region, v => e.Azure.Region = v, placeholder: "eastus"),
            ],
            "polly" =>
            [
                F("Access key ID", () => e.Polly.AccessKeyId, v => e.Polly.AccessKeyId = v, secret: true),
                F("Secret access key", () => e.Polly.SecretAccessKey, v => e.Polly.SecretAccessKey = v, secret: true),
                F("Region", () => e.Polly.Region, v => e.Polly.Region = v, choices: SpeechDefaults.PollyRegions),
                F("Default engine", () => e.Polly.Engine, v => e.Polly.Engine = v, choices: SpeechDefaults.PollyEngines),
            ],
            "google" =>
            [
                F("API key", () => e.Google.ApiKey, v => e.Google.ApiKey = v, secret: true),
                F("Service account JSON file", () => e.Google.ServiceAccountFile, v => e.Google.ServiceAccountFile = v,
                    placeholder: "Used when no API key is set"),
            ],
            "watson" =>
            [
                F("API key", () => e.Watson.ApiKey, v => e.Watson.ApiKey = v, secret: true),
                F("Service URL", () => e.Watson.Url, v => e.Watson.Url = v,
                    placeholder: "https://api.us-south.text-to-speech.watson.cloud.ibm.com/instances/..."),
                F("ibm-credentials.env file", () => e.Watson.CredentialsFile, v => e.Watson.CredentialsFile = v,
                    placeholder: "Read when the key or URL is empty"),
            ],
            "uberduck" =>
            [
                F("API key", () => e.UberDuck.ApiKey, v => e.UberDuck.ApiKey = v, secret: true),
                F("API secret", () => e.UberDuck.ApiSecret, v => e.UberDuck.ApiSecret = v, secret: true),
            ],
            "ttsmonster" =>
            [
                F("API key or overlay URL", () => e.TtsMonster.ApiKey, v => e.TtsMonster.ApiKey = v, secret: true),
            ],
            "acapela" =>
            [
                F("Account email", () => e.Acapela.Email, v => e.Acapela.Email = v),
                F("Password", () => e.Acapela.Password, v => e.Acapela.Password = v, secret: true),
            ],
            "cereproc" =>
            [
                F("CereVoice Cloud email", () => e.CereProc.Email, v => e.CereProc.Email = v),
                F("Password", () => e.CereProc.Password, v => e.CereProc.Password = v, secret: true),
            ],
            _ => [],
        };
    }

    private static string HintFor(string id) => id switch
    {
        "windows" => "Built-in Windows voices (SAPI and OneCore). Windows only.",
        "espeak" => "Offline voices on Linux and Windows. Install espeak-ng (e.g. sudo apt install espeak-ng).",
        "elevenlabs" => "Create a key in your ElevenLabs profile. Free accounts include a monthly character quota.",
        "azure" => "Azure portal → Speech service → Keys and Endpoint.",
        "polly" => "Use an IAM user with the AmazonPollyReadOnlyAccess policy.",
        "google" => "Restrict the API key to the Cloud Text-to-Speech API.",
        "watson" => "IBM Cloud → Text to Speech → Manage → Credentials.",
        "uberduck" => "Account → API keys.",
        "ttsmonster" => "TTS Monster console → API key (or paste the overlay URL that contains it).",
        "acapela" => "Acapela Cloud account login.",
        "cereproc" => "CereVoice Cloud account login.",
        _ => "",
    };
}

/// <summary>Engines tab: one card per engine.</summary>
public sealed class SpeechEnginesViewModel
{
    public SpeechEnginesViewModel(SpeechPageContext ctx)
    {
        Cards = ctx.Speech.Engines.Select(e => new EngineCardViewModel(e, ctx)).ToArray();
    }

    public IReadOnlyList<EngineCardViewModel> Cards { get; }

    public IReadOnlyList<EngineCardViewModel> LocalEngines => [.. Cards.Where(c => !c.IsCloud)];

    public IReadOnlyList<EngineCardViewModel> CloudEngines => [.. Cards.Where(c => c.IsCloud)];

    public void RefreshStatus()
    {
        foreach (var card in Cards) card.RefreshStatus();
    }
}
