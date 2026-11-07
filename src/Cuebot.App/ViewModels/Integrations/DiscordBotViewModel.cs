using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core;
using Cuebot.Discord;

namespace Cuebot.App.ViewModels.Integrations;

/// <summary>
/// The Discord bot sub-page: setup walkthrough link, token, invite button, the two privileged switches, the bot's servers
/// and channels, and the slash command editor.
/// </summary>
public sealed partial class DiscordBotViewModel : ObservableObject
{
    public const string PortalUrl = "https://discord.com/developers/applications";
    public const string GuideUrl = "https://techdaddykb.github.io/cuebot/guides/services/discord-bot/";

    private readonly DiscordBotService _bot;

    public DiscordBotViewModel(DiscordBotService bot)
    {
        _bot = bot;
        bot.StatusChanged += () => OnUi(() => OnPropertyChanged(string.Empty));
        bot.DataChanged += () => OnUi(RefreshServers);
        foreach (var c in bot.Settings.Commands) Commands.Add(new DiscordCommandRowViewModel(this, c));
        RefreshServers();
    }

    /// <summary>Opens links in the browser (tests replace it).</summary>
    public Action<string> OpenUrl { get; init; } = Cuebot.App.Shell.UrlLauncher.Open;

    private DiscordBotSettings S => _bot.Settings;

    public string Name => _bot.Name;
    public string State => _bot.State.ToString();
    public string StatusMessage => _bot.StatusMessage;
    public bool IsConnected => _bot.State == ConnectionState.Connected;

    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _info = "";

    /// <summary>The bot token; the view masks it.</summary>
    public string BotToken
    {
        get => S.BotToken;
        set
        {
            var token = value.Trim();
            if (token.StartsWith("Bot ", StringComparison.OrdinalIgnoreCase)) token = token[4..].Trim();
            if (token == S.BotToken) return;
            S.BotToken = token;
            // A new token may be another bot: forget what was learned about the old one.
            S.ApplicationId = "";
            S.BotName = "";
            _bot.SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaskedToken));
        }
    }

    public string MaskedToken => S.BotToken.Length == 0
        ? "No bot token saved"
        : $"Saved bot token: {Cuebot.Integrations.Secrets.Mask(S.BotToken)}{(S.BotName.Length > 0 ? $" (bot: {S.BotName})" : "")}";

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; _bot.SaveSettings(); OnPropertyChanged(); }
    }

    public bool MessageContent
    {
        get => S.MessageContentIntent;
        set { S.MessageContentIntent = value; _bot.SaveSettings(); OnPropertyChanged(); OnPropertyChanged(nameof(ReconnectHint)); }
    }

    public bool ServerMembers
    {
        get => S.ServerMembersIntent;
        set { S.ServerMembersIntent = value; _bot.SaveSettings(); OnPropertyChanged(); OnPropertyChanged(nameof(ReconnectHint)); }
    }

    /// <summary>Intents are sent when the bot connects, so a change needs a reconnect.</summary>
    public string ReconnectHint => IsConnected ? "Changed a switch? Click Connect again so Discord sees it." : "";

    public string DefaultReply
    {
        get => S.DefaultReply;
        set { S.DefaultReply = value; _bot.SaveSettings(); OnPropertyChanged(); }
    }

    public ObservableCollection<DiscordServerRowViewModel> Servers { get; } = [];
    public bool HasNoServers => Servers.Count == 0;

    public ObservableCollection<DiscordCommandRowViewModel> Commands { get; } = [];
    public bool HasNoCommands => Commands.Count == 0;

    private void RefreshServers()
    {
        Servers.Clear();
        foreach (var g in _bot.Guilds) Servers.Add(new DiscordServerRowViewModel(g));
        OnPropertyChanged(nameof(HasNoServers));
    }

    [RelayCommand]
    private void OpenPortal() => OpenUrl(PortalUrl);

    [RelayCommand]
    private void OpenGuide() => OpenUrl(GuideUrl);

    [RelayCommand]
    private async Task Connect()
    {
        Error = "";
        try { await _bot.ConnectAsync(); }
        catch (Exception ex) { Error = ex.Message; }
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        Error = "";
        await _bot.DisconnectAsync();
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private Task Invite() => RunAsync(async () =>
    {
        var id = await _bot.ApplicationIdAsync();
        OpenUrl(DiscordBotService.InviteUrl(id));
        OnPropertyChanged(nameof(MaskedToken));
        return "Opened the invite page in your browser: pick your server and click Authorize.";
    });

    [RelayCommand]
    private Task Refresh() => RunAsync(async () =>
    {
        var guilds = await _bot.RefreshAsync();
        return guilds.Count == 0 ? "The bot is not in any server yet. Invite it first." : $"The bot is in {guilds.Count} server(s).";
    });

    [RelayCommand]
    private void AddCommand()
    {
        var command = new DiscordSlashCommand { Name = Commands.Count == 0 ? "socials" : $"command{Commands.Count + 1}", Description = "" };
        S.Commands.Add(command);
        Commands.Add(new DiscordCommandRowViewModel(this, command));
        _bot.SaveSettings();
        OnPropertyChanged(nameof(HasNoCommands));
    }

    internal void RemoveCommand(DiscordCommandRowViewModel row)
    {
        S.Commands.Remove(row.Command);
        Commands.Remove(row);
        _bot.SaveSettings();
        OnPropertyChanged(nameof(HasNoCommands));
    }

    internal void Save() => _bot.SaveSettings();

    [RelayCommand]
    private Task Register() => RunAsync(async () =>
    {
        var servers = await _bot.RegisterCommandsAsync();
        return S.Commands.Count == 0
            ? $"Removed Cuebot's slash commands from {servers} server(s)."
            : $"Registered {S.Commands.Count} slash command(s) in {servers} server(s). Type / in Discord to see them.";
    });

    private async Task RunAsync(Func<Task<string>> command)
    {
        Error = "";
        Info = "";
        try { Info = await command(); }
        catch (Exception ex) { Error = ex.Message; }
    }

    private static void OnUi(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
        else Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }
}

/// <summary>One server with its text channels and roles (read-only).</summary>
public sealed class DiscordServerRowViewModel(DiscordGuild guild)
{
    public string Name => guild.Name;

    public string Channels => string.Join("   ", guild.Channels.Where(c => c.IsText).OrderBy(c => c.Position).Select(c => "#" + c.Name)) is { Length: > 0 } list
        ? list : "No text channels the bot can see";

    public string Roles => string.Join(", ", Cuebot.Discord.DiscordBotService.AssignableRoleNames(guild)) is { Length: > 0 } list
        ? list : "No roles the bot can give";
}

/// <summary>One slash command in the editor; edits write through to the settings.</summary>
public sealed partial class DiscordCommandRowViewModel(DiscordBotViewModel owner, DiscordSlashCommand command) : ObservableObject
{
    internal DiscordSlashCommand Command => command;

    public string Name
    {
        get => command.Name;
        set { command.Name = value.Trim().TrimStart('/').ToLowerInvariant(); owner.Save(); OnPropertyChanged(); }
    }

    public string Description
    {
        get => command.Description;
        set { command.Description = value; owner.Save(); OnPropertyChanged(); }
    }

    public string OptionName
    {
        get => command.OptionName;
        set { command.OptionName = value.Trim().ToLowerInvariant(); owner.Save(); OnPropertyChanged(); }
    }

    public string OptionDescription
    {
        get => command.OptionDescription;
        set { command.OptionDescription = value; owner.Save(); OnPropertyChanged(); }
    }

    public bool OptionRequired
    {
        get => command.OptionRequired;
        set { command.OptionRequired = value; owner.Save(); OnPropertyChanged(); }
    }

    public bool Private
    {
        get => command.Private;
        set { command.Private = value; owner.Save(); OnPropertyChanged(); }
    }

    [RelayCommand]
    private void Remove() => owner.RemoveCommand(this);
}
