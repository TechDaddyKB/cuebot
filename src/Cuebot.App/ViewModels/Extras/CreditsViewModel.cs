using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Credits;

namespace Cuebot.App.ViewModels.Extras;

public sealed class CreditsSectionViewModel(CreditsSection section)
{
    public string Id => section.Id;
    public string Title => section.Title;
    public string CountText => section.Entries.Count.ToString(CultureInfo.InvariantCulture);
    public IReadOnlyList<string> Names { get; } = section.Entries.Select(Describe).ToArray();

    private static string Describe(CreditsEntry e)
    {
        var extra = e.Detail.Length > 0 ? e.Detail
            : e.Amount > 0 ? e.Amount.ToString("0.##", CultureInfo.InvariantCulture)
            : "";
        if (e.Count > 1) extra = extra.Length > 0 ? $"{extra}, ×{e.Count}" : $"×{e.Count}";
        return extra.Length > 0 ? $"{e.User} ({extra})" : e.User;
    }
}

/// <summary>Current stream credits, the overlay JSON, reset, test data and the collection settings.</summary>
public sealed partial class CreditsViewModel : PageViewModel
{
    private readonly CreditsCollector _credits;
    private bool _loading;

    public CreditsViewModel(CreditsCollector credits) : base("Credits")
    {
        _credits = credits;
        var s = credits.Settings;
        _loading = true;
        ResetOnStreamOnline = s.ResetOnStreamOnline;
        AutoResetHours = s.AutoResetHours;
        RecordChatters = !s.DisabledSections.Contains(CreditsSections.Chatters, StringComparer.OrdinalIgnoreCase);
        IgnoredUsers = string.Join(", ", s.IgnoredUsers);
        _loading = false;
        credits.Changed += () => OnUi(Refresh);
        Refresh();
    }

    public ObservableCollection<CreditsSectionViewModel> Sections { get; } = [];

    [ObservableProperty] private bool _showTestData;
    [ObservableProperty] private string _json = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _resetOnStreamOnline;
    [ObservableProperty] private decimal? _autoResetHours;
    [ObservableProperty] private bool _recordChatters;
    [ObservableProperty] private string _ignoredUsers = "";

    public bool IsEmpty => Sections.Count == 0;

    partial void OnShowTestDataChanged(bool value) => Refresh();

    partial void OnResetOnStreamOnlineChanged(bool value) => Save();
    partial void OnAutoResetHoursChanged(decimal? value) => Save();
    partial void OnRecordChattersChanged(bool value) => Save();
    partial void OnIgnoredUsersChanged(string value) => Save();

    private void Save()
    {
        if (_loading) return;
        _credits.UpdateSettings(s =>
        {
            s.ResetOnStreamOnline = ResetOnStreamOnline;
            s.AutoResetHours = (int)Math.Max(0, AutoResetHours ?? 0);
            s.DisabledSections.RemoveAll(id => string.Equals(id, CreditsSections.Chatters, StringComparison.OrdinalIgnoreCase));
            if (!RecordChatters) s.DisabledSections.Add(CreditsSections.Chatters);
            s.IgnoredUsers = IgnoredUsers.Split([',', ';', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(u => u.TrimStart('@')).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        });
    }

    internal void Refresh()
    {
        var snapshot = ShowTestData ? _credits.Test() : _credits.Snapshot();
        Sections.Clear();
        foreach (var section in snapshot.Sections) Sections.Add(new CreditsSectionViewModel(section));
        Json = CreditsJson.Serialize(snapshot, indented: true);
        Summary = ShowTestData
            ? "Showing sample data (the real credits are unchanged)."
            : snapshot.StartedAt is { } started
                ? $"{snapshot.TotalEntries} credit(s) since {started.ToLocalTime().ToString("ddd HH:mm", CultureInfo.InvariantCulture)}"
                : "No credits yet this stream.";
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand]
    private void Reset()
    {
        _credits.Reset();
        ShowTestData = false;
        Refresh();
    }

    [RelayCommand]
    private void ToggleTestData() => ShowTestData = !ShowTestData;
}
