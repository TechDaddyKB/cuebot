using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Speech;

namespace Cuebot.App.ViewModels.Speech;

/// <summary>One chatter's speech settings, written straight to the user directory.</summary>
public sealed partial class SpeechUserViewModel : ObservableObject
{
    private readonly SpeechPageContext _ctx;

    public SpeechUserViewModel(SpeechUser user, SpeechPageContext ctx, IReadOnlyList<Choice> aliasChoices)
    {
        User = user;
        _ctx = ctx;
        AliasChoices = aliasChoices;
    }

    public SpeechUser User { get; }

    public string Title => User.UserName.Length > 0 ? User.UserName : User.UserId;

    public string Subtitle => QueueItemViewModel.Join(User.Platform, User.Nickname.Length > 0 ? $"“{User.Nickname}”" : "",
        User.Alias, Flags);

    public string Flags => QueueItemViewModel.Join(User.Ignored ? "ignored" : "", User.OptedOut ? "opted out" : "",
        User.Forced ? "forced" : "", User.Regular ? "regular" : "");

    public string Details => QueueItemViewModel.Join(
        User.UserId.Length > 0 ? $"id {User.UserId}" : "",
        User.LastSeen > DateTimeOffset.MinValue ? $"last seen {User.LastSeen.ToLocalTime():g}" : "",
        User.LastVoice.Length > 0 ? $"last voice {User.LastVoice}" : "");

    public IReadOnlyList<Choice> AliasChoices { get; private set; }

    public Choice? Alias
    {
        get => SpeechPageContext.Find(AliasChoices, User.Alias);
        set
        {
            if (value is null || value.Value == User.Alias) return;
            Change(u => u.Alias = value.Value);
        }
    }

    public string Nickname
    {
        get => User.Nickname;
        set
        {
            var nick = (value ?? "").Trim();
            if (nick != User.Nickname) Change(u => u.Nickname = nick);
        }
    }

    /// <summary>Empty keeps the alias rate.</summary>
    public decimal? Rate
    {
        get => User.Rate;
        set
        {
            int? rate = value is null ? null : SpeechText.Clamp(value, -10, 10);
            if (rate != User.Rate) Change(u => u.Rate = rate);
        }
    }

    public decimal? Pitch
    {
        get => User.Pitch;
        set
        {
            int? pitch = value is null ? null : SpeechText.Clamp(value, -10, 10);
            if (pitch != User.Pitch) Change(u => u.Pitch = pitch);
        }
    }

    public bool Ignored { get => User.Ignored; set => SetFlag(User.Ignored, value, (u, v) => u.Ignored = v); }
    public bool Forced { get => User.Forced; set => SetFlag(User.Forced, value, (u, v) => u.Forced = v); }
    public bool Regular { get => User.Regular; set => SetFlag(User.Regular, value, (u, v) => u.Regular = v); }
    public bool OptedOut { get => User.OptedOut; set => SetFlag(User.OptedOut, value, (u, v) => u.OptedOut = v); }

    public string RandomVoice => User.RandomVoice.Length > 0 ? User.RandomVoice : "none";

    public bool HasRandomVoice => User.RandomVoice.Length > 0;

    [RelayCommand]
    private void ResetRandomVoice() => Change(u => u.RandomVoice = "");

    public bool Matches(string search) =>
        search.Length == 0
        || User.UserName.Contains(search, StringComparison.OrdinalIgnoreCase)
        || User.Nickname.Contains(search, StringComparison.OrdinalIgnoreCase)
        || User.UserId.Contains(search, StringComparison.OrdinalIgnoreCase)
        || User.Platform.Contains(search, StringComparison.OrdinalIgnoreCase)
        || User.Alias.Contains(search, StringComparison.OrdinalIgnoreCase);

    public void RefreshChoices(IReadOnlyList<Choice> aliasChoices)
    {
        AliasChoices = aliasChoices;
        OnPropertyChanged(nameof(AliasChoices));
        OnPropertyChanged(nameof(Alias));
    }

    public override string ToString() => Title;

    private void SetFlag(bool current, bool value, Action<SpeechUser, bool> set)
    {
        if (current != value) Change(u => set(u, value));
    }

    private void Change(Action<SpeechUser> change)
    {
        change(User);
        _ctx.Speech.Users.Save();
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>Users tab: everyone the speech module remembers, with search and per-user overrides.</summary>
public sealed partial class SpeechUsersViewModel : ObservableObject
{
    private readonly SpeechPageContext _ctx;
    private IReadOnlyList<SpeechUserViewModel> _all = [];
    private IReadOnlyList<Choice> _aliasChoices;

    public SpeechUsersViewModel(SpeechPageContext ctx)
    {
        _ctx = ctx;
        _aliasChoices = ctx.AliasChoices("Source alias");
        ctx.ChoicesChanged += () =>
        {
            _aliasChoices = ctx.AliasChoices("Source alias");
            foreach (var user in _all) user.RefreshChoices(_aliasChoices);
        };
        Refresh();
    }

    public IReadOnlyList<SpeechUserViewModel> Users { get; private set; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(RemoveCommand))]
    private SpeechUserViewModel? _selected;

    public bool HasSelection => Selected is not null;

    [ObservableProperty] private string _search = "";

    public string CountText => _all.Count == Users.Count ? $"{_all.Count} users" : $"{Users.Count} of {_all.Count} users";

    public bool IsEmpty => _all.Count == 0;

    partial void OnSearchChanged(string value) => ApplyFilter();

    /// <summary>Re-reads the directory (it changes as chatters speak and moderators use !tts).</summary>
    [RelayCommand]
    public void Refresh()
    {
        var selected = Selected?.User;
        _all = _ctx.Speech.Users.All
            .OrderByDescending(u => u.LastSeen)
            .ThenBy(u => u.UserName, StringComparer.OrdinalIgnoreCase)
            .Select(u => new SpeechUserViewModel(u, _ctx, _aliasChoices))
            .ToArray();
        ApplyFilter();
        Selected = Users.FirstOrDefault(u => ReferenceEquals(u.User, selected)) ?? Users.FirstOrDefault();
        OnPropertyChanged(nameof(IsEmpty));
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private void Remove()
    {
        if (Selected is not { } user) return;
        _ctx.Speech.Users.Remove(user.User);
        Selected = null;
        Refresh();
    }

    private void ApplyFilter()
    {
        var search = Search.Trim();
        Users = _all.Where(u => u.Matches(search)).ToArray();
        OnPropertyChanged(nameof(Users));
        OnPropertyChanged(nameof(CountText));
    }
}
