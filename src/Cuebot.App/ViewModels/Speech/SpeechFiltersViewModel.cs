using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Speech;

namespace Cuebot.App.ViewModels.Speech;

/// <summary>Editor of one filter profile. Legacy speak-chat switches show through where the profile has no value yet.</summary>
public sealed partial class FilterProfileViewModel : ObservableObject
{
    private readonly SpeechPageContext _ctx;

    public FilterProfileViewModel(FilterProfile profile, bool isDefault, SpeechPageContext ctx)
    {
        Profile = profile;
        IsDefault = isDefault;
        _ctx = ctx;
    }

    public FilterProfile Profile { get; }
    public bool IsDefault { get; }
    public bool CanRename => !IsDefault;
    private SpeakChatSettings Legacy => _ctx.Settings.SpeakChat;

    public string Name
    {
        get => IsDefault ? FilterProfile.DefaultName : Profile.Name;
        set
        {
            var name = (value ?? "").Trim();
            if (IsDefault || name == Profile.Name) return;
            var old = Profile.Name;
            Profile.Name = name;
            _ctx.RenameFilterProfileReferences(old, name);
            Save();
            OnPropertyChanged();
            _ctx.NotifyChoicesChanged();
        }
    }

    public IReadOnlyList<BadWordAction> BadWordActions { get; } = Enum.GetValues<BadWordAction>();
    public IReadOnlyList<UrlMode> UrlModes { get; } = Enum.GetValues<UrlMode>();

    public string BadWords
    {
        get => SpeechText.JoinLines(Profile.BadWords);
        set => SetList(Profile.BadWords, SpeechText.SplitList(value), v => Profile.BadWords = v);
    }

    public BadWordAction BadWordAction { get => Profile.BadWordAction; set => Set(Profile.BadWordAction, value, v => Profile.BadWordAction = v); }
    public string BadWordReplacement { get => Profile.BadWordReplacement; set => Set(Profile.BadWordReplacement, value ?? "", v => Profile.BadWordReplacement = v); }
    public bool IsReplacingBadWords => Profile.BadWordAction == BadWordAction.Replace;

    public UrlMode UrlMode
    {
        get => Profile.UrlMode ?? (Legacy.SkipLinks ? UrlMode.Replace : UrlMode.Keep);
        set
        {
            if (Profile.UrlMode == value) return;
            Profile.UrlMode = value;
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsReplacingUrls));
        }
    }

    public bool IsReplacingUrls => UrlMode == UrlMode.Replace;
    public string UrlWord { get => Profile.UrlWord; set => Set(Profile.UrlWord, value ?? "", v => Profile.UrlWord = v); }

    public decimal MaxWords { get => Profile.MaxWords; set => Set(Profile.MaxWords, SpeechText.Clamp(value, 0, 10000), v => Profile.MaxWords = v); }

    /// <summary>0 falls back to the legacy speak-chat length limit.</summary>
    public decimal MaxCharacters { get => Profile.MaxCharacters; set => Set(Profile.MaxCharacters, SpeechText.Clamp(value, 0, 100000), v => Profile.MaxCharacters = v); }

    public string MaxCharactersHint => Legacy.MaxLength > 0 ? $"0 uses the legacy limit of {Legacy.MaxLength}." : "0 is unlimited.";

    public decimal MaxRepeatedCharacters { get => Profile.MaxRepeatedCharacters; set => Set(Profile.MaxRepeatedCharacters, SpeechText.Clamp(value, 0, 100), v => Profile.MaxRepeatedCharacters = v); }
    public decimal MaxRepeatedWords { get => Profile.MaxRepeatedWords; set => Set(Profile.MaxRepeatedWords, SpeechText.Clamp(value, 0, 100), v => Profile.MaxRepeatedWords = v); }
    public bool StripEmotes { get => Profile.StripEmotes; set => Set(Profile.StripEmotes, value, v => Profile.StripEmotes = v); }
    public bool AllowFirstEmote { get => Profile.AllowFirstEmote; set => Set(Profile.AllowFirstEmote, value, v => Profile.AllowFirstEmote = v); }

    public string AllowedEmotes
    {
        get => SpeechText.JoinList(Profile.AllowedEmotes);
        set => SetList(Profile.AllowedEmotes, SpeechText.SplitList(value), v => Profile.AllowedEmotes = v);
    }

    public bool StripEmoji { get => Profile.StripEmoji; set => Set(Profile.StripEmoji, value, v => Profile.StripEmoji = v); }

    public string IgnoredPrefixes
    {
        get => SpeechText.JoinList(Profile.IgnoredPrefixes ?? (Legacy.IgnoreCommands ? ["!"] : []));
        set
        {
            var list = SpeechText.SplitList(value);
            if (Profile.IgnoredPrefixes is { } current && current.SequenceEqual(list)) return;
            Profile.IgnoredPrefixes = list;
            Save();
        }
    }

    public string IgnoredUsers
    {
        get => SpeechText.JoinList(Profile.IgnoredUsers);
        set => SetList(Profile.IgnoredUsers, SpeechText.SplitList(value), v => Profile.IgnoredUsers = v);
    }

    public bool IgnoreBots { get => Profile.IgnoreBots; set => Set(Profile.IgnoreBots, value, v => Profile.IgnoreBots = v); }

    public string BotNames
    {
        get => SpeechText.JoinList(Profile.BotNames);
        set => SetList(Profile.BotNames, SpeechText.SplitList(value), v => Profile.BotNames = v);
    }

    public string KnownBots { get; } = string.Join(", ", FilterProfile.KnownBots);

    public bool IgnoreSharedChat { get => Profile.IgnoreSharedChat; set => Set(Profile.IgnoreSharedChat, value, v => Profile.IgnoreSharedChat = v); }

    public override string ToString() => Name;

    private void Set<T>(T current, T value, Action<T> set, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        set(value);
        Save();
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(IsReplacingBadWords));
    }

    private void SetList(List<string> current, List<string> value, Action<List<string>> set)
    {
        if (current.SequenceEqual(value)) return;
        set(value);
        Save();
    }

    private void Save() => _ctx.Save();
}

/// <summary>A word or regex replacement applied to every spoken text.</summary>
public sealed partial class ReplacementViewModel : ObservableObject
{
    private readonly SpeechPageContext _ctx;

    public ReplacementViewModel(WordReplacement replacement, SpeechPageContext ctx)
    {
        Replacement = replacement;
        _ctx = ctx;
    }

    public WordReplacement Replacement { get; }

    public string Find { get => Replacement.Find; set => Set(Replacement.Find, value ?? "", v => Replacement.Find = v); }
    public string Replace { get => Replacement.Replace; set => Set(Replacement.Replace, value ?? "", v => Replacement.Replace = v); }
    public bool IsRegex { get => Replacement.IsRegex; set => Set(Replacement.IsRegex, value, v => Replacement.IsRegex = v); }

    /// <summary>Why the regular expression is ignored, or empty.</summary>
    public string Error
    {
        get
        {
            if (!Replacement.IsRegex || Replacement.Find.Length == 0) return "";
            try
            {
                _ = new Regex(Replacement.Find, RegexOptions.None, TimeSpan.FromMilliseconds(100));
                return "";
            }
            catch (ArgumentException ex)
            {
                return $"Invalid regular expression: {ex.Message}";
            }
        }
    }

    public bool HasError => Error.Length > 0;

    private void Set<T>(T current, T value, Action<T> set, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        set(value);
        _ctx.Save();
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(Error));
        OnPropertyChanged(nameof(HasError));
    }
}

/// <summary>Voices and locales left out of random voice picks.</summary>
public sealed partial class VoiceIgnoreProfileViewModel : ObservableObject
{
    private readonly SpeechPageContext _ctx;
    private readonly Action _renamed;

    public VoiceIgnoreProfileViewModel(VoiceIgnoreProfile profile, SpeechPageContext ctx, Action renamed)
    {
        Profile = profile;
        _ctx = ctx;
        _renamed = renamed;
    }

    public VoiceIgnoreProfile Profile { get; }

    public string Name
    {
        get => Profile.Name;
        set
        {
            var name = (value ?? "").Trim();
            if (name == Profile.Name) return;
            if (string.Equals(_ctx.Settings.ActiveVoiceIgnoreProfile, Profile.Name, StringComparison.OrdinalIgnoreCase))
                _ctx.Settings.ActiveVoiceIgnoreProfile = name;
            Profile.Name = name;
            _ctx.Save();
            OnPropertyChanged();
            _renamed();
        }
    }

    /// <summary>One per line: "engineId|voiceId" or a bare voice id.</summary>
    public string Voices
    {
        get => SpeechText.JoinLines(Profile.Voices);
        set
        {
            var list = SpeechText.SplitLines(value);
            if (list.SequenceEqual(Profile.Voices)) return;
            Profile.Voices = list;
            _ctx.Save();
        }
    }

    public string Locales
    {
        get => SpeechText.JoinList(Profile.Locales);
        set
        {
            var list = SpeechText.SplitList(value);
            if (list.SequenceEqual(Profile.Locales)) return;
            Profile.Locales = list;
            _ctx.Save();
        }
    }

    public override string ToString() => Name;
}

/// <summary>Filters tab: filter profiles, which source uses which, replacements and voice ignore profiles.</summary>
public sealed partial class SpeechFiltersViewModel : ObservableObject
{
    private readonly SpeechPageContext _ctx;

    public SpeechFiltersViewModel(SpeechPageContext ctx)
    {
        _ctx = ctx;
        Profiles = new ObservableCollection<FilterProfileViewModel>(
            [new FilterProfileViewModel(ctx.Settings.Filters, true, ctx), .. ctx.Settings.FilterProfiles.Select(p => new FilterProfileViewModel(p, false, ctx))]);
        _selectedProfile = Profiles[0];
        Replacements = new ObservableCollection<ReplacementViewModel>(ctx.Settings.Replacements.Select(r => new ReplacementViewModel(r, ctx)));
        IgnoreProfiles = new ObservableCollection<VoiceIgnoreProfileViewModel>(
            ctx.Settings.VoiceIgnoreProfiles.Select(p => new VoiceIgnoreProfileViewModel(p, ctx, RefreshIgnoreChoices)));
        _selectedIgnoreProfile = IgnoreProfiles.FirstOrDefault();
        FilterProfileChoices = ctx.FilterProfileChoices();
        IgnoreChoices = BuildIgnoreChoices();
        ctx.ChoicesChanged += () =>
        {
            FilterProfileChoices = ctx.FilterProfileChoices();
            OnPropertyChanged(nameof(FilterProfileChoices));
            OnPropertyChanged(nameof(ChatProfile));
            OnPropertyChanged(nameof(EventsProfile));
        };
    }

    public ObservableCollection<FilterProfileViewModel> Profiles { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RemoveProfileCommand))]
    private FilterProfileViewModel? _selectedProfile;

    public IReadOnlyList<Choice> FilterProfileChoices { get; private set; }

    public Choice? ChatProfile
    {
        get => SpeechPageContext.Find(FilterProfileChoices, _ctx.Settings.SpeakChat.FilterProfile);
        set
        {
            if (value is null || value.Value == _ctx.Settings.SpeakChat.FilterProfile) return;
            _ctx.Settings.SpeakChat.FilterProfile = value.Value;
            _ctx.Save();
            OnPropertyChanged();
        }
    }

    public Choice? EventsProfile
    {
        get => SpeechPageContext.Find(FilterProfileChoices, _ctx.Settings.Events.FilterProfile);
        set
        {
            if (value is null || value.Value == _ctx.Settings.Events.FilterProfile) return;
            _ctx.Settings.Events.FilterProfile = value.Value;
            _ctx.Save();
            OnPropertyChanged();
        }
    }

    public ObservableCollection<ReplacementViewModel> Replacements { get; }

    public ObservableCollection<VoiceIgnoreProfileViewModel> IgnoreProfiles { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasIgnoreProfile))]
    [NotifyCanExecuteChangedFor(nameof(RemoveIgnoreProfileCommand))]
    private VoiceIgnoreProfileViewModel? _selectedIgnoreProfile;

    public bool HasIgnoreProfile => SelectedIgnoreProfile is not null;

    public IReadOnlyList<Choice> IgnoreChoices { get; private set; }

    public Choice? ActiveIgnoreProfile
    {
        get => SpeechPageContext.Find(IgnoreChoices, _ctx.Settings.ActiveVoiceIgnoreProfile);
        set
        {
            if (value is null || value.Value == _ctx.Settings.ActiveVoiceIgnoreProfile) return;
            _ctx.Speech.ActivateVoiceIgnoreProfile(value.Value);
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void AddProfile()
    {
        var taken = Profiles.Select(p => p.Name);
        var profile = new FilterProfile { Name = SpeechText.UniqueName("Profile", taken) };
        _ctx.Settings.FilterProfiles.Add(profile);
        _ctx.Save();
        var vm = new FilterProfileViewModel(profile, false, _ctx);
        Profiles.Add(vm);
        SelectedProfile = vm;
        _ctx.NotifyChoicesChanged();
    }

    private bool CanRemoveProfile() => SelectedProfile is { IsDefault: false };

    [RelayCommand(CanExecute = nameof(CanRemoveProfile))]
    private void RemoveProfile()
    {
        if (SelectedProfile is not { IsDefault: false } profile) return;
        _ctx.Settings.FilterProfiles.Remove(profile.Profile);
        _ctx.RenameFilterProfileReferences(profile.Profile.Name, ""); // sources fall back to the default profile
        _ctx.Save();
        Profiles.Remove(profile);
        SelectedProfile = Profiles[0];
        _ctx.NotifyChoicesChanged();
    }

    [RelayCommand]
    private void AddReplacement()
    {
        var replacement = new WordReplacement();
        _ctx.Settings.Replacements.Add(replacement);
        _ctx.Save();
        Replacements.Add(new ReplacementViewModel(replacement, _ctx));
    }

    [RelayCommand]
    private void RemoveReplacement(ReplacementViewModel? replacement)
    {
        if (replacement is null) return;
        _ctx.Settings.Replacements.Remove(replacement.Replacement);
        _ctx.Save();
        Replacements.Remove(replacement);
    }

    [RelayCommand]
    private void AddIgnoreProfile()
    {
        var profile = new VoiceIgnoreProfile { Name = SpeechText.UniqueName("Ignore", IgnoreProfiles.Select(p => p.Name)) };
        _ctx.Settings.VoiceIgnoreProfiles.Add(profile);
        _ctx.Save();
        var vm = new VoiceIgnoreProfileViewModel(profile, _ctx, RefreshIgnoreChoices);
        IgnoreProfiles.Add(vm);
        SelectedIgnoreProfile = vm;
        RefreshIgnoreChoices();
    }

    [RelayCommand(CanExecute = nameof(HasIgnoreProfile))]
    private void RemoveIgnoreProfile()
    {
        if (SelectedIgnoreProfile is not { } profile) return;
        if (string.Equals(_ctx.Settings.ActiveVoiceIgnoreProfile, profile.Name, StringComparison.OrdinalIgnoreCase))
            _ctx.Speech.ActivateVoiceIgnoreProfile(null);
        _ctx.Settings.VoiceIgnoreProfiles.Remove(profile.Profile);
        _ctx.Save();
        IgnoreProfiles.Remove(profile);
        SelectedIgnoreProfile = IgnoreProfiles.FirstOrDefault();
        RefreshIgnoreChoices();
    }

    private IReadOnlyList<Choice> BuildIgnoreChoices() =>
    [
        new Choice("", "None (every voice)"),
        .. _ctx.Settings.VoiceIgnoreProfiles.Where(p => p.Name.Length > 0).Select(p => new Choice(p.Name, p.Name)),
    ];

    private void RefreshIgnoreChoices()
    {
        IgnoreChoices = BuildIgnoreChoices();
        OnPropertyChanged(nameof(IgnoreChoices));
        OnPropertyChanged(nameof(ActiveIgnoreProfile));
    }
}
