using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Assistant;

namespace Cuebot.App.ViewModels.Assistant;

/// <summary>
/// Assistant page: chat with the AI the streamer picked (an AI CLI they are signed in to, or an API key) to set Cuebot up.
/// The AI works through Cuebot's MCP tools; deletes, imports and chat sends wait for Approve.
/// </summary>
public sealed partial class AssistantViewModel : PageViewModel
{
    private readonly AssistantService _service;
    private readonly Action<string> _openUrl;
    private CancellationTokenSource? _turn;

    public AssistantViewModel(AssistantService service, Action<string>? openUrl = null) : base("Assistant")
    {
        _service = service;
        _openUrl = openUrl ?? Cuebot.App.Shell.UrlLauncher.Open;
        Choices = [new ProviderChoice("", "Automatic (first one ready)", null), .. service.Providers.Select(p => new ProviderChoice(p.Id, p.Name, p))];
        _selectedChoice = Choices.FirstOrDefault(c => c.Id.Equals(service.Settings.Provider, StringComparison.OrdinalIgnoreCase)) ?? Choices[0];
        foreach (var entry in service.History)
            if (Enum.TryParse<AssistantItemKind>(entry.Kind, out var kind)) Items.Add(new AssistantItem(kind, entry.Text) { State = entry.State });
        if (Items.Count > 0)
            Items.Add(new AssistantItem(AssistantItemKind.Notice, "Earlier conversation: the AI does not remember it. New chat clears it.") { Saved = false });
        _ = RefreshStatusAsync();
    }

    internal AssistantService Service => _service;

    // ---- Provider -----------------------------------------------------------------------------------------------

    public IReadOnlyList<ProviderChoice> Choices { get; }

    [ObservableProperty] private ProviderChoice _selectedChoice;
    [ObservableProperty] private string _providerStatus = "";
    [ObservableProperty] private bool _providerReady;

    /// <summary>The provider the settings below apply to: the chosen one, or (automatic) the one in use.</summary>
    [ObservableProperty] private IAssistantProvider? _effective;

    public ObservableCollection<string> Models { get; } = [];

    partial void OnSelectedChoiceChanged(ProviderChoice value)
    {
        _service.Settings.Provider = value.Id;
        _service.SaveSettings();
        _ = StartOverAsync();
    }

    partial void OnEffectiveChanged(IAssistantProvider? value)
    {
        Models.Clear();
        OnPropertyChanged(nameof(HasModels));
        OnPropertyChanged(nameof(Model));
        OnPropertyChanged(nameof(ApiKey));
        OnPropertyChanged(nameof(UsesApiKey));
        OnPropertyChanged(nameof(IsCli));
        OnPropertyChanged(nameof(CanSignIn));
        OnPropertyChanged(nameof(Command));
        OnPropertyChanged(nameof(HasProvider));
        OnPropertyChanged(nameof(ModelHint));
        OnPropertyChanged(nameof(IsLocal));
        OnPropertyChanged(nameof(ServerUrl));
        OnPropertyChanged(nameof(Thinking));
        OnPropertyChanged(nameof(ContextLength));
        OnPropertyChanged(nameof(AllTools));
    }

    public bool HasProvider => Effective is not null;
    public bool UsesApiKey => Effective?.Kind == AssistantProviderKind.ApiKey;
    public bool IsCli => Effective?.Kind == AssistantProviderKind.Cli;
    public bool IsLocal => Effective?.Kind == AssistantProviderKind.Local;

    /// <summary>Local model: the server's address (empty = Ollama on this computer).</summary>
    public string ServerUrl
    {
        get => Effective is { } p ? _service.Settings.For(p.Id).ServerUrl : "";
        set
        {
            if (Effective is not { } p || _service.Settings.For(p.Id).ServerUrl == value.Trim()) return;
            _service.Settings.For(p.Id).ServerUrl = value.Trim();
            _service.SaveSettings();
            OnPropertyChanged();
            _ = RefreshStatusAsync();
        }
    }

    /// <summary>Local model: think before answering (slower).</summary>
    public bool Thinking
    {
        get => Effective is { } p && _service.Settings.For(p.Id).Thinking;
        set => UpdateLocal(s => s.Thinking = value);
    }

    /// <summary>Local model: the context size in tokens.</summary>
    public decimal ContextLength
    {
        get => Effective is { } p ? _service.Settings.For(p.Id).ContextLength : 32768;
        set => UpdateLocal(s => s.ContextLength = (int)Math.Clamp(value, 4096, 262144));
    }

    /// <summary>Local model: every tool instead of the focused set.</summary>
    public bool AllTools
    {
        get => Effective is { } p && _service.Settings.For(p.Id).AllTools;
        set => UpdateLocal(s => s.AllTools = value);
    }

    /// <summary>Changes a local-model option; it applies from the next conversation (New chat), so typing does not clear this one.</summary>
    private void UpdateLocal(Action<AssistantProviderSettings> change, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (Effective is not { } p) return;
        change(_service.Settings.For(p.Id));
        _service.SaveSettings();
        OnPropertyChanged(name);
    }
    public bool CanSignIn => Effective?.CanSignIn == true;

    /// <summary>AI CLIs: the program to run when Cuebot does not find it (empty = search PATH and the usual install folders).</summary>
    public string Command
    {
        get => Effective is { } p ? _service.Settings.For(p.Id).Command : "";
        set
        {
            if (Effective is not { } p || _service.Settings.For(p.Id).Command == value.Trim()) return;
            _service.Settings.For(p.Id).Command = value.Trim();
            _service.SaveSettings();
            OnPropertyChanged();
            _ = RefreshStatusAsync();
        }
    }

    /// <summary>Starts the app's own sign-in (Codex: Sign in with ChatGPT) in the browser.</summary>
    [RelayCommand]
    private async Task SignInAsync()
    {
        if (Effective is not { CanSignIn: true } provider) return;
        try
        {
            if (await provider.SignInAsync() is { } url)
            {
                _openUrl(url);
                ProviderStatus = "Finish signing in in your browser, then press Refresh.";
            }
        }
        catch (Exception ex)
        {
            ProviderStatus = ex.Message;
        }
    }

    public string ModelHint => Effective?.Kind switch
    {
        AssistantProviderKind.Cli => "Empty uses the app's own default model.",
        AssistantProviderKind.Local => "Empty picks a downloaded model (Qwen3.5 if you have it).",
        _ => "Empty uses the newest suitable model for this key.",
    };

    public string Model
    {
        get => Effective is { } p ? _service.Settings.For(p.Id).Model : "";
        set
        {
            if (Effective is not { } p || _service.Settings.For(p.Id).Model == value.Trim()) return;
            _service.Settings.For(p.Id).Model = value.Trim();
            _service.SaveSettings();
            OnPropertyChanged();
        }
    }

    /// <summary>Shown masked; never logged.</summary>
    public string ApiKey
    {
        get => Effective is { } p ? _service.Settings.For(p.Id).ApiKey : "";
        set
        {
            if (Effective is not { } p || _service.Settings.For(p.Id).ApiKey == value.Trim()) return;
            _service.Settings.For(p.Id).ApiKey = value.Trim();
            _service.SaveSettings();
            OnPropertyChanged();
            _ = RefreshStatusAsync();
        }
    }

    [RelayCommand]
    private async Task RefreshStatusAsync()
    {
        var chosen = SelectedChoice.Provider ?? await _service.ResolveProviderAsync();
        Effective = chosen;
        if (chosen is null)
        {
            ProviderReady = false;
            ProviderStatus = "No AI is ready yet. Choose one in the AI list, then sign in to it or add its API key.";
            return;
        }
        var status = await _service.SafeStatusAsync(chosen);
        ProviderReady = status.Ready;
        ProviderStatus = SelectedChoice.Provider is null ? $"Using {chosen.Name}: {status.Message}" : status.Message;
        // Offer the provider's models straight away instead of only after List models.
        if (status.Ready && Models.Count == 0) await FillModelsAsync(reportEmpty: false);
    }

    [RelayCommand]
    private Task LoadModelsAsync() => FillModelsAsync(reportEmpty: true);

    private async Task FillModelsAsync(bool reportEmpty)
    {
        if (Effective is not { } provider) return;
        try
        {
            var models = await provider.ListModelsAsync();
            if (!ReferenceEquals(provider, Effective)) return;   // the streamer switched providers meanwhile
            Models.Clear();
            foreach (var model in models) Models.Add(model);
            OnPropertyChanged(nameof(HasModels));
            if (models.Count == 0 && reportEmpty) ProviderStatus = "This provider did not list any models; type a model name instead.";
        }
        catch (Exception ex)
        {
            if (reportEmpty) ProviderStatus = ex.Message;   // a silent refresh keeps the status line
        }
    }

    public bool HasModels => Models.Count > 0;

    // ---- Conversation -------------------------------------------------------------------------------------------

    public ObservableCollection<AssistantItem> Items { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    private string _input = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SendCommand))]
    [NotifyCanExecuteChangedFor(nameof(StopCommand))]
    private bool _isBusy;

    public bool IsEmpty => Items.Count == 0;

    private bool CanSend() => !IsBusy && Input.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = Input.Trim();
        Input = "";
        Add(new AssistantItem(AssistantItemKind.User, text));
        IsBusy = true;
        _turn = new CancellationTokenSource();
        AssistantItem? answer = null;
        try
        {
            await foreach (var update in _service.SendAsync(text, _turn.Token))
            {
                switch (update)
                {
                    case AssistantText t:
                        if (answer is null) Add(answer = new AssistantItem(AssistantItemKind.Assistant, ""));
                        answer.Text += t.Text;
                        break;
                    case AssistantToolStarted s:
                        Add(new AssistantItem(AssistantItemKind.Tool, ToolLabel(s.Tool)) { CallId = s.CallId });
                        answer = null;
                        break;
                    case AssistantToolFinished f:
                        if (Items.LastOrDefault(i => i.CallId == f.CallId) is { } tool) tool.Finish(f.Failed);
                        break;
                    case AssistantConfirmation c:
                        Add(new AssistantItem(AssistantItemKind.Confirmation, c.Summary) { Confirmation = c });
                        answer = null;
                        break;
                    case AssistantError e:
                        Add(new AssistantItem(AssistantItemKind.Error, e.Message));
                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            Add(new AssistantItem(AssistantItemKind.Notice, "Stopped."));
        }
        catch (Exception ex)
        {
            Add(new AssistantItem(AssistantItemKind.Error, ex.Message));
        }
        finally
        {
            foreach (var pending in Items.Where(i => i.AwaitingAnswer).ToList()) pending.Expire();
            IsBusy = false;
            _turn.Dispose();
            _turn = null;
            SaveHistory();
            await RefreshStatusAsync();
        }
    }

    [RelayCommand(CanExecute = nameof(IsBusy))]
    private void Stop() => _turn?.Cancel();

    [RelayCommand]
    private Task NewChatAsync() => StartOverAsync();

    private async Task StartOverAsync()
    {
        _turn?.Cancel();
        await _service.NewChatAsync();
        Items.Clear();
        SaveHistory();
        OnPropertyChanged(nameof(IsEmpty));
        await RefreshStatusAsync();
    }

    private void Add(AssistantItem item)
    {
        Items.Add(item);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private static string ToolLabel(string tool) => tool.Replace('_', ' ');

    private void SaveHistory() =>
        _service.SaveHistory(Items.Where(i => i.Saved).Select(i => new AssistantHistoryEntry { Kind = i.Kind.ToString(), Text = i.Text, State = i.State }));
}

/// <summary>An entry in the provider picker; <see cref="Provider"/> is null for automatic.</summary>
public sealed record ProviderChoice(string Id, string Name, IAssistantProvider? Provider)
{
    public override string ToString() => Name;
}

public enum AssistantItemKind { User, Assistant, Tool, Confirmation, Error, Notice }

/// <summary>One entry in the conversation.</summary>
public sealed partial class AssistantItem(AssistantItemKind kind, string text) : ObservableObject
{
    public AssistantItemKind Kind { get; } = kind;

    [ObservableProperty] private string _text = text;
    [ObservableProperty] private string _state = kind == AssistantItemKind.Tool ? "running" : "";

    internal string? CallId { get; init; }

    /// <summary>False for page notes that are not part of the conversation (not saved).</summary>
    internal bool Saved { get; init; } = true;

    public AssistantConfirmation? Confirmation { get; init; }

    public bool IsUser => Kind == AssistantItemKind.User;
    public bool IsAssistant => Kind == AssistantItemKind.Assistant;
    public bool IsTool => Kind == AssistantItemKind.Tool;
    public bool IsConfirmation => Kind == AssistantItemKind.Confirmation;
    public bool IsError => Kind == AssistantItemKind.Error;
    public bool IsNotice => Kind == AssistantItemKind.Notice;

    public bool AwaitingAnswer => Confirmation is { Answer.IsCompleted: false };

    internal void Finish(bool failed) => State = failed ? "failed" : "done";

    /// <summary>The turn ended before the streamer answered: nothing was done.</summary>
    internal void Expire()
    {
        Confirmation?.Deny();
        State = "Not answered - nothing was done";
        OnPropertyChanged(nameof(AwaitingAnswer));
    }

    [RelayCommand]
    private void Approve() => Answer(true);

    [RelayCommand]
    private void Deny() => Answer(false);

    private void Answer(bool approve)
    {
        if (Confirmation is not { } c || c.Answer.IsCompleted) return;
        if (approve) c.Approve();
        else c.Deny();
        State = approve ? "Approved" : "Declined";
        OnPropertyChanged(nameof(AwaitingAnswer));
    }
}
