using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Twitch;

namespace Cuebot.App.ViewModels.Twitch;

/// <summary>Accounts tab: broadcaster sign-in and connection, the optional bot account, and a test message.</summary>
public sealed partial class TwitchAccountsViewModel(TwitchService twitch) : TwitchTabViewModel(twitch)
{
    private CancellationTokenSource? _authorizing;
    private CancellationTokenSource? _botAuthorizing;

    public static IReadOnlyList<string> SenderChoices { get; } = Enum.GetNames<TwitchSender>();

    public string ClientId
    {
        get => Twitch.Settings.ClientId;
        set { Twitch.Settings.ClientId = value.Trim(); Twitch.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => Twitch.Settings.AutoConnect;
        set { Twitch.Settings.AutoConnect = value; Twitch.SaveSettings(); OnPropertyChanged(); }
    }

    public bool SendChatAsBot
    {
        get => Twitch.Settings.SendChatAsBot;
        set { Twitch.Settings.SendChatAsBot = value; Twitch.SaveSettings(); OnPropertyChanged(); }
    }

    public bool SendWhispersAsBot
    {
        get => Twitch.Settings.SendWhispersAsBot;
        set { Twitch.Settings.SendWhispersAsBot = value; Twitch.SaveSettings(); OnPropertyChanged(); }
    }

    public string State => Twitch.State.ToString();
    public string StatusMessage => Twitch.StatusMessage;
    public bool IsAuthorized => Twitch.IsAuthorized;
    public string Account => Twitch.IsAuthorized ? Twitch.Settings.Login : "Not signed in";

    public bool IsBotAuthorized => Twitch.IsBotAuthorized;
    public string BotState => Twitch.BotState.ToString();
    public string BotStatusMessage => Twitch.BotStatusMessage;
    public string BotAccount => Twitch.IsBotAuthorized ? Twitch.Settings.Bot.Login : "No bot account";

    [ObservableProperty] private string? _userCode;
    [ObservableProperty] private string? _verificationUri;
    [ObservableProperty] private bool _isAuthorizing;
    [ObservableProperty] private string? _botUserCode;
    [ObservableProperty] private string? _botVerificationUri;
    [ObservableProperty] private bool _isBotAuthorizing;
    [ObservableProperty] private string _testMessage = "";
    [ObservableProperty] private string _testSender = nameof(TwitchSender.Default);

    public override void Refresh()
    {
        base.Refresh();
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private async Task Authorize()
    {
        Error = "";
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            Error = "Enter the Client ID of your Twitch application first.";
            return;
        }
        _authorizing = new CancellationTokenSource();
        IsAuthorizing = true;
        try
        {
            var auth = await Twitch.BeginAuthorizationAsync(_authorizing.Token);
            UserCode = auth.UserCode;
            VerificationUri = auth.VerificationUri;
            Cuebot.App.Shell.UrlLauncher.Open(auth.VerificationUri);
            await Twitch.CompleteAuthorizationAsync(auth, _authorizing.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            IsAuthorizing = false;
            UserCode = VerificationUri = null;
            Refresh();
        }
    }

    [RelayCommand]
    private void CancelAuthorize() => _authorizing?.Cancel();

    [RelayCommand]
    private void OpenVerificationPage()
    {
        if (VerificationUri is not null) Cuebot.App.Shell.UrlLauncher.Open(VerificationUri);
    }

    [RelayCommand]
    private async Task AuthorizeBot()
    {
        Error = "";
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            Error = "Enter the Client ID of your Twitch application first.";
            return;
        }
        _botAuthorizing = new CancellationTokenSource();
        IsBotAuthorizing = true;
        try
        {
            var auth = await Twitch.BeginBotAuthorizationAsync(_botAuthorizing.Token);
            BotUserCode = auth.UserCode;
            BotVerificationUri = auth.VerificationUri;
            Cuebot.App.Shell.UrlLauncher.Open(auth.VerificationUri);
            await Twitch.CompleteBotAuthorizationAsync(auth, _botAuthorizing.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            IsBotAuthorizing = false;
            BotUserCode = BotVerificationUri = null;
            Refresh();
        }
    }

    [RelayCommand]
    private void CancelAuthorizeBot() => _botAuthorizing?.Cancel();

    [RelayCommand]
    private void OpenBotVerificationPage()
    {
        if (BotVerificationUri is not null) Cuebot.App.Shell.UrlLauncher.Open(BotVerificationUri);
    }

    [RelayCommand]
    private Task Connect() => Run(() => Twitch.ConnectAsync());

    [RelayCommand]
    private Task Disconnect() => Run(Twitch.DisconnectAsync);

    [RelayCommand]
    private Task ConnectBot() => Run(() => Twitch.ConnectBotAsync());

    [RelayCommand]
    private void SignOut()
    {
        Twitch.SignOut();
        Refresh();
    }

    [RelayCommand]
    private async Task SignOutBot()
    {
        await Run(Twitch.SignOutBotAsync);
        Refresh();
    }

    [RelayCommand]
    private Task SendTest() => Run(async () =>
    {
        if (TestMessage.Length == 0) return;
        var sender = Enum.TryParse<TwitchSender>(TestSender, out var s) ? s : TwitchSender.Default;
        var sentAs = await Api.SendChatMessageAsync(TestMessage, sender);
        TestMessage = "";
        Info = $"Sent as the {(sentAs == TwitchSender.Bot ? "bot account" : "broadcaster")}.";
    });
}
