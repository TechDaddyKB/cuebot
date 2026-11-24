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
        if (engine is VoiceLibraryEngine library) Piper = new VoiceLibraryPanelViewModel(library, OnPiperChanged);
    }

    /// <summary>The voice library section (null for other engines).</summary>
    public VoiceLibraryPanelViewModel? Piper { get; }

    public bool HasPiper => Piper is not null;

    private void OnPiperChanged()
    {
        _ctx.Voices.Invalidate(_engine.Id);
        RefreshStatus();
        _ctx.NotifyChoicesChanged();
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
            "openai" =>
            [
                F("API key", () => e.OpenAi.ApiKey, v => e.OpenAi.ApiKey = v, secret: true, placeholder: "sk-..."),
                F("Model", () => e.OpenAi.Model, v => e.OpenAi.Model = v, choices: ["gpt-4o-mini-tts", "tts-1", "tts-1-hd"]),
                F("How the voices should sound (optional)", () => e.OpenAi.Instructions, v => e.OpenAi.Instructions = v,
                    placeholder: "e.g. Cheerful and a bit fast"),
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
        "openai" => "platform.openai.com → API keys. Paid per use; a chat message costs a fraction of a cent.",
        "piper" => "Free voices that run on this computer, no account needed: Piper voices old and new, Kokoro and other sherpa-onnx voices. Download them below or import voice files.",
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

/// <summary>A voice in the voice library, with what it is, what speaks it (or why nothing can) and a delete button.</summary>
public sealed partial class LibraryVoiceRow(string id, string detail, string note, Action<string> delete) : ObservableObject
{
    public string Id => id;

    /// <summary>"Kokoro voice · voice engine".</summary>
    public string Detail => detail;

    /// <summary>Why the voice cannot speak; empty when it can.</summary>
    public string Note => note;

    [RelayCommand]
    private void Delete() => delete(id);
}

/// <summary>
/// The voice library on the Engines tab: Cuebot's voice engine, Piper's voice catalog, importing voice files and
/// packages, and the voices in the library. Changes made to the voices folder outside Cuebot show up by themselves.
/// </summary>
public sealed partial class VoiceLibraryPanelViewModel : ObservableObject
{
    private readonly VoiceLibraryEngine _engine;
    private readonly Action _changed;
    private IReadOnlyList<PiperCatalogVoice> _catalog = [];

    public VoiceLibraryPanelViewModel(VoiceLibraryEngine engine, Action changed)
    {
        _engine = engine;
        _changed = changed;
        engine.Library.Changed += () => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            RefreshInstalled();
            _changed();
        });
        RefreshInstalled();
    }

    private VoiceLibrary Library => _engine.Library;

    public string FolderPath => Library.Directory;

    public bool CanGetEngine => VoiceEngine.CanInstall;

    public string EngineStatus => _engine.Engine.IsInstalled
        ? $"Cuebot's voice engine is installed (sherpa-onnx {VoiceEnginePins.SherpaVersion}). It speaks every voice below."
        : !VoiceEngine.CanInstall ? "Cuebot's voice engine has no build for this computer."
        : _engine.Store.FindProgram("") is not null
            ? $"Piper ({PiperRelease.Tag}) speaks your older Piper voices. Get the voice engine (about {VoiceEngine.DownloadMegabytes} MB, once) for Kokoro, sherpa-onnx and newer Piper voices."
            : $"Get the voice engine to speak these voices (about {VoiceEngine.DownloadMegabytes} MB, once).";

    public string EngineButton => _engine.Engine.IsInstalled ? "Repair voice engine" : "Get voice engine";

    public IReadOnlyList<LibraryVoiceRow> Installed { get; private set; } = [];

    public bool HasInstalled => Installed.Count > 0;

    /// <summary>Catalog voices matching <see cref="Filter"/>, not yet downloaded.</summary>
    public IReadOnlyList<PiperCatalogVoice> Available { get; private set; } = [];

    [ObservableProperty] private PiperCatalogVoice? _selectedVoice;
    [ObservableProperty] private string _progress = "";
    [ObservableProperty] private string _error = "";
    [ObservableProperty] [NotifyCanExecuteChangedFor(nameof(GetEngineCommand), nameof(DownloadVoiceCommand), nameof(LoadCatalogCommand))]
    private bool _busy;

    [ObservableProperty] private string _filter = "English";

    /// <summary>Import moves the picked files into the library instead of copying them.</summary>
    [ObservableProperty] private bool _moveImportedFiles;

    partial void OnFilterChanged(string value) => ApplyFilter();

    private bool Idle() => !Busy;

    [RelayCommand(CanExecute = nameof(Idle))]
    private Task GetEngine() => RunAsync("Voice engine", p => _engine.Engine.InstallAsync(p));

    [RelayCommand]
    private void OpenFolder() => Cuebot.App.Shell.FolderLauncher.Open(Library.Directory);

    /// <summary>Imports the files the view's file picker returned (see SpeechEnginesView).</summary>
    public Task ImportAsync(IReadOnlyList<string> paths) => paths.Count == 0 ? Task.CompletedTask
        : RunAsync("Importing", async p =>
        {
            var ids = await Library.ImportAsync(paths, MoveImportedFiles, p);
            p.Report($"Imported {string.Join(", ", ids)}");
        }, keepLastProgress: true);

    [RelayCommand(CanExecute = nameof(Idle))]
    private async Task LoadCatalog()
    {
        Error = "";
        Busy = true;
        try
        {
            _catalog = await _engine.Store.GetCatalogAsync();
            ApplyFilter();
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(Idle))]
    private Task DownloadVoice()
    {
        if (SelectedVoice is not { } voice)
        {
            Error = "Pick a voice first.";
            return Task.CompletedTask;
        }
        return RunAsync($"Downloading {voice.Display}", p => _engine.Store.DownloadVoiceAsync(voice,
            new Progress<double>(f => p.Report($"{f:P0}"))));
    }

    private async Task RunAsync(string what, Func<IProgress<string>, Task> work, bool keepLastProgress = false)
    {
        Error = "";
        Busy = true;
        var progress = new Progress<string>(m => Progress = $"{what}: {m}");
        try
        {
            await work(progress);
            if (!keepLastProgress) Progress = "";
        }
        catch (Exception ex)
        {
            Progress = "";
            Error = ex.Message;
        }
        finally
        {
            Busy = false;
            Library.Refresh();
        }
    }

    private void DeleteVoice(string id)
    {
        try
        {
            Library.Delete(id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Error = $"Could not delete {id}: {ex.Message}";
        }
    }

    private void RefreshInstalled()
    {
        Installed = [.. Library.Voices().Select(v =>
        {
            var (backend, why) = _engine.Choose(v);
            var by = backend switch
            {
                VoiceBackend.VoiceEngine => " · voice engine",
                VoiceBackend.PiperProgram => $" · Piper {PiperRelease.Tag}",
                _ => "",
            };
            return new LibraryVoiceRow(v.Id, v.FormatName + by + (v.Note is null ? "" : $" · {v.Note}"), why ?? "", DeleteVoice);
        })];
        OnPropertyChanged(nameof(Installed));
        OnPropertyChanged(nameof(HasInstalled));
        OnPropertyChanged(nameof(EngineStatus));
        OnPropertyChanged(nameof(EngineButton));
        ApplyFilter();
    }

    private void ApplyFilter()
    {
        var words = Filter.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Available = [.. _catalog.Where(v => !_engine.Store.IsInstalled(v.Key)
            && words.All(w => v.Display.Contains(w, StringComparison.OrdinalIgnoreCase) || v.Key.Contains(w, StringComparison.OrdinalIgnoreCase)))];
        OnPropertyChanged(nameof(Available));
        if (SelectedVoice is not null && !Available.Contains(SelectedVoice)) SelectedVoice = null;
    }
}
