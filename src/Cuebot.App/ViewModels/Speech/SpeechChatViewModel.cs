using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Speech;

namespace Cuebot.App.ViewModels.Speech;

/// <summary>A "!tts" sub-command: on/off, who may use it and its cooldown, stored as overrides of the defaults.</summary>
public sealed partial class SubCommandRowViewModel : ObservableObject
{
    private readonly SpeechPageContext _ctx;
    private readonly SpeechDefaults.SubCommand _command;

    public SubCommandRowViewModel(SpeechDefaults.SubCommand command, SpeechPageContext ctx)
    {
        _command = command;
        _ctx = ctx;
        Audience = new AudienceViewModel(() => Permission, SetPermission);
    }

    private TtsCommandSettings Settings => _ctx.Settings.Commands;

    public string Name => _command.Name;
    public string Usage => _command.Usage;
    public bool IsViewerCommand => _command.ViewerCommand;
    public string Kind => _command.ViewerCommand ? "Viewer" : "Moderator";

    public AudienceViewModel Audience { get; }

    private ChatAudience Permission => Settings.Permissions.TryGetValue(Name, out var p) ? p : _command.DefaultPermission;

    /// <summary>Off leaves the command to the broadcaster only.</summary>
    public bool Enabled
    {
        get => Permission != ChatAudience.None;
        set
        {
            if (value == Enabled) return;
            SetPermission(value ? _command.DefaultPermission : ChatAudience.None);
            Audience.Refresh();
        }
    }

    public bool IsCustomized => Settings.Permissions.ContainsKey(Name) || Settings.Cooldowns.ContainsKey(Name);

    /// <summary>Seconds; empty uses the viewer cooldown.</summary>
    public decimal? Cooldown
    {
        get => Settings.Cooldowns.TryGetValue(Name, out var seconds) ? seconds : null;
        set
        {
            if (value is null) Settings.Cooldowns.Remove(Name);
            else Settings.Cooldowns[Name] = SpeechText.Clamp(value, 0, 3600);
            _ctx.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustomized));
        }
    }

    [RelayCommand]
    private void Reset()
    {
        Settings.Permissions.Remove(Name);
        Settings.Cooldowns.Remove(Name);
        _ctx.Save();
        Audience.Refresh();
        OnPropertyChanged(string.Empty);
    }

    private void SetPermission(ChatAudience value)
    {
        if (value == _command.DefaultPermission) Settings.Permissions.Remove(Name);
        else Settings.Permissions[Name] = value;
        _ctx.Save();
        OnPropertyChanged(nameof(Enabled));
        OnPropertyChanged(nameof(IsCustomized));
    }
}

/// <summary>A custom speaking command ("!pirate text") with its own alias and permission.</summary>
public sealed partial class CustomCommandViewModel : ObservableObject
{
    private readonly SpeechPageContext _ctx;

    public CustomCommandViewModel(TtsCustomCommand command, SpeechPageContext ctx, IReadOnlyList<Choice> aliasChoices)
    {
        Command = command;
        _ctx = ctx;
        AliasChoices = aliasChoices;
        Audience = new AudienceViewModel(() => command.Permission, v => { command.Permission = v; ctx.Save(); });
    }

    public TtsCustomCommand Command { get; }

    public AudienceViewModel Audience { get; }

    public string Text
    {
        get => Command.Command;
        set
        {
            var text = (value ?? "").Trim();
            if (text == Command.Command) return;
            Command.Command = text;
            _ctx.Save();
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<Choice> AliasChoices { get; private set; }

    public Choice? Alias
    {
        get => SpeechPageContext.Find(AliasChoices, Command.Alias);
        set
        {
            if (value is null || value.Value == Command.Alias) return;
            Command.Alias = value.Value;
            _ctx.Save();
            OnPropertyChanged();
        }
    }

    public void RefreshChoices(IReadOnlyList<Choice> aliasChoices)
    {
        AliasChoices = aliasChoices;
        OnPropertyChanged(nameof(AliasChoices));
        OnPropertyChanged(nameof(Alias));
    }
}

/// <summary>Chat &amp; commands tab: reading chat aloud, the !tts command, speaking commands and moderation.</summary>
public sealed partial class SpeechChatViewModel : ObservableObject
{
    private readonly SpeechPageContext _ctx;

    public SpeechChatViewModel(SpeechPageContext ctx)
    {
        _ctx = ctx;
        Permission = new AudienceViewModel(() => Chat.Permission, v => { Chat.Permission = v; Save(); });
        SpeakCommandPermission = new AudienceViewModel(() => Commands.SpeakCommandPermission, v => { Commands.SpeakCommandPermission = v; Save(); });
        SubCommands = SpeechDefaults.SubCommands.Select(c => new SubCommandRowViewModel(c, ctx)).ToArray();
        LoadChoices();
        CustomCommands = new ObservableCollection<CustomCommandViewModel>(
            Commands.CustomCommands.Select(c => new CustomCommandViewModel(c, ctx, CommandAliasChoices)));
        ctx.ChoicesChanged += () =>
        {
            LoadChoices();
            foreach (var command in CustomCommands) command.RefreshChoices(CommandAliasChoices);
            OnPropertyChanged(nameof(AliasChoices));
            OnPropertyChanged(nameof(CommandAliasChoices));
            OnPropertyChanged(nameof(FilterProfiles));
            OnPropertyChanged(nameof(Alias));
            OnPropertyChanged(nameof(FilterProfile));
        };
    }

    private SpeakChatSettings Chat => _ctx.Settings.SpeakChat;
    private TtsCommandSettings Commands => _ctx.Settings.Commands;
    private ModerationSettings Moderation => _ctx.Settings.Moderation;

    public IReadOnlyList<Choice> AliasChoices { get; private set; } = [];
    public IReadOnlyList<Choice> CommandAliasChoices { get; private set; } = [];
    public IReadOnlyList<Choice> FilterProfiles { get; private set; } = [];

    // Speak chat
    public bool SpeakChatEnabled { get => Chat.Enabled; set => Set(Chat.Enabled, value, v => Chat.Enabled = v); }
    public IReadOnlyList<SpeakMode> Modes { get; } = Enum.GetValues<SpeakMode>();
    public SpeakMode Mode { get => Chat.Mode; set => Set(Chat.Mode, value, v => Chat.Mode = v); }
    public AudienceViewModel Permission { get; }
    public bool AllowForced { get => Chat.AllowForced; set => Set(Chat.AllowForced, value, v => Chat.AllowForced = v); }

    public string Platforms
    {
        get => SpeechText.JoinList(Chat.Platforms);
        set => SetList(Chat.Platforms, SpeechText.SplitList(value), v => Chat.Platforms = v);
    }

    public string Template { get => Chat.Template; set => Set(Chat.Template, value ?? "", v => Chat.Template = v); }
    public string RepeatTemplate { get => Chat.RepeatTemplate; set => Set(Chat.RepeatTemplate, value ?? "", v => Chat.RepeatTemplate = v); }
    public decimal NameGraceSeconds { get => Chat.NameGraceSeconds; set => Set(Chat.NameGraceSeconds, SpeechText.Clamp(value, 0, 3600), v => Chat.NameGraceSeconds = v); }
    public bool UseNicknames { get => Chat.UseNicknames; set => Set(Chat.UseNicknames, value, v => Chat.UseNicknames = v); }
    public bool RandomVoices { get => Chat.RandomVoices; set => Set(Chat.RandomVoices, value, v => Chat.RandomVoices = v); }
    public bool StickyRandom { get => Chat.StickyRandom; set => Set(Chat.StickyRandom, value, v => Chat.StickyRandom = v); }

    public Choice? Alias
    {
        get => SpeechPageContext.Find(AliasChoices, Chat.Alias);
        set { if (value is not null) Set(Chat.Alias, value.Value, v => Chat.Alias = v); }
    }

    public Choice? FilterProfile
    {
        get => SpeechPageContext.Find(FilterProfiles, Chat.FilterProfile);
        set { if (value is not null) Set(Chat.FilterProfile, value.Value, v => Chat.FilterProfile = v); }
    }

    public string TemplateVariables { get; } = SpeechDefaults.Variables(SpeechDefaults.ChatVariables);

    // !tts
    public bool CommandsEnabled { get => Commands.Enabled; set => Set(Commands.Enabled, value, v => Commands.Enabled = v); }
    public string Prefix { get => Commands.Prefix; set => Set(Commands.Prefix, (value ?? "").Trim(), v => Commands.Prefix = v); }
    public bool SilenceOutput { get => Commands.SilenceOutput; set => Set(Commands.SilenceOutput, value, v => Commands.SilenceOutput = v); }
    public string NotAllowedText { get => Commands.NotAllowedText; set => Set(Commands.NotAllowedText, value ?? "", v => Commands.NotAllowedText = v); }
    public decimal ViewerCooldownSeconds { get => Commands.ViewerCooldownSeconds; set => Set(Commands.ViewerCooldownSeconds, SpeechText.Clamp(value, 0, 3600), v => Commands.ViewerCooldownSeconds = v); }
    public bool AllowViewerNicknames { get => Commands.AllowViewerNicknames; set => Set(Commands.AllowViewerNicknames, value, v => Commands.AllowViewerNicknames = v); }

    public string ViewerAliases
    {
        get => SpeechText.JoinList(Commands.ViewerAliases);
        set => SetList(Commands.ViewerAliases, SpeechText.SplitList(value), v => Commands.ViewerAliases = v);
    }

    public IReadOnlyList<SubCommandRowViewModel> SubCommands { get; }

    // Speaking commands
    public string SpeakCommands
    {
        get => SpeechText.JoinList(Commands.SpeakCommands);
        set => SetList(Commands.SpeakCommands, SpeechText.SplitList(value), v => Commands.SpeakCommands = v);
    }

    public AudienceViewModel SpeakCommandPermission { get; }

    public ObservableCollection<CustomCommandViewModel> CustomCommands { get; }

    // Moderation
    public bool SkipOnMessageDeleted { get => Moderation.SkipOnMessageDeleted; set => Set(Moderation.SkipOnMessageDeleted, value, v => Moderation.SkipOnMessageDeleted = v); }
    public bool SkipOnTimeout { get => Moderation.SkipOnTimeout; set => Set(Moderation.SkipOnTimeout, value, v => Moderation.SkipOnTimeout = v); }
    public bool SkipOnBan { get => Moderation.SkipOnBan; set => Set(Moderation.SkipOnBan, value, v => Moderation.SkipOnBan = v); }

    [RelayCommand]
    private void AddCustomCommand()
    {
        var command = new TtsCustomCommand { Command = $"!voice{Commands.CustomCommands.Count + 1}" };
        Commands.CustomCommands.Add(command);
        Save();
        CustomCommands.Add(new CustomCommandViewModel(command, _ctx, CommandAliasChoices));
    }

    [RelayCommand]
    private void RemoveCustomCommand(CustomCommandViewModel? command)
    {
        if (command is null) return;
        Commands.CustomCommands.Remove(command.Command);
        Save();
        CustomCommands.Remove(command);
    }

    private void LoadChoices()
    {
        AliasChoices = _ctx.AliasChoices("Default voice");
        CommandAliasChoices = _ctx.AliasChoices("Chat alias");
        FilterProfiles = _ctx.FilterProfileChoices();
    }

    private void Set<T>(T current, T value, Action<T> set, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        set(value);
        Save();
        OnPropertyChanged(name);
    }

    private void SetList(List<string> current, List<string> value, Action<List<string>> set)
    {
        if (current.SequenceEqual(value)) return;
        set(value);
        Save();
    }

    private void Save() => _ctx.Save();
}
