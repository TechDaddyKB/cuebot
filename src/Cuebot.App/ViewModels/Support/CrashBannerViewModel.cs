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

    /// <summary>The user chose Report: show the Support page (its report form is already prefilled).</summary>
    event Action? ReportRequested;
}

public sealed partial class CrashBannerViewModel : ObservableObject, ICrashPrompt
{
    public const string Prompt = "Cuebot closed unexpectedly last time — report it?";

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
    }

    [ObservableProperty] private bool _isVisible;

    public string Message => Prompt;

    /// <summary>The exception type and message (scrubbed).</summary>
    public string Detail { get; } = "";

    public event Action? ReportRequested;

    IRelayCommand ICrashPrompt.ReportCommand => ReportCommand;
    IRelayCommand ICrashPrompt.DismissCommand => DismissCommand;

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
