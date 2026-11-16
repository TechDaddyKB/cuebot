using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Assistant;

namespace Cuebot.App.ViewModels.Assistant;

/// <summary>
/// Assistant page: chat with the AI the streamer picked (an AI CLI they are signed in to, or an API key) to set Cuebot up.
/// The AI works through Cuebot's MCP tools; deletes, imports and chat sends wait for Approve unless the streamer allowed
/// them (Approvals, or "Always allow" for one tool). Anything public (a feature request) always asks.
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
        RefreshAllowedTools();
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
        // A check that finishes after the streamer picked another AI must not overwrite the newer one's fields (the
        // page's first Automatic check could land after a quick switch and show the wrong provider).
        var asked = SelectedChoice;
        var chosen = asked.Provider ?? await _service.ResolveProviderAsync();
        if (!ReferenceEquals(asked, SelectedChoice)) return;
        Effective = chosen;
        if (chosen is null)
        {
            ProviderReady = false;
            ProviderStatus = "No AI is ready yet. Choose one in the AI list, then sign in to it or add its API key.";
            return;
        }
        var status = await _service.SafeStatusAsync(chosen);
        if (!ReferenceEquals(asked, SelectedChoice)) return;
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

    // ---- Approvals ----------------------------------------------------------------------------------------------

    public IReadOnlyList<ApprovalChoice> ApprovalChoices { get; } =
    [
        new(AssistantApprovals.Ask, "Ask every time"),
        new(AssistantApprovals.ThisChat, "Allow everything in this chat"),
        new(AssistantApprovals.Always, "Always allow (trusted)"),
    ];

    /// <summary>When tools that change or remove things need Approve; stored in assistant.json.</summary>
    public ApprovalChoice SelectedApproval
    {
        get => ApprovalChoices.First(c => c.Value == _service.Approvals);
        set
        {
            if (value is null || value.Value == _service.Approvals) return;
            _service.Approvals = value.Value;
            OnPropertyChanged();
        }
    }

    /// <summary>Tools remembered with "Always allow" on an Approve card; each can be forgotten.</summary>
    public ObservableCollection<AllowedTool> AllowedTools { get; } = [];

    public bool HasAllowedTools => AllowedTools.Count > 0;

    private void RefreshAllowedTools()
    {
        AllowedTools.Clear();
        foreach (var tool in _service.AlwaysAllowedTools) AllowedTools.Add(new AllowedTool(tool, ToolLabel(tool)));
        OnPropertyChanged(nameof(HasAllowedTools));
    }

    [RelayCommand]
    private void ForgetTool(AllowedTool tool)
    {
        _service.Forget(tool.Tool);
        RefreshAllowedTools();
    }

    [RelayCommand]
    private void ForgetAllTools()
    {
        _service.ForgetAll();
        RefreshAllowedTools();
    }

    /// <summary>"Always allow" on a card: remembered, so later calls of that tool run without a card.</summary>
    private void AlwaysAllow(string tool)
    {
        if (_service.AlwaysAllow(tool)) RefreshAllowedTools();
    }

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

    private bool CanSend() => !IsBusy && (Input.Trim().Length > 0 || Attachments.Count > 0);

    // ---- Working indicator: what the AI is doing in the running turn, and for how long ----

    /// <summary>What the AI is doing right now (Thinking, Using a tool, Writing, Waiting for your answer).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BusyText))]
    private string _activity = "";

    private DateTimeOffset _turnStarted;
    private DispatcherTimer? _busyClock;

    /// <summary>Now; tests move it.</summary>
    internal Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;

    /// <summary>The working strip under the conversation, e.g. "Using list actions... 1:05".</summary>
    public string BusyText
    {
        get
        {
            if (!IsBusy) return "";
            var elapsed = Clock() - _turnStarted;
            if (elapsed < TimeSpan.Zero) elapsed = TimeSpan.Zero;
            var time = $"{(int)elapsed.TotalMinutes}:{elapsed.Seconds:00}";
            var hint = elapsed >= LongTurn && !Activity.StartsWith("Waiting", StringComparison.Ordinal) ? " - still working; Stop ends it" : "";
            return $"{Activity} {time}{hint}";
        }
    }

    /// <summary>After this long the strip says it is still working, and how to stop it.</summary>
    internal static readonly TimeSpan LongTurn = TimeSpan.FromMinutes(1);

    private void StartBusyClock()
    {
        _turnStarted = Clock();
        Activity = "Thinking...";
        _busyClock ??= new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => OnPropertyChanged(nameof(BusyText)));
        _busyClock.Start();
    }

    private void StopBusyClock()
    {
        _busyClock?.Stop();
        Activity = "";
    }

    /// <summary>Once the streamer answers an Approve card, the strip goes back to working.</summary>
    private async Task AfterAnswerAsync(AssistantConfirmation confirmation)
    {
        await confirmation.Answer;
        if (IsBusy && Activity.StartsWith("Waiting", StringComparison.Ordinal)) Activity = "Working...";
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(BusyText));
        Badge = value ? "…" : "";   // the sidebar shows it is still working from any page
    }

    /// <summary>
    /// Files picked with Attach (or dropped on the message box), sent as full paths at the end of the message so every AI
    /// can use them (edit_image, a sound or video step...). Nothing is uploaded: the paths are on this computer.
    /// </summary>
    public ObservableCollection<AssistantAttachment> Attachments { get; } = [];

    public bool HasAttachments => Attachments.Count > 0;

    public void Attach(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            if (path.Length > 0 && Attachments.All(a => !string.Equals(a.Path, path, StringComparison.Ordinal)) && Attachments.Count < 10)
                Attachments.Add(new AssistantAttachment(path));
        OnAttachmentsChanged();
    }

    [RelayCommand]
    private void RemoveAttachment(AssistantAttachment attachment)
    {
        Attachments.Remove(attachment);
        OnAttachmentsChanged();
    }

    private void OnAttachmentsChanged()
    {
        OnPropertyChanged(nameof(HasAttachments));
        SendCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The message as sent: the text, then the attached files' paths.</summary>
    internal static string WithAttachments(string text, IReadOnlyList<AssistantAttachment> attachments)
    {
        if (attachments.Count == 0) return text;
        var list = string.Join("\n", attachments.Select(a => "- " + a.Path));
        return (text.Length > 0 ? text + "\n\n" : "") + "Attached files:\n" + list;
    }

    [RelayCommand(CanExecute = nameof(CanSend))]
    private async Task SendAsync()
    {
        var text = WithAttachments(Input.Trim(), [.. Attachments]);
        Input = "";
        Attachments.Clear();
        OnAttachmentsChanged();
        Add(new AssistantItem(AssistantItemKind.User, text));
        IsBusy = true;
        StartBusyClock();
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
                        Activity = "Writing...";
                        break;
                    case AssistantToolStarted s:
                        Add(new AssistantItem(AssistantItemKind.Tool, ToolLabel(s.Tool)) { CallId = s.CallId });
                        Activity = $"Using {ToolLabel(s.Tool)}...";
                        answer = null;
                        break;
                    case AssistantToolFinished f:
                        if (Items.LastOrDefault(i => i.CallId == f.CallId) is { } tool) tool.Finish(f.Failed);
                        Activity = "Thinking...";
                        break;
                    case AssistantConfirmation c:
                        Add(new AssistantItem(AssistantItemKind.Confirmation, c.Summary)
                        {
                            Confirmation = c,
                            AlwaysAllowLabel = _service.AlwaysAsks(c.Tool) ? null : $"Always allow {ToolLabel(c.Tool)}",
                            OnAlwaysAllow = () => AlwaysAllow(c.Tool),
                        });
                        Activity = "Waiting for you to Approve or Deny...";
                        _ = AfterAnswerAsync(c);
                        answer = null;
                        break;
                    case AssistantAllowed allowed:
                        // No card, so nothing waits for the streamer; the line says what ran.
                        Add(new AssistantItem(AssistantItemKind.Notice, $"Allowed without asking: {allowed.Summary}"));
                        answer = null;
                        break;
                    case AssistantError e:
                        Add(new AssistantItem(AssistantItemKind.Error, e.Message));
                        break;
                    case AssistantChanges changes:
                        Add(new AssistantItem(AssistantItemKind.Changes, changes.Undo.Summary) { Undo = changes.Undo });
                        answer = null;
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
            foreach (var tool in Items.Where(i => i.IsTool && i.State == "running")) tool.State = "stopped";
            StopBusyClock();
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
        OnPropertyChanged(nameof(SelectedApproval));   // "Allow everything in this chat" ended
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

/// <summary>An entry in the Approvals picker.</summary>
public sealed record ApprovalChoice(AssistantApprovals Value, string Name)
{
    public override string ToString() => Name;
}

/// <summary>A tool remembered with "Always allow" (<see cref="Label"/> as the tool rows show it).</summary>
public sealed record AllowedTool(string Tool, string Label);

/// <summary>An entry in the provider picker; <see cref="Provider"/> is null for automatic.</summary>
public sealed record ProviderChoice(string Id, string Name, IAssistantProvider? Provider)
{
    public override string ToString() => Name;
}

public enum AssistantItemKind { User, Assistant, Tool, Confirmation, Error, Notice, Changes }

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

    /// <summary>Confirmation: the "Always allow" button's text; null for tools that always ask (they publish something).</summary>
    public string? AlwaysAllowLabel { get; init; }

    public bool CanAlwaysAllow => AlwaysAllowLabel is not null && AwaitingAnswer;

    internal Action? OnAlwaysAllow { get; init; }

    /// <summary>Changes: puts back what that turn changed; null once used, or for a card from an earlier session.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUndo))]
    private AssistantUndo? _undo;

    public bool CanUndo => Undo is not null;

    [RelayCommand]
    private async Task UndoChangesAsync()
    {
        if (Undo is not { } undo) return;
        Undo = null;
        try
        {
            State = await undo.UndoAsync();
        }
        catch (Exception ex)
        {
            State = "Could not undo: " + ex.Message;
        }
    }

    public bool IsChanges => Kind == AssistantItemKind.Changes;

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
        OnPropertyChanged(nameof(CanAlwaysAllow));
    }

    [RelayCommand]
    private void Approve() => Answer(true);

    [RelayCommand]
    private void Deny() => Answer(false);

    /// <summary>Approves and remembers the tool, so it runs without a card from now on.</summary>
    [RelayCommand]
    private void AlwaysAllow()
    {
        if (AlwaysAllowLabel is null || Confirmation is not { Answer.IsCompleted: false }) return;
        OnAlwaysAllow?.Invoke();
        Answer(true, "Approved - allowed without asking from now on");
    }

    private void Answer(bool approve, string? state = null)
    {
        if (Confirmation is not { } c || c.Answer.IsCompleted) return;
        if (approve) c.Approve();
        else c.Deny();
        State = state ?? (approve ? "Approved" : "Declined");
        OnPropertyChanged(nameof(AwaitingAnswer));
        OnPropertyChanged(nameof(CanAlwaysAllow));
    }
}

/// <summary>A file attached to the next message (shown as a chip with its name).</summary>
public sealed record AssistantAttachment(string Path)
{
    public string Name => System.IO.Path.GetFileName(Path);
}
