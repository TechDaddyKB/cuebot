using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Giveaways;
using Cuebot.Core.Store;

namespace Cuebot.App.ViewModels.Extras;

/// <summary>One store item; edits write through to the store as they are made (a refused edit shows why and reverts).</summary>
public sealed class StoreItemRowViewModel : ObservableObject
{
    private readonly PointsStore _store;
    private readonly Action<string> _status;
    private StoreItem _item;

    public StoreItemRowViewModel(StoreItem item, PointsStore store, Action<string> status)
    {
        _item = item;
        _store = store;
        _status = status;
        DeleteCommand = new RelayCommand(() => store.Remove(_item.Id));
    }

    public string Id => _item.Id;
    public IRelayCommand DeleteCommand { get; }

    private void Set(Action<StoreItem> edit, string property)
    {
        try
        {
            _store.Update(_item.Id, edit);
            _item = _store.Find(_item.Id) ?? _item;
        }
        catch (ArgumentException ex)
        {
            _status(ex.Message);
        }
        OnPropertyChanged(property);
    }

    public string Name { get => _item.Name; set => Set(i => i.Name = value, nameof(Name)); }
    public string Description { get => _item.Description; set => Set(i => i.Description = value, nameof(Description)); }
    public decimal Cost { get => _item.Cost; set => Set(i => i.Cost = (long)value, nameof(Cost)); }

    /// <summary>-1 is unlimited.</summary>
    public decimal Stock { get => _item.Stock; set => Set(i => i.Stock = (int)value, nameof(Stock)); }
    public decimal UserCooldown { get => _item.UserCooldownSeconds; set => Set(i => i.UserCooldownSeconds = (int)value, nameof(UserCooldown)); }
    public decimal GlobalCooldown { get => _item.GlobalCooldownSeconds; set => Set(i => i.GlobalCooldownSeconds = (int)value, nameof(GlobalCooldown)); }
    public bool Enabled { get => _item.Enabled; set => Set(i => i.Enabled = value, nameof(Enabled)); }
    public bool NeedsApproval { get => _item.NeedsApproval; set => Set(i => i.NeedsApproval = value, nameof(NeedsApproval)); }
    public bool SubscribersOnly { get => _item.SubscribersOnly; set => Set(i => i.SubscribersOnly = value, nameof(SubscribersOnly)); }
}

/// <summary>A redemption waiting for approval.</summary>
public sealed class PendingRedemptionViewModel(StoreRedemption redemption, PointsStore store)
{
    public string Text { get; } = $"{redemption.UserName} ({redemption.Platform}) wants {redemption.ItemName}" +
                                  (redemption.Input.Length > 0 ? $": {redemption.Input}" : "");
    public string When { get; } = redemption.Time.ToLocalTime().ToString("HH:mm", CultureInfo.CurrentCulture);
    public IRelayCommand ApproveCommand { get; } = new RelayCommand(() => store.Approve(redemption.Id));
    public IRelayCommand RejectCommand { get; } = new RelayCommand(() => store.Reject(redemption.Id));
}

/// <summary>
/// Giveaways (open with a chat command, draw, redraw, cancel with refunds) and the points store (items, approvals and
/// the latest redemptions). Both work in every platform's chat with Cuebot points.
/// </summary>
public sealed partial class GiveawaysStoreViewModel : PageViewModel
{
    public const string PageTitle = "Giveaways & Store";

    private readonly Giveaways _giveaways;
    private readonly PointsStore _store;

    [ObservableProperty] private string _prize = "";
    [ObservableProperty] private string _joinCommand = "!join";
    [ObservableProperty] private decimal _ticketCost;
    [ObservableProperty] private decimal _maxTickets = 1;
    [ObservableProperty] private decimal _subscriberLuck = 1;
    [ObservableProperty] private bool _subscribersOnly;
    [ObservableProperty] private decimal _seconds;
    [ObservableProperty] private decimal _winnerCount = 1;
    [ObservableProperty] private string _newItemName = "";
    [ObservableProperty] private decimal _newItemCost = 100;
    [ObservableProperty] private string _status = "";

    public GiveawaysStoreViewModel(Giveaways giveaways, PointsStore store) : base(PageTitle)
    {
        _giveaways = giveaways;
        _store = store;
        giveaways.Changed += () => OnUi(RefreshGiveaway);
        store.Changed += () => OnUi(RefreshStore);
        RefreshGiveaway();
        RefreshStore();
    }

    // ---- Giveaway ---------------------------------------------------------------------------------------------------

    public bool GiveawayRunning => _giveaways.Running;
    public bool CanOpen => !_giveaways.Running;
    public bool CanClose => _giveaways.Status == Giveaways.OpenStatus;
    public bool CanRedraw => _giveaways.Status == Giveaways.DrawnStatus;

    public bool AnnounceWinners
    {
        get => _giveaways.AnnounceWinners;
        set => _giveaways.AnnounceWinners = value;
    }

    /// <summary>"Open: 12 entries, 30 tickets", "Drawn: Ann won"...</summary>
    public string GiveawaySummary
    {
        get
        {
            var entrants = _giveaways.Entrants;
            var winners = _giveaways.Winners;
            var tickets = entrants.Sum(e => e.Tickets);
            var counts = $"{entrants.Count} {(entrants.Count == 1 ? "entry" : "entries")}, {tickets} {(tickets == 1 ? "ticket" : "tickets")}";
            return _giveaways.Status switch
            {
                Giveaways.OpenStatus => "Taking entries: " + counts,
                Giveaways.ClosedStatus => "Entries closed: " + counts + ". Draw the winners when you are ready.",
                Giveaways.DrawnStatus => winners.Count > 0 ? $"Won by {string.Join(", ", winners.Select(w => $"{w.UserName} ({w.Platform})"))} ({counts})" : "Nobody entered.",
                Giveaways.CanceledStatus => "Cancelled; tickets were refunded.",
                _ => "No giveaway yet.",
            };
        }
    }

    private void RefreshGiveaway()
    {
        foreach (var name in (string[])[nameof(GiveawayRunning), nameof(CanOpen), nameof(CanClose), nameof(CanRedraw), nameof(GiveawaySummary)])
            OnPropertyChanged(name);
    }

    private void Try(Func<object?> run, string done)
    {
        try
        {
            Status = run() is null ? "There was nothing to do." : done;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            Status = ex.Message;
        }
        RefreshGiveaway();
    }

    [RelayCommand]
    private void OpenGiveaway() => Try(() => _giveaways.Open(new GiveawayOptions
    {
        Title = Prize, Command = JoinCommand, TicketCost = (long)TicketCost, MaxTickets = (int)MaxTickets, SubscriberLuck = (double)SubscriberLuck,
        SubscribersOnly = SubscribersOnly, Seconds = (int)Seconds, Winners = (int)WinnerCount,
    }), $"Giveaway open: viewers type {JoinCommand.Trim()} to enter.");

    [RelayCommand]
    private void CloseGiveaway() => Try(() => _giveaways.Close(), "Entries closed.");

    [RelayCommand]
    private void DrawGiveaway() => Try(() => _giveaways.Draw(), "Winners drawn.");

    [RelayCommand]
    private void RedrawGiveaway() => Try(() => _giveaways.Draw(oneMore: true), "Drew one more winner.");

    [RelayCommand]
    private void CancelGiveaway() => Try(() => _giveaways.Cancel(), "Giveaway cancelled; tickets were refunded.");

    // ---- Store ------------------------------------------------------------------------------------------------------

    public ObservableCollection<StoreItemRowViewModel> Items { get; } = [];
    public ObservableCollection<PendingRedemptionViewModel> Pending { get; } = [];
    public ObservableCollection<string> Recent { get; } = [];

    public bool HasPending => Pending.Count > 0;

    public string RedeemCommand
    {
        get => _store.Command;
        set => _store.Command = value;
    }

    private void RefreshStore()
    {
        var items = _store.Items;
        if (!items.Select(i => i.Id).SequenceEqual(Items.Select(r => r.Id)))
        {
            Items.Clear();
            foreach (var item in items) Items.Add(new StoreItemRowViewModel(item, _store, s => Status = s));
        }
        Pending.Clear();
        foreach (var r in _store.Pending) Pending.Add(new PendingRedemptionViewModel(r, _store));
        Recent.Clear();
        foreach (var r in _store.History.Take(15))
            Recent.Add($"{r.Time.ToLocalTime():HH:mm}  {r.UserName} ({r.Platform}): {r.ItemName}{(r.Input.Length > 0 ? " — " + r.Input : "")}" +
                       (r.Status == StoreRedemption.Rejected ? " (rejected, refunded)" : ""));
        OnPropertyChanged(nameof(HasPending));
        OnPropertyChanged(nameof(RedeemCommand));
    }

    [RelayCommand]
    private void AddItem()
    {
        try
        {
            _store.Add(new StoreItem { Name = NewItemName, Cost = (long)NewItemCost });
            Status = $"Added \"{NewItemName.Trim()}\": viewers type {_store.Command} {NewItemName.Trim()}.";
            NewItemName = "";
        }
        catch (ArgumentException ex)
        {
            Status = ex.Message;
        }
    }
}
