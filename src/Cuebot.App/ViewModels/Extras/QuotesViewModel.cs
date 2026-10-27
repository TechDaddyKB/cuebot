using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Quotes;

namespace Cuebot.App.ViewModels.Extras;

/// <summary>One quote row; edits write through to the store as they are typed.</summary>
public sealed class QuoteRowViewModel : ObservableObject
{
    private readonly Quote quote;
    private readonly Action<Action> write;
    private readonly QuoteStore store;

    public QuoteRowViewModel(Quote quote, QuoteStore store, Action<Action> write, Action<QuoteRowViewModel> delete)
    {
        this.quote = quote;
        this.store = store;
        this.write = write;
        DeleteCommand = new RelayCommand(() => delete(this));
    }

    public int Id => quote.Id;
    public string IdText => "#" + quote.Id.ToString(CultureInfo.InvariantCulture);
    public string Date => quote.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    public string Platform => quote.Platform;
    public IRelayCommand DeleteCommand { get; }

    public string Text
    {
        get => quote.Text;
        set
        {
            if (value == quote.Text || string.IsNullOrWhiteSpace(value)) return;
            quote.Text = value;
            write(() => store.Update(quote.Id, text: quote.Text));
            OnPropertyChanged();
        }
    }

    public string User
    {
        get => quote.User;
        set
        {
            if (value == quote.User) return;
            quote.User = value;
            write(() => store.Update(quote.Id, user: quote.User));
            OnPropertyChanged();
        }
    }

    public string Game
    {
        get => quote.Game;
        set
        {
            if (value == quote.Game) return;
            quote.Game = value;
            write(() => store.Update(quote.Id, game: quote.Game));
            OnPropertyChanged();
        }
    }
}

/// <summary>Browse, search, add, edit, delete, re-index, import and export quotes.</summary>
public sealed partial class QuotesViewModel : PageViewModel
{
    private readonly QuoteStore _store;
    private bool _editing;

    public QuotesViewModel(QuoteStore store) : base("Quotes")
    {
        _store = store;
        store.Changed += () => OnUi(() => { if (!_editing) Refresh(); });
        Refresh();
    }

    public ObservableCollection<QuoteRowViewModel> Quotes { get; } = [];

    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _newText = "";
    [ObservableProperty] private string _newUser = "";
    [ObservableProperty] private string _newGame = "";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _replaceOnImport;

    public string Summary => (_store.Count == 1 ? "1 quote" : $"{_store.Count} quotes") + (Search.Trim().Length > 0 ? $", {Quotes.Count} shown" : "");

    public string CurrentGameHint => _store.CurrentGame.Length > 0 ? $"Current category: {_store.CurrentGame}" : "Game / category (optional)";

    partial void OnSearchChanged(string value) => Refresh();

    internal void Refresh()
    {
        Quotes.Clear();
        foreach (var quote in _store.Search(Search)) Quotes.Add(Row(quote));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(CurrentGameHint));
    }

    private QuoteRowViewModel Row(Quote quote)
    {
        return new QuoteRowViewModel(quote, _store, Quietly, DeleteQuote);
    }

    private void Quietly(Action change)
    {
        _editing = true;
        try { change(); }
        finally { _editing = false; }
    }

    [RelayCommand]
    private void AddQuote()
    {
        if (NewText.Trim().Length == 0)
        {
            Status = "Type the quote first.";
            return;
        }
        var quote = _store.Add(NewText, NewUser, platform: "", game: NewGame.Trim().Length > 0 ? NewGame : null);
        NewText = NewUser = NewGame = "";
        Status = $"Added quote #{quote.Id}.";
        Refresh();
    }

    [RelayCommand]
    private void DeleteQuote(QuoteRowViewModel row)
    {
        Quietly(() => _store.Delete(row.Id));
        Quotes.Remove(row);
        Status = $"Deleted quote {row.IdText}.";
        OnPropertyChanged(nameof(Summary));
    }

    [RelayCommand]
    private void Reindex()
    {
        var changed = _store.Reindex();
        Status = changed == 0 ? "Ids were already 1..n." : $"Renumbered {changed} quote(s).";
        Refresh();
    }

    public string ExportJson() => QuoteTransfer.ToJson(_store.All());
    public string ExportCsv() => QuoteTransfer.ToCsv(_store.All());

    /// <summary>Imports JSON or CSV text (chosen by <paramref name="fileName"/>'s extension, else by content).</summary>
    public int Import(string content, string fileName, bool replace)
    {
        try
        {
            var csv = fileName.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                      || (!fileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase) && content.TrimStart() is var t && !t.StartsWith('[') && !t.StartsWith('{'));
            var quotes = csv ? QuoteTransfer.FromCsv(content) : QuoteTransfer.FromJson(content);
            var count = _store.Import(quotes, replace);
            Status = $"Imported {count} quote(s){(replace ? ", replacing the old ones" : "")}.";
            Refresh();
            return count;
        }
        catch (FormatException ex)
        {
            Status = "Import failed: " + ex.Message;
            return 0;
        }
    }

    public void ReportExport(string path) => Status = $"Exported {_store.Count} quote(s) to {Path.GetFileName(path)}.";
}
