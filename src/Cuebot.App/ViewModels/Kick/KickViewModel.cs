using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Kick;
using Cuebot.Kick.Auth;

namespace Cuebot.App.ViewModels.Kick;

/// <summary>Kick page: app credentials, broadcaster and bot sign-in (browser + loopback), status, channel info, settings.</summary>
public sealed partial class KickViewModel : PageViewModel
{
    private readonly KickService _kick;
    private CancellationTokenSource? _signIn;

    public KickViewModel(KickService kick) : base("Kick")
    {
        _kick = kick;
        kick.StatusChanged += () => OnUi(() => OnPropertyChanged(string.Empty));
    }

    private KickSettings S => _kick.Settings;

    public string ClientId
    {
        get => S.ClientId;
        set { S.ClientId = value.Trim(); Save(); }
    }

    /// <summary>A secret; the view masks it.</summary>
    public string ClientSecret
    {
        get => S.ClientSecret;
        set { S.ClientSecret = value.Trim(); Save(); }
    }

    public decimal RedirectPort
    {
        get => S.RedirectPort;
        set { S.RedirectPort = (int)Math.Clamp(value, 1024, 65535); Save(); OnPropertyChanged(nameof(RedirectUri)); }
    }

    public string RedirectUri => _kick.RedirectUri;

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; Save(); }
    }

    public bool UseRealtimeChat
    {
        get => S.UseRealtimeChat;
        set { S.UseRealtimeChat = value; Save(); }
    }

    public bool UseWebhookRelay
    {
        get => S.UseWebhookRelay;
        set { S.UseWebhookRelay = value; Save(); }
    }

    public decimal PollSeconds
    {
        get => S.PollSeconds;
        set { S.PollSeconds = (int)Math.Max(15, value); Save(); }
    }

    /// <summary>Manual override when kick.com blocks the automatic lookup (0 = look up automatically).</summary>
    public string ChannelId
    {
        get => S.ChannelId > 0 ? S.ChannelId.ToString(CultureInfo.InvariantCulture) : "";
        set { S.ChannelId = ParseId(value); Save(); }
    }

    public string ChatroomId
    {
        get => S.ChatroomId > 0 ? S.ChatroomId.ToString(CultureInfo.InvariantCulture) : "";
        set { S.ChatroomId = ParseId(value); Save(); }
    }

    public string State => _kick.State.ToString();
    public string StatusMessage => _kick.StatusMessage;
    public bool IsBroadcasterSignedIn => _kick.IsBroadcasterSignedIn;
    public bool IsBotSignedIn => _kick.IsBotSignedIn;
    public string BroadcasterAccount => _kick.BroadcasterName ?? "Not signed in";
    public string BotAccount => _kick.BotName ?? "Not signed in (chat is sent as the broadcaster)";
    public string RealtimeStatus => !S.UseRealtimeChat ? "Off"
        : _kick.IsRealtimeConnected ? "Connected"
        : _kick.RealtimeProblem ?? (_kick.IsConnected ? "Connecting..." : "Not connected");

    public string ChannelSlug => S.ChannelSlug.Length > 0 ? S.ChannelSlug : "—";
    public string LiveStatus => _kick.Channel is null ? "—" : _kick.IsLive ? $"Live, {_kick.Channel.ViewerCount} viewers" : "Offline";
    public string StreamTitle => _kick.Channel?.Title is { Length: > 0 } t ? t : "—";
    public string Category => _kick.Channel?.Category?.Name ?? "—";

    [ObservableProperty] private bool _isSigningIn;
    [ObservableProperty] private string _signInUrl = "";
    [ObservableProperty] private string _newTitle = "";
    [ObservableProperty] private string _newCategory = "";
    [ObservableProperty] private string _testMessage = "";
    [ObservableProperty] private bool _testAsBot;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _info = "";

    [RelayCommand]
    private Task SignInBroadcaster() => SignIn(KickAccount.Broadcaster);

    [RelayCommand]
    private Task SignInBot() => SignIn(KickAccount.Bot);

    private async Task SignIn(KickAccount account)
    {
        Error = Info = "";
        _signIn = new CancellationTokenSource();
        IsSigningIn = true;
        try
        {
            await _kick.SignInAsync(account, url =>
            {
                OnUi(() => SignInUrl = url.ToString());
                OpenBrowser(url.ToString());
            }, _signIn.Token);
            Info = account == KickAccount.Bot ? "Bot account signed in." : "Signed in. Connect to start receiving events.";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            IsSigningIn = false;
            SignInUrl = "";
            OnPropertyChanged(string.Empty);
        }
    }

    [RelayCommand]
    private void CancelSignIn() => _signIn?.Cancel();

    [RelayCommand]
    private void OpenSignInPage()
    {
        if (SignInUrl.Length > 0) OpenBrowser(SignInUrl);
    }

    [RelayCommand]
    private Task SignOutBroadcaster() => Run(() => _kick.SignOutAsync(KickAccount.Broadcaster));

    [RelayCommand]
    private Task SignOutBot() => Run(() => _kick.SignOutAsync(KickAccount.Bot));

    [RelayCommand]
    private Task Connect() => Run(() => _kick.ConnectAsync());

    [RelayCommand]
    private Task Disconnect() => Run(_kick.DisconnectAsync);

    [RelayCommand]
    private Task RefreshChannel() => Run(() => _kick.RefreshChannelAsync());

    [RelayCommand]
    private Task UpdateTitle() => Run(async () =>
    {
        if (NewTitle.Trim().Length == 0) return;
        await _kick.SetTitleAsync(NewTitle);
        NewTitle = "";
        Info = "Title updated.";
        await _kick.RefreshChannelAsync();
    });

    [RelayCommand]
    private Task UpdateCategory() => Run(async () =>
    {
        if (NewCategory.Trim().Length == 0) return;
        var category = await _kick.SetCategoryAsync(NewCategory);
        NewCategory = "";
        Info = $"Category set to {category.Name}.";
        await _kick.RefreshChannelAsync();
    });

    [RelayCommand]
    private Task SendTest() => Run(async () =>
    {
        if (TestMessage.Trim().Length == 0) return;
        await _kick.SendChatMessageAsync(TestMessage, TestAsBot, fallback: false);
        TestMessage = "";
    });

    private async Task Run(Func<Task> operation)
    {
        Error = "";
        try { await operation(); }
        catch (Exception ex) { Error = ex.Message; }
        OnPropertyChanged(string.Empty);
    }

    private void Save()
    {
        _kick.SaveSettings();
        OnPropertyChanged(string.Empty);
    }

    private static long ParseId(string text) =>
        long.TryParse(text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0 ? id : 0;

    private static void OpenBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* the link stays visible on the page */ }
    }
}
