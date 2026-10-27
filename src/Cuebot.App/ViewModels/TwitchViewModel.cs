using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Twitch;

namespace Cuebot.App.ViewModels;

public sealed partial class TwitchViewModel : PageViewModel
{
    private readonly TwitchService _twitch;
    private CancellationTokenSource? _authorizing;

    public TwitchViewModel(TwitchService twitch) : base("Twitch")
    {
        _twitch = twitch;
        twitch.StatusChanged += () => OnUi(() => OnPropertyChanged(string.Empty));
    }

    public string ClientId
    {
        get => _twitch.Settings.ClientId;
        set { _twitch.Settings.ClientId = value.Trim(); _twitch.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => _twitch.Settings.AutoConnect;
        set { _twitch.Settings.AutoConnect = value; _twitch.SaveSettings(); OnPropertyChanged(); }
    }

    public string State => _twitch.State.ToString();
    public string StatusMessage => _twitch.StatusMessage;
    public bool IsAuthorized => _twitch.IsAuthorized;
    public string Account => _twitch.IsAuthorized ? _twitch.Settings.Login : "Not signed in";

    [ObservableProperty] private string? _userCode;
    [ObservableProperty] private string? _verificationUri;
    [ObservableProperty] private bool _isAuthorizing;
    [ObservableProperty] private string _testMessage = "";
    [ObservableProperty] private string _error = "";

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
            var auth = await _twitch.BeginAuthorizationAsync(_authorizing.Token);
            UserCode = auth.UserCode;
            VerificationUri = auth.VerificationUri;
            OpenBrowser(auth.VerificationUri);
            await _twitch.CompleteAuthorizationAsync(auth, _authorizing.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            IsAuthorizing = false;
            UserCode = VerificationUri = null;
            OnPropertyChanged(string.Empty);
        }
    }

    [RelayCommand]
    private void CancelAuthorize() => _authorizing?.Cancel();

    [RelayCommand]
    private void OpenVerificationPage()
    {
        if (VerificationUri is not null) OpenBrowser(VerificationUri);
    }

    [RelayCommand]
    private Task Connect() => Run(() => _twitch.ConnectAsync());

    [RelayCommand]
    private Task Disconnect() => Run(_twitch.DisconnectAsync);

    [RelayCommand]
    private void SignOut()
    {
        _twitch.SignOut();
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private Task SendTest() => Run(async () =>
    {
        if (TestMessage.Length == 0) return;
        await _twitch.SendChatMessageAsync(TestMessage);
        TestMessage = "";
    });

    private async Task Run(Func<Task> operation)
    {
        Error = "";
        try { await operation(); }
        catch (Exception ex) { Error = ex.Message; }
    }

    internal static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* the code and link stay visible on the page */ }
    }
}
