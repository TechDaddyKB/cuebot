using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.App.Diagnostics;

namespace Cuebot.App.ViewModels.Support;

/// <summary>The non-blocking "closed unexpectedly" prompt the main window shows as a banner.</summary>
public interface ICrashPrompt
{
    bool IsVisible { get; }
    string Message { get; }
    IRelayCommand ReportCommand { get; }
    IRelayCommand DismissCommand { get; }

    /// <summary>The user chose Review first: show the Support page (its report form is already prefilled).</summary>
    event Action? ReportRequested;
}

/// <summary>
/// After a crash: Send report (one click, through Cuebot's report intake), Review first (the prefilled report form),
/// or Dismiss, plus "send automatically from now on". With that setting on, the crash is sent as soon as Cuebot starts
/// and the banner only says where it went.
/// </summary>
public sealed partial class CrashBannerViewModel : ObservableObject, ICrashPrompt
{
    public const string Prompt = "Cuebot closed unexpectedly last time — report it?";
    public const string SendingMessage = "Cuebot closed unexpectedly last time. Sending the crash report…";

    private readonly CrashStore _store;
    private readonly ReportProblemViewModel _report;
    private readonly string? _crashFile;

    public CrashBannerViewModel(CrashStore store, ReportProblemViewModel report)
    {
        _store = store;
        _report = report;
        _crashFile = store.LatestUnseen();
        if (_crashFile is not null && CrashStore.Read(_crashFile) is { } crash)
            Detail = SecretScrubber.ScrubText(crash.Summary);
        _isVisible = _crashFile is not null;
        if (_crashFile is not null && report.SendCrashesAutomatically) Automatic = SendAsync();
    }

    [ObservableProperty] private bool _isVisible;

    [ObservableProperty] private string _message = Prompt;

    /// <summary>The exception type and message (scrubbed).</summary>
    [ObservableProperty] private string _detail = "";

    /// <summary>Asking (Send / Review / Dismiss shown) rather than sending or done.</summary>
    [ObservableProperty] private bool _isAsking = true;

    [ObservableProperty] private bool _isSending;

    /// <summary>Sent: the issue's link is offered.</summary>
    [ObservableProperty] private bool _isSent;

    /// <summary>The automatic send started at start-up (tests wait on it).</summary>
    public Task? Automatic { get; }

    /// <summary>Bound to "Send crash reports automatically from now on"; the setting lives with the report form.</summary>
    public bool SendAutomatically
    {
        get => _report.SendCrashesAutomatically;
        set
        {
            _report.SendCrashesAutomatically = value;
            OnPropertyChanged();
        }
    }

    public event Action? ReportRequested;

    IRelayCommand ICrashPrompt.ReportCommand => ReportCommand;
    IRelayCommand ICrashPrompt.DismissCommand => DismissCommand;

    /// <summary>One click: sends the crash with the report form's defaults (scrubbed log, connections, crash).</summary>
    [RelayCommand]
    public async Task SendAsync()
    {
        if (_crashFile is null || IsSending) return;
        IsAsking = false;
        IsSending = true;
        Message = SendingMessage;
        try
        {
            var sent = await _report.SendCrashAsync(_crashFile);
            if (sent)
            {
                _store.MarkAllSeen();   // only now: Review first still needs the file when sending failed
                IsSent = true;
                Message = "Cuebot closed unexpectedly last time. " + _report.Status;
            }
            else
            {
                Message = "Cuebot closed unexpectedly last time. The report could not be sent: " + _report.Error;
                IsAsking = true;   // Review first and Dismiss stay available
            }
        }
        finally
        {
            IsSending = false;
        }
    }

    [RelayCommand]
    private void OpenIssue() => _report.OpenSentIssueCommand.Execute(null);

    /// <summary>Review first: the prefilled report form on the Support page.</summary>
    [RelayCommand]
    private void Report()
    {
        if (_crashFile is null) return;
        _report.AttachCrash(_crashFile);   // read before the file is renamed
        _store.MarkAllSeen();
        IsVisible = false;
        ReportRequested?.Invoke();
    }

    [RelayCommand]
    private void Dismiss()
    {
        _store.MarkAllSeen();
        IsVisible = false;
    }
}
