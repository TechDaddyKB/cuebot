using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core.Chat.Emotes;
using Cuebot.Core.Storage;

namespace Cuebot.App.ViewModels.Chat;

/// <summary>One editable quick action row.</summary>
public sealed partial class QuickActionEditViewModel : ObservableObject
{
    private readonly Action _save;
    private readonly Action<QuickActionEditViewModel> _remove;

    public QuickActionEditViewModel(QuickActionDefinition definition, Action save, Func<IEnumerable<string>> actionNames,
        Action<QuickActionEditViewModel> remove)
    {
        Definition = definition;
        _save = save;
        _remove = remove;
        ActionNames = actionNames().ToArray();
        _label = definition.Label;
        _actionName = definition.ActionName;
        _scope = definition.Scope;
    }

    public QuickActionDefinition Definition { get; }
    public IReadOnlyList<string> ActionNames { get; }
    public static IReadOnlyList<QuickActionScope> Scopes { get; } = Enum.GetValues<QuickActionScope>();

    [ObservableProperty] private string _label;
    [ObservableProperty] private string _actionName;
    [ObservableProperty] private QuickActionScope _scope;

    partial void OnLabelChanged(string value) { Definition.Label = value; _save(); }
    partial void OnActionNameChanged(string value) { Definition.ActionName = value ?? ""; _save(); }
    partial void OnScopeChanged(QuickActionScope value) { Definition.Scope = value; _save(); }

    [RelayCommand]
    private void Remove() => _remove(this);
}

/// <summary>The chat page's options panel: display toggles, highlight rules, quick actions and emote sources.</summary>
public sealed partial class ChatOptionsViewModel : ObservableObject
{
    private readonly ChatPageSettings _settings;
    private readonly Action _persist;
    private readonly EmoteService? _emotes;
    private readonly JsonFile<EmoteSettings>? _emoteSettings;
    private readonly bool _loading = true;

    public ChatOptionsViewModel(ChatPageSettings settings, Action persist, EmoteService? emotes,
        JsonFile<EmoteSettings>? emoteSettings, Func<IEnumerable<string>> actionNames)
    {
        _settings = settings;
        _persist = persist;
        _emotes = emotes;
        _emoteSettings = emoteSettings;
        ActionNames = actionNames;
        ShowEmotes = settings.ShowEmotes;
        ShowPronouns = settings.ShowPronouns;
        HighlightMentions = settings.Highlights.Mentions;
        HighlightFirstTime = settings.Highlights.FirstTimeChatters;
        MentionNames = string.Join(", ", settings.Highlights.MentionNames);
        Keywords = string.Join(", ", settings.Highlights.Keywords);
        foreach (var q in settings.QuickActions) QuickActions.Add(new QuickActionEditViewModel(q, Save, ActionNames, RemoveQuickAction));
        if (emoteSettings?.Value is { } e)
        {
            TwitchChannelId = Channel(e, "Twitch");
            YouTubeChannelId = Channel(e, "YouTube");
            KickChannelId = Channel(e, "Kick");
            SevenTv = e.SevenTv;
            Bttv = e.Bttv;
            Ffz = e.Ffz;
        }
        _loading = false;
        UpdateEmoteStatus();
        if (emotes is not null) emotes.EmotesChanged += _ => Avalonia.Threading.Dispatcher.UIThread.Post(UpdateEmoteStatus);
    }

    /// <summary>Raised after any option changed and was saved.</summary>
    public event Action? Changed;

    public Func<IEnumerable<string>> ActionNames { get; }
    public ObservableCollection<QuickActionEditViewModel> QuickActions { get; } = [];
    public bool HasEmoteService => _emotes is not null;

    [ObservableProperty] private bool _showEmotes;
    [ObservableProperty] private bool _showPronouns;
    [ObservableProperty] private bool _highlightMentions;
    [ObservableProperty] private bool _highlightFirstTime;
    [ObservableProperty] private string _mentionNames = "";
    [ObservableProperty] private string _keywords = "";
    [ObservableProperty] private string _twitchChannelId = "";
    [ObservableProperty] private string _youTubeChannelId = "";
    [ObservableProperty] private string _kickChannelId = "";
    [ObservableProperty] private bool _sevenTv = true;
    [ObservableProperty] private bool _bttv = true;
    [ObservableProperty] private bool _ffz = true;
    [ObservableProperty] private string _emoteStatus = "";

    partial void OnShowEmotesChanged(bool value) { _settings.ShowEmotes = value; Save(); }
    partial void OnShowPronounsChanged(bool value) { _settings.ShowPronouns = value; Save(); }
    partial void OnHighlightMentionsChanged(bool value) { _settings.Highlights.Mentions = value; Save(); }
    partial void OnHighlightFirstTimeChanged(bool value) { _settings.Highlights.FirstTimeChatters = value; Save(); }
    partial void OnMentionNamesChanged(string value) { _settings.Highlights.MentionNames = SplitList(value); Save(); }
    partial void OnKeywordsChanged(string value) { _settings.Highlights.Keywords = SplitList(value); Save(); }
    partial void OnTwitchChannelIdChanged(string value) => SaveEmotes(s => SetChannel(s, "Twitch", value));
    partial void OnYouTubeChannelIdChanged(string value) => SaveEmotes(s => SetChannel(s, "YouTube", value));
    partial void OnKickChannelIdChanged(string value) => SaveEmotes(s => SetChannel(s, "Kick", value));
    partial void OnSevenTvChanged(bool value) => SaveEmotes(s => s.SevenTv = value);
    partial void OnBttvChanged(bool value) => SaveEmotes(s => s.Bttv = value);
    partial void OnFfzChanged(bool value) => SaveEmotes(s => s.Ffz = value);

    [RelayCommand]
    private void AddQuickAction()
    {
        var definition = new QuickActionDefinition { Label = "New quick action", ActionName = ActionNames().FirstOrDefault() ?? "" };
        _settings.QuickActions.Add(definition);
        QuickActions.Add(new QuickActionEditViewModel(definition, Save, ActionNames, RemoveQuickAction));
        Save();
    }

    [RelayCommand]
    private void RemoveQuickAction(QuickActionEditViewModel? item)
    {
        if (item is null) return;
        _settings.QuickActions.Remove(item.Definition);
        QuickActions.Remove(item);
        Save();
    }

    [RelayCommand]
    private async Task RefreshEmotes()
    {
        if (_emotes is null) return;
        EmoteStatus = "Refreshing emotes...";
        try
        {
            var count = await _emotes.RefreshAsync(force: true);
            EmoteStatus = $"{count} emotes loaded";
        }
        catch (Exception ex)
        {
            EmoteStatus = ex.Message;
        }
        UpdateEmoteStatus();
    }

    private void UpdateEmoteStatus()
    {
        if (_emotes is null)
        {
            EmoteStatus = "Emote service not available";
            return;
        }
        var sets = _emotes.LoadedSets();
        EmoteStatus = sets.Count == 0
            ? "No emote sets loaded yet"
            : string.Join(" · ", sets.GroupBy(s => s.Provider).Select(g => $"{g.Key} {g.Sum(s => s.Count)}"));
    }

    private void Save()
    {
        if (_loading) return;
        _persist();
        Changed?.Invoke();
    }

    private void SaveEmotes(Action<EmoteSettings> change)
    {
        if (_loading || _emoteSettings is null) return;
        change(_emoteSettings.Value);
        _emoteSettings.ScheduleSave();
        Changed?.Invoke();
    }

    private static string Channel(EmoteSettings s, string platform) =>
        s.Channels.FirstOrDefault(kv => string.Equals(kv.Key, platform, StringComparison.OrdinalIgnoreCase)).Value ?? "";

    private static void SetChannel(EmoteSettings s, string platform, string value)
    {
        foreach (var key in s.Channels.Keys.Where(k => string.Equals(k, platform, StringComparison.OrdinalIgnoreCase)).ToArray())
            s.Channels.Remove(key);
        if (!string.IsNullOrWhiteSpace(value)) s.Channels[platform] = value.Trim();
    }

    internal static List<string> SplitList(string value) =>
        value.Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
