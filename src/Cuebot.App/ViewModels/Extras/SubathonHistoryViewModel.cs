using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Chat;
using Cuebot.Core.Spending;
using Cuebot.Core.Subathon;

namespace Cuebot.App.ViewModels.Extras;

/// <summary>One change in the History list: when, who, from where, what it was and what it added.</summary>
public sealed class HistoryRowViewModel(SubathonHistoryEntry e, string owner, bool current)
{
    public SubathonHistoryEntry Entry => e;
    public string Id => e.Id;
    public string Time => e.Time.ToLocalTime().ToString("ddd d MMM yyyy HH:mm", CultureInfo.InvariantCulture);
    public string Owner => owner.Length > 0 ? owner : "(deleted)";
    public string Who => e.User.Length > 0 ? e.User : e.Kind == HistoryKind.Event ? "(no name)" : "You";
    public string Source => SubathonService.SourceName(e);
    public bool IsRemoved => e.Removed;
    public bool IsCurrent => current;
    public bool HasAmount => e.Amount > 0;

    /// <summary>"Wishlist gift, $10.00", "Cheer, 500 Bits", "Set time left".</summary>
    public string What
    {
        get
        {
            var what = e.Reason.Length > 0 ? e.Reason : e.Kind.ToString();
            if (e.Unit == nameof(SpendUnit.Money) && e.Amount > 0) return $"{what}, {SpendingText.Money(e.Amount, e.Currency)}";
            if (e.Unit is nameof(SpendUnit.Bits) or nameof(SpendUnit.Kicks) or nameof(SpendUnit.Jewels))
                return $"{what}, {e.Quantity.ToString("0.##", CultureInfo.InvariantCulture)} {e.Unit}";
            if (e.Unit == "Raid") return $"{what}, {e.Quantity.ToString("0", CultureInfo.InvariantCulture)} viewers";
            return e.Quantity > 1 ? $"{what} ×{e.Quantity.ToString("0.##", CultureInfo.InvariantCulture)}" : what;
        }
    }

    public string Change => SubathonService.Change(e);

    /// <summary>"Removed", "Test", "Changed", "Before the last reset", and the note, so the streamer sees what it counts for.</summary>
    public string Badges => string.Join(" · ", new[]
    {
        e.Removed ? "Removed" : "",
        e.IsTest ? "Test" : "",
        e.OriginalValue is not null || e.OriginalAmount is not null ? "Changed" : "",
        e.Multiplier != 1 ? $"×{e.Multiplier.ToString("0.##", CultureInfo.InvariantCulture)}" : "",
        current ? "" : "Before the last reset",
        e.Note,
    }.Where(b => b.Length > 0));
}

/// <summary>
/// The History tab: every change to every timer and goal (events, changes by hand, "set" changes), filtered by timer or
/// goal, period, name, source and test events, with totals by source. A change can be corrected (new value, new money
/// amount, a note), removed or put back; its timer or goal follows. Exports to CSV.
/// </summary>
public sealed partial class SubathonHistoryViewModel : ObservableObject
{
    public const string AllOwners = "Every timer and goal";
    public const string AllSources = "Every source";
    public const int ShownLimit = 500;
    public static IReadOnlyList<string> Periods { get; } = ["All time", "Today", "This week", "This month", "This year", "Dates below"];

    private readonly SubathonService _service;
    private readonly TimeProvider _time;
    private readonly List<(string Label, string Id, bool IsGoal)> _owners = [];
    private bool _loading;

    public SubathonHistoryViewModel(SubathonService service, TimeProvider? time = null)
    {
        _service = service;
        _time = time ?? TimeProvider.System;
        Refresh();
    }

    public ObservableCollection<HistoryRowViewModel> Rows { get; } = [];
    public ObservableCollection<string> OwnerChoices { get; } = [AllOwners];
    public ObservableCollection<string> SourceChoices { get; } = [AllSources];

    /// <summary>Timers and goals a correction can be added to.</summary>
    public ObservableCollection<string> AddChoices { get; } = [];

    [ObservableProperty] private string _ownerFilter = AllOwners;
    [ObservableProperty] private string _period = "All time";
    [ObservableProperty] private string _fromFilter = "";
    [ObservableProperty] private string _toFilter = "";
    [ObservableProperty] private string _nameFilter = "";
    [ObservableProperty] private string _sourceFilter = AllSources;
    [ObservableProperty] private bool _testOnly;
    [ObservableProperty] private bool _showRemoved = true;

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _bySource = "";
    [ObservableProperty] private string _status = "";

    [ObservableProperty] private HistoryRowViewModel? _selected;
    [ObservableProperty] private decimal? _editValue;
    [ObservableProperty] private decimal? _editAmount;
    [ObservableProperty] private string _editNote = "";
    [ObservableProperty] private string _editHint = "";

    [ObservableProperty] private string _addTo = "";
    [ObservableProperty] private decimal? _addValue;
    [ObservableProperty] private string _addName = "";
    [ObservableProperty] private string _addNote = "";

    [ObservableProperty] private bool _confirmRemoveTests;
    [ObservableProperty] private bool _confirmRemoveShown;

    public bool HasSelection => Selected is not null;
    public bool SelectedIsRemoved => Selected?.IsRemoved == true;
    public bool SelectedIsCounted => Selected is { IsRemoved: false };
    public bool SelectedHasAmount => Selected is { HasAmount: true, IsRemoved: false };
    public string EditUnit => Selected?.Entry.IsGoal == true ? "points" : "seconds";
    public string AddUnit => _owners.FirstOrDefault(o => o.Label == AddTo).IsGoal ? "points (negative takes away)" : "seconds (negative takes away)";
    public bool IsEmpty => Rows.Count == 0;

    partial void OnOwnerFilterChanged(string value) => Refresh();
    partial void OnFromFilterChanged(string value) => Refresh();
    partial void OnToFilterChanged(string value) => Refresh();
    partial void OnNameFilterChanged(string value) => Refresh();
    partial void OnSourceFilterChanged(string value) => Refresh();
    partial void OnTestOnlyChanged(bool value) => Refresh();
    partial void OnShowRemovedChanged(bool value) => Refresh();
    partial void OnAddToChanged(string value) => OnPropertyChanged(nameof(AddUnit));

    /// <summary>A period fills in the dates (in local time; weeks start on Monday); "Dates below" leaves them to type.</summary>
    partial void OnPeriodChanged(string value)
    {
        var now = _time.GetLocalNow();
        var today = new DateTimeOffset(now.Date, now.Offset);
        DateTimeOffset? from = value switch
        {
            "Today" => today,
            "This week" => today.AddDays(-(((int)today.DayOfWeek + 6) % 7)),
            "This month" => new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, now.Offset),
            "This year" => new DateTimeOffset(now.Year, 1, 1, 0, 0, 0, now.Offset),
            _ => null,
        };
        if (value == "Dates below") return;
        _loading = true;
        FromFilter = from?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        ToFilter = "";
        _loading = false;
        Refresh();
    }

    partial void OnSelectedChanged(HistoryRowViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectedIsRemoved));
        OnPropertyChanged(nameof(SelectedIsCounted));
        OnPropertyChanged(nameof(SelectedHasAmount));
        OnPropertyChanged(nameof(EditUnit));
        if (value is null) return;
        EditValue = (decimal)value.Entry.Value;
        EditAmount = value.HasAmount ? (decimal)value.Entry.Amount : null;
        EditNote = value.Entry.Note;
        EditHint = value.IsCurrent
            ? $"Changes here also change '{value.Owner}' now."
            : $"This was before '{value.Owner}' was last reset, so changing it only changes the history, not the {(value.Entry.IsGoal ? "points" : "clock")}.";
    }

    private HistoryFilter Filter()
    {
        static DateTimeOffset? Date(string text, bool endOfDay)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (!DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)) return null;
            return endOfDay && d.TimeOfDay == TimeSpan.Zero && !text.Contains(':') ? d.AddDays(1).AddTicks(-1) : d;   // a date alone is the whole day
        }
        var owner = OwnerFilter == AllOwners ? null : _owners.FirstOrDefault(o => o.Label == OwnerFilter).Id;
        return new HistoryFilter(owner, Date(FromFilter, false), Date(ToFilter, true), NameFilter,
            SourceFilter == AllSources ? null : SourceFilter, TestOnly, ShowRemoved);
    }

    private bool FilterIsEmpty => OwnerFilter == AllOwners && FromFilter.Trim().Length == 0 && ToFilter.Trim().Length == 0
                                  && NameFilter.Trim().Length == 0 && SourceFilter == AllSources && !TestOnly;

    internal void Refresh()
    {
        if (_loading) return;
        LoadOwners();
        foreach (var source in _service.Summarize(new HistoryFilter()).BySource.Select(s => s.Source).Order(StringComparer.OrdinalIgnoreCase))
            if (!SourceChoices.Contains(source, StringComparer.OrdinalIgnoreCase)) SourceChoices.Add(source);

        var filter = Filter();
        var selectedId = Selected?.Id;
        var entries = _service.History(filter, ShownLimit);
        Rows.Clear();
        foreach (var e in entries) Rows.Add(new HistoryRowViewModel(e, _service.OwnerName(e), _service.IsCurrent(e)));
        Selected = selectedId is null ? null : Rows.FirstOrDefault(r => r.Id == selectedId);

        var sum = _service.Summarize(filter);
        var total = ViewIsGoal is null ? "" : ", " + ValueText(sum.Value);
        Summary = sum.Count == 0 && sum.Removed == 0
            ? "Nothing yet. Every change to a timer or goal is listed here as it happens, from this version on."
            : $"{sum.Count} change(s){total}" + (sum.Amount > 0 ? $", {Money(sum.Amount)} in purchases" : "")
              + (sum.Removed > 0 ? $"; {sum.Removed} removed" : "")
              + (entries.Count >= ShownLimit ? $". The newest {ShownLimit} are listed; narrow the dates to see older ones (the totals count them all)." : ".");
        BySource = string.Join("   ", sum.BySource.Select(s =>
            $"{s.Source}: {s.Count}" + (s.Amount > 0 ? $" · {Money(s.Amount)}" : "") + (ViewIsGoal is null ? "" : $" · {ValueText(s.Value)}")));
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void LoadOwners()
    {
        _owners.Clear();
        foreach (var t in _service.Timers()) _owners.Add(($"Timer: {t.Name}", t.Id, false));
        foreach (var g in _service.Goals()) _owners.Add(($"Goal: {g.Name}", g.Id, true));
        var labels = _owners.Select(o => o.Label).ToList();
        Sync(OwnerChoices, [AllOwners, .. labels]);
        Sync(AddChoices, labels);
        if (!OwnerChoices.Contains(OwnerFilter))
        {
            _loading = true;
            OwnerFilter = AllOwners;
            _loading = false;
        }
        if (!AddChoices.Contains(AddTo)) AddTo = AddChoices.FirstOrDefault() ?? "";
    }

    private static void Sync(ObservableCollection<string> list, IReadOnlyList<string> wanted)
    {
        if (list.SequenceEqual(wanted)) return;
        foreach (var gone in list.Where(x => !wanted.Contains(x)).ToList()) list.Remove(gone);
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = list.IndexOf(wanted[i]);
            if (at < 0) list.Insert(i, wanted[i]);
            else if (at != i) list.Move(at, i);
        }
    }

    /// <summary>Whether the list shows goals (points) or timers (time); null when it mixes both, so totals are not added up.</summary>
    private bool? ViewIsGoal => OwnerFilter == AllOwners
        ? _owners.Select(o => o.IsGoal).Distinct().ToList() is [var only] ? only : null
        : _owners.FirstOrDefault(o => o.Label == OwnerFilter).IsGoal;

    private string ValueText(double value) =>
        SubathonService.Change(new SubathonHistoryEntry { Value = value, IsGoal = ViewIsGoal == true });

    private string Money(double amount) => SpendingText.Money(amount, Rows.FirstOrDefault(r => r.Entry.Currency.Length > 0)?.Entry.Currency ?? "");

    private void Try(Action action, string done)
    {
        try
        {
            action();
            Status = done;
        }
        catch (ArgumentException ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void SaveEdit()
    {
        if (Selected is not { } row) return;
        var e = row.Entry;
        double? amount = EditAmount is { } a && row.HasAmount && (double)a != e.Amount ? (double)a : null;
        double? value = amount is null && EditValue is { } v && (double)v != e.Value ? (double)v : null;
        var note = EditNote.Trim() != e.Note ? EditNote : null;
        if (amount is null && value is null && note is null)
        {
            Status = "Nothing to save: change the value, the amount or the note first.";
            return;
        }
        try
        {
            var changed = _service.ChangeEntry(e.Id, value, amount, note);
            Status = changed.Note != e.Note && changed.Note.Contains("could be applied", StringComparison.Ordinal)
                ? changed.Note
                : row.IsCurrent ? $"Saved. '{row.Owner}' was changed to match." : "Saved (in the history only).";
        }
        catch (ArgumentException ex)
        {
            Status = ex.Message;
        }
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (Selected is not { } row) return;
        Try(() => _service.RemoveEntry(row.Id), row.IsCurrent
            ? $"Removed {row.Who}'s {row.Change}. It was taken off '{row.Owner}'."
            : $"Removed {row.Who}'s {row.Change} from the history.");
    }

    [RelayCommand]
    private void RestoreSelected()
    {
        if (Selected is not { } row) return;
        Try(() => _service.RestoreEntry(row.Id), row.IsCurrent ? $"Put back on '{row.Owner}'." : "Put back in the history.");
    }

    [RelayCommand]
    private void RemoveTestEntries()
    {
        if (!ConfirmRemoveTests)
        {
            ConfirmRemoveTests = true;   // the button asks to be clicked again
            return;
        }
        ConfirmRemoveTests = false;
        var removed = _service.RemoveEntries(new HistoryFilter(TestOnly: true));
        Status = removed == 0 ? "There are no test changes to remove." : $"Removed {removed} test change(s) and took them off their timers and goals.";
    }

    [RelayCommand]
    private void RemoveShown()
    {
        if (FilterIsEmpty)
        {
            Status = "Pick a timer or goal, dates, a name, a source or test only first: this removes what the list shows.";
            return;
        }
        if (!ConfirmRemoveShown)
        {
            ConfirmRemoveShown = true;
            return;
        }
        ConfirmRemoveShown = false;
        var removed = _service.RemoveEntries(Filter());
        Status = $"Removed {removed} change(s) and took them off their timers and goals.";
    }

    [RelayCommand]
    private void AddCorrection()
    {
        var owner = _owners.FirstOrDefault(o => o.Label == AddTo);
        if (owner.Id is null)
        {
            Status = "Add a timer or goal first.";
            return;
        }
        if (AddValue is not { } amount || amount == 0)
        {
            Status = owner.IsGoal ? "Enter the points to add (or a negative number to take away)." : "Enter the seconds to add (or a negative number to take away).";
            return;
        }
        var reason = AddNote.Trim().Length > 0 ? AddNote.Trim() : "Correction";
        Try(() =>
        {
            if (owner.IsGoal) _service.AddPoints(owner.Id, (double)amount, user: AddName.Trim(), reason: reason);
            else _service.AddTime(owner.Id, (double)amount, user: AddName.Trim(), reason: reason);
        }, $"Added to '{owner.Label[(owner.Label.IndexOf(':') + 2)..]}'.");
        AddValue = null;
        AddName = "";
        AddNote = "";
    }

    /// <summary>The changes the filters show, oldest first, as CSV.</summary>
    public string ExportCsv() => _service.HistoryCsv(Filter());

    public void ReportExport(string fileName) => Status = $"Saved the history to {fileName}.";
}
