using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Overlays.Events;
using Cuebot.Core.Overlays.Session;

namespace Cuebot.App.ViewModels.Extras;

/// <summary>One stored event in the Stream Data list.</summary>
public sealed class StreamEventRowViewModel(StoredStreamEvent e, bool thisStream)
{
    public StoredStreamEvent Event => e;
    public string Id => e.Id;
    public string Time => e.Time.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture);
    public string Type => e.Type;
    public string Name => e.Name.Length > 0 ? e.Name : "(no name)";
    public string Platform => e.Service;
    public string Amount => SessionDataStore.AmountTypes.Contains(e.Type) || e.Type == "raid"
        ? (e.Amount.ToString("0.##", CultureInfo.InvariantCulture) + (e.Event["currency"]?.ToString() is { Length: > 0 } c ? " " + c : ""))
        : e.Count == 1 ? "" : $"×{e.Count}";
    public bool IsTest => e.IsTest;

    /// <summary>"Test", "This stream only" and the like, so the streamer sees what the event counts towards.</summary>
    public string Badges => string.Join(" · ", new[]
    {
        e.IsTest ? "Test" : "",
        !e.InTotals ? "This stream only" : "",
        thisStream ? "" : "Earlier stream",
    }.Where(b => b.Length > 0));
}

/// <summary>
/// Stream Data: the events and totals behind labels, goal bars, event lists and custom widgets. Browse and filter the
/// stored events, edit or remove one (everything it counted in is recalculated), remove test events in bulk, override
/// totals, latest entries and top donations, choose when this stream's totals reset, and back the data up.
/// </summary>
public sealed partial class StreamDataViewModel : PageViewModel
{
    public const string PageTitle = "Stream Data";
    public const string Everything = "All";
    public static IReadOnlyList<string> TallyPeriods { get; } = ["This stream", "This week", "This month", "All time"];
    public static IReadOnlyList<string> BoardPeriods { get; } = ["This stream", "This week", "This month", "All time"];
    public static IReadOnlyList<string> ResetModes { get; } = ["15 minutes after going offline", "Only by hand"];

    private readonly SessionDataStore _store;
    private bool _loading;

    public StreamDataViewModel(SessionDataStore store) : base(PageTitle)
    {
        _store = store;
        _loading = true;
        ResetMode = store.ResetMode == SessionResetMode.Manual ? ResetModes[1] : ResetModes[0];
        CountTestEvents = store.CountTestEvents;
        _loading = false;
        store.Changed += () => OnUi(Refresh);
        Refresh();
    }

    public ObservableCollection<StreamEventRowViewModel> Rows { get; } = [];
    public ObservableCollection<string> TypeChoices { get; } = [Everything];
    public ObservableCollection<string> PlatformChoices { get; } = [Everything];

    // Filters
    [ObservableProperty] private string _typeFilter = Everything;
    [ObservableProperty] private string _platformFilter = Everything;
    [ObservableProperty] private string _nameFilter = "";
    [ObservableProperty] private string _fromFilter = "";
    [ObservableProperty] private string _toFilter = "";
    [ObservableProperty] private bool _testOnly;

    // The selected event, for editing
    [ObservableProperty] private StreamEventRowViewModel? _selected;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private decimal? _editAmount;
    [ObservableProperty] private bool _editIsTest;

    // Overrides
    [ObservableProperty] private string _overrideType = "";
    [ObservableProperty] private string _overridePeriod = "This stream";
    [ObservableProperty] private decimal? _overrideCount;
    [ObservableProperty] private decimal? _overrideAmount;
    [ObservableProperty] private string _overrideName = "";
    [ObservableProperty] private string _overrideBoardPeriod = "All time";
    [ObservableProperty] private string _currentValues = "";

    // Settings
    [ObservableProperty] private string _resetMode = ResetModes[0];
    [ObservableProperty] private bool _countTestEvents;
    [ObservableProperty] private string _resetText = "";

    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _confirmRemoveTests;
    [ObservableProperty] private bool _confirmRemoveShown;
    [ObservableProperty] private bool _confirmReset;

    public bool HasSelection => Selected is not null;
    public bool IsEmpty => Rows.Count == 0;

    partial void OnTypeFilterChanged(string value) => Refresh();
    partial void OnPlatformFilterChanged(string value) => Refresh();
    partial void OnNameFilterChanged(string value) => Refresh();
    partial void OnFromFilterChanged(string value) => Refresh();
    partial void OnToFilterChanged(string value) => Refresh();
    partial void OnTestOnlyChanged(bool value) => Refresh();

    partial void OnSelectedChanged(StreamEventRowViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        if (value is null) return;
        EditName = value.Event.Name;
        EditAmount = (decimal)value.Event.Amount;
        EditIsTest = value.Event.IsTest;
        OverrideType = value.Event.Type;
    }

    partial void OnOverrideTypeChanged(string value) => ShowCurrentValues();
    partial void OnOverridePeriodChanged(string value) => ShowCurrentValues();
    partial void OnOverrideBoardPeriodChanged(string value) => ShowCurrentValues();

    partial void OnResetModeChanged(string value)
    {
        if (_loading) return;
        _store.SetResetMode(value == ResetModes[1] ? SessionResetMode.Manual : SessionResetMode.AfterOffline);
    }

    partial void OnCountTestEventsChanged(bool value)
    {
        if (!_loading) _store.SetCountTestEvents(value);
    }

    private StreamEventFilter Filter()
    {
        static DateTimeOffset? Date(string text, bool endOfDay)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (!DateTimeOffset.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d)) return null;
            // A date without a time means the whole day.
            return endOfDay && d.TimeOfDay == TimeSpan.Zero && !text.Contains(':') ? d.AddDays(1).AddTicks(-1) : d;
        }
        return new StreamEventFilter(TypeFilter == Everything ? null : TypeFilter, PlatformFilter == Everything ? null : PlatformFilter,
            Date(FromFilter, false), Date(ToFilter, true), TestOnly, NameFilter);
    }

    private bool FilterIsEmpty => TypeFilter == Everything && PlatformFilter == Everything && NameFilter.Trim().Length == 0
                                  && FromFilter.Trim().Length == 0 && ToFilter.Trim().Length == 0 && !TestOnly;

    internal void Refresh()
    {
        foreach (var t in _store.Types())
            if (!TypeChoices.Contains(t)) TypeChoices.Add(t);
        var all = _store.Events(limit: SessionDataStore.EventLogSize);
        foreach (var p in all.Select(e => e.Service).Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase))
            if (!PlatformChoices.Contains(p, StringComparer.OrdinalIgnoreCase)) PlatformChoices.Add(p);

        var selectedId = Selected?.Id;
        var events = _store.Events(Filter(), SessionDataStore.EventLogSize);
        Rows.Clear();
        foreach (var e in events) Rows.Add(new StreamEventRowViewModel(e, _store.IsThisStream(e)));
        Selected = selectedId is null ? null : Rows.FirstOrDefault(r => r.Id == selectedId);

        var info = _store.Info();
        var tests = info["testEvents"]!.GetValue<int>();
        Summary = all.Count == 0
            ? "No stored events yet. Events are kept from now on; totals from before this version can still be changed with the overrides."
            : $"{Rows.Count} of {all.Count} stored event(s) shown" + (tests > 0 ? $", {tests} of them test events." : ".")
              + (all.Count >= SessionDataStore.EventLogSize ? $" Only the last {SessionDataStore.EventLogSize} events are kept." : "");
        var started = _store.SessionStarted is { } s ? s.ToLocalTime().ToString("ddd d MMM HH:mm", CultureInfo.InvariantCulture) : null;
        ResetText = (started is null ? "No stream has been counted yet. " : $"This stream's totals started {started}. ")
                    + (_store.ResetMode == SessionResetMode.Manual
                        ? "They only start again when you reset them."
                        : info["resetsAt"]?.GetValue<string>() is { } at
                            ? $"You are offline: they start again at {DateTimeOffset.Parse(at, CultureInfo.InvariantCulture).ToLocalTime():HH:mm} (15 minutes after going offline), or when you go live after that."
                            : "They start again 15 minutes after your last platform goes offline (or when you go live after that).");
        OnPropertyChanged(nameof(IsEmpty));
        ShowCurrentValues();
    }

    private static string TallyKey(string label) => label switch { "This week" => "week", "This month" => "month", "All time" => "total", _ => "session" };
    private static string BoardKey(string label) => label switch { "This week" => "weekly", "This month" => "monthly", "All time" => "alltime", _ => "session" };

    /// <summary>What the overrides would replace, so the streamer sees the values before changing them.</summary>
    private void ShowCurrentValues()
    {
        var type = OverrideType.Trim();
        if (type.Length == 0)
        {
            CurrentValues = "Pick a type to see its values.";
            return;
        }
        var data = _store.Snapshot();
        var tally = data[$"{type}-{TallyKey(OverridePeriod)}"];
        var latest = data[$"{type}-latest"];
        var parts = new List<string>
        {
            $"{OverridePeriod}: {tally?["count"] ?? 0} event(s), amount {OverlayAlerts.NumberOf(tally?["amount"]).ToString("0.##", CultureInfo.InvariantCulture)}",
            $"Latest: {(latest?["name"]?.ToString() is { Length: > 0 } n ? $"{n} ({OverlayAlerts.NumberOf(latest?["amount"]).ToString("0.##", CultureInfo.InvariantCulture)})" : "none")}",
        };
        if (SessionDataStore.AmountTypes.Contains(type))
        {
            var top = data[$"{type}-{BoardKey(OverrideBoardPeriod)}-top-donation"];
            var donator = data[$"{type}-{BoardKey(OverrideBoardPeriod)}-top-donator"];
            parts.Add($"Top donation ({OverrideBoardPeriod.ToLowerInvariant()}): {Describe(top)}");
            parts.Add($"Top donator ({OverrideBoardPeriod.ToLowerInvariant()}): {Describe(donator)}");
        }
        CurrentValues = string.Join("\n", parts);
    }

    private static string Describe(JsonNode? entry) => entry?["name"]?.ToString() is { Length: > 0 } name
        ? $"{name} ({OverlayAlerts.NumberOf(entry["amount"]).ToString("0.##", CultureInfo.InvariantCulture)})"
        : "none";

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
        Try(() => _store.EditEvent(row.Id, new StreamEventEdit(EditName, EditAmount is { } a ? (double)a : null, EditIsTest)),
            "Saved. The totals were recalculated.");
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (Selected is not { } row) return;
        if (_store.RemoveEvent(row.Id)) Status = $"Removed {row.Name}'s {row.Type}. The totals were recalculated.";
        Selected = null;
    }

    [RelayCommand]
    private void RemoveTestEvents()
    {
        if (!ConfirmRemoveTests)
        {
            ConfirmRemoveTests = true;   // the button asks to be clicked again
            return;
        }
        ConfirmRemoveTests = false;
        var removed = _store.RemoveEvents(new StreamEventFilter(TestOnly: true));
        Status = removed == 0 ? "There are no stored test events." : $"Removed {removed} test event(s). The totals were recalculated.";
    }

    [RelayCommand]
    private void RemoveShown()
    {
        if (FilterIsEmpty)
        {
            Status = "Set a filter first (type, platform, name, dates or test only): this removes the events the list shows.";
            return;
        }
        if (!ConfirmRemoveShown)
        {
            ConfirmRemoveShown = true;
            return;
        }
        ConfirmRemoveShown = false;
        var removed = _store.RemoveEvents(Filter());
        Status = $"Removed {removed} event(s). The totals were recalculated.";
    }

    [RelayCommand]
    private void ResetNow()
    {
        if (!ConfirmReset)
        {
            ConfirmReset = true;
            return;
        }
        ConfirmReset = false;
        _store.ResetSession();
        Status = "This stream's totals start again from zero. Week, month and all-time totals are kept.";
    }

    [RelayCommand]
    private void SetTotal()
    {
        if (OverrideCount is null && OverrideAmount is null)
        {
            Status = "Enter a count, an amount or both.";
            return;
        }
        Try(() => _store.SetTally(OverrideType, TallyKey(OverridePeriod), OverrideCount is { } c ? (long)c : null,
            OverrideAmount is { } a ? (double)a : null), $"{OverridePeriod}'s {OverrideType} total changed.");
    }

    [RelayCommand]
    private void SetLatest() =>
        Try(() => _store.SetLatest(OverrideType, OverrideName, OverrideAmount is { } a ? (double)a : 0),
            OverrideName.Trim().Length == 0 ? $"The latest {OverrideType} was cleared." : $"The latest {OverrideType} was changed.");

    [RelayCommand]
    private void ClearLatest() => Try(() => _store.SetLatest(OverrideType, null), $"The latest {OverrideType} was cleared.");

    [RelayCommand]
    private void SetTopDonation() =>
        Try(() => _store.SetTopDonation(OverrideType, BoardKey(OverrideBoardPeriod), OverrideName, OverrideAmount is { } a ? (double)a : 0),
            $"The top {OverrideType} ({OverrideBoardPeriod.ToLowerInvariant()}) was changed.");

    [RelayCommand]
    private void ClearTopDonation() =>
        Try(() => _store.SetTopDonation(OverrideType, BoardKey(OverrideBoardPeriod), null),
            $"The top {OverrideType} ({OverrideBoardPeriod.ToLowerInvariant()}) was cleared.");

    [RelayCommand]
    private void SetDonator()
    {
        if (OverrideName.Trim().Length == 0)
        {
            Status = "Enter the donator's name (amount 0 takes them off the board).";
            return;
        }
        Try(() => _store.SetDonator(OverrideType, BoardKey(OverrideBoardPeriod), OverrideName, OverrideAmount is { } a ? (double)a : 0),
            $"{OverrideName.Trim()}'s {OverrideType} total ({OverrideBoardPeriod.ToLowerInvariant()}) was changed.");
    }

    /// <summary>Everything as JSON for a backup file.</summary>
    public string Export() => _store.Export().ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    public void Import(string json, string fileName)
    {
        try
        {
            _store.Import(JsonNode.Parse(json) as JsonObject ?? throw new ArgumentException("The file is not a stream totals backup"));
            Status = $"Restored the stream data from {fileName}.";
        }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            Status = "Import failed: " + ex.Message;
        }
    }

    public void ReportExport(string fileName) => Status = $"Saved a backup to {fileName}.";
}
