using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.YouTube;
using Cuebot.YouTube.Auth;

namespace Cuebot.App.ViewModels.YouTube;

/// <summary>YouTube page: Google OAuth client, broadcaster and bot sign-in, connection, current broadcast, polling and quota.</summary>
public sealed partial class YouTubeViewModel : PageViewModel
{
    private readonly YouTubeService _youtube;
    private CancellationTokenSource? _signingIn;

    public YouTubeViewModel(YouTubeService youtube) : base("YouTube")
    {
        _youtube = youtube;
        ShowOwnApp = !youtube.HasBuiltInApp || youtube.Settings.ClientId.Trim().Length > 0;
        youtube.StatusChanged += () => OnUi(() => OnPropertyChanged(string.Empty));
    }

    // ------------------------------------------------------------ OAuth client

    public string ClientId
    {
        get => _youtube.Settings.ClientId;
        set { _youtube.Settings.ClientId = value.Trim(); Save(); }
    }

    /// <summary>Shown masked in the view.</summary>
    public string ClientSecret
    {
        get => _youtube.Settings.ClientSecret;
        set { _youtube.Settings.ClientSecret = value.Trim(); Save(); }
    }

    public bool HasBuiltInApp => _youtube.HasBuiltInApp;

    /// <summary>Advanced starts open when the user's own client is needed or already in use.</summary>
    public bool ShowOwnApp { get; }

    /// <summary>Each sign-in kind is offered when a client for it exists (both when none does, so the error explains why).</summary>
    public bool ShowCodeSignIn => _youtube.CanSignInWithCode || !_youtube.IsConfigured;
    public bool ShowBrowserSignIn => _youtube.CanSignInWithBrowser || !_youtube.IsConfigured;

    public bool AutoConnect
    {
        get => _youtube.Settings.AutoConnect;
        set { _youtube.Settings.AutoConnect = value; Save(); }
    }

    // ------------------------------------------------------------ status and accounts

    public string State => _youtube.State.ToString();
    public string StatusMessage => _youtube.StatusMessage;
    public bool IsConfigured => _youtube.IsConfigured;
    public bool IsBroadcasterSignedIn => _youtube.IsBroadcasterSignedIn;
    public bool IsBotSignedIn => _youtube.IsBotSignedIn;
    public bool CanConnect => IsConfigured && IsBroadcasterSignedIn;

    public string BroadcasterAccount => Describe(_youtube.Settings.Broadcaster);
    public string BotAccount => _youtube.IsBotSignedIn ? Describe(_youtube.Settings.Bot) : "Not signed in (optional: chat can be sent as the broadcaster)";

    public string CurrentBroadcast
    {
        get
        {
            if (!_youtube.IsConnected) return "Not connected";
            if (_youtube.LatestBroadcast is not { } b)
                return _youtube.UpcomingBroadcasts.FirstOrDefault() is { } next
                    ? $"Not live. Next scheduled: {next.Title}"
                    : "Not live";
            var stats = _youtube.StatisticsOf(b.Id);
            var viewers = stats?.ConcurrentViewers is { } v ? $" · {v} watching" : "";
            var more = _youtube.MonitoredBroadcasts.Count > 1 ? $" (+{_youtube.MonitoredBroadcasts.Count - 1} more monitored)" : "";
            return $"Live: {b.Title} ({b.PrivacyStatus}){viewers}{more}";
        }
    }

    public string BroadcastUrl => _youtube.LatestBroadcast?.Url ?? "";
    public bool HasBroadcast => _youtube.LatestBroadcast is not null;

    // ------------------------------------------------------------ polling and quota

    public decimal MinimumChatPollSeconds
    {
        get => _youtube.Settings.MinimumChatPollSeconds;
        set { _youtube.Settings.MinimumChatPollSeconds = (int)Math.Clamp(value, 2, 120); Save(); }
    }

    public decimal BroadcastPollSeconds
    {
        get => _youtube.Settings.BroadcastPollSeconds;
        set { _youtube.Settings.BroadcastPollSeconds = (int)Math.Clamp(value, 30, 3600); Save(); }
    }

    public decimal LivePollSeconds
    {
        get => _youtube.Settings.LivePollSeconds;
        set { _youtube.Settings.LivePollSeconds = (int)Math.Clamp(value, 15, 3600); Save(); }
    }

    public decimal SubscriberPollSeconds
    {
        get => _youtube.Settings.SubscriberPollSeconds;
        set { _youtube.Settings.SubscriberPollSeconds = (int)Math.Clamp(value, 60, 3600); Save(); }
    }

    public bool PollNewSubscribers
    {
        get => _youtube.Settings.PollNewSubscribers;
        set { _youtube.Settings.PollNewSubscribers = value; Save(); }
    }

    public bool MonitorUpcoming
    {
        get => _youtube.Settings.MonitorUpcoming;
        set { _youtube.Settings.MonitorUpcoming = value; Save(); }
    }

    public decimal DailyQuota
    {
        get => _youtube.Settings.DailyQuota;
        set { _youtube.Settings.DailyQuota = (int)Math.Max(1, value); Save(); }
    }

    public string QuotaUsage
    {
        get
        {
            var used = _youtube.QuotaUsedToday;
            var text = string.Format(CultureInfo.CurrentCulture, "About {0:N0} of {1:N0} units used today (resets {2:t})",
                used, _youtube.Settings.DailyQuota, _youtube.QuotaResetsAt.ToLocalTime());
            return _youtube.QuotaExhaustedUntil is null ? text : text + ". Quota exhausted: polling is paused until the reset.";
        }
    }

    public string QuotaEstimate
    {
        get
        {
            var (live, idle) = _youtube.EstimateQuotaPerHour();
            var budget = _youtube.Settings.DailyQuota;
            var hours = live > 0 ? budget / live : 0;
            return string.Format(CultureInfo.CurrentCulture,
                "Polling costs about {0:N0} units/hour while live and {1:N0} units/hour while waiting, so {2:N0} units last about {3:0.#} hours of streaming. Sending a message, moderating or changing the title costs 50 units each.",
                live, idle, budget, hours);
        }
    }

    // ------------------------------------------------------------ sign-in

    [ObservableProperty] private string? _userCode;
    [ObservableProperty] private string? _verificationUrl;
    [ObservableProperty] private string _signingInAs = "";
    [ObservableProperty] private bool _isSigningIn;
    [ObservableProperty] private bool _isWaitingForBrowser;
    [ObservableProperty] private string _testMessage = "";
    [ObservableProperty] private string _error = "";

    public bool IsShowingCode => IsSigningIn && UserCode is not null;

    partial void OnUserCodeChanged(string? value) => OnPropertyChanged(nameof(IsShowingCode));
    partial void OnIsSigningInChanged(bool value) => OnPropertyChanged(nameof(IsShowingCode));

    [RelayCommand]
    private Task SignInBroadcaster() => DeviceSignIn(YouTubeAccountKind.Broadcaster);

    [RelayCommand]
    private Task SignInBot() => DeviceSignIn(YouTubeAccountKind.Bot);

    [RelayCommand]
    private Task BrowserSignInBroadcaster() => BrowserSignIn(YouTubeAccountKind.Broadcaster);

    [RelayCommand]
    private Task BrowserSignInBot() => BrowserSignIn(YouTubeAccountKind.Bot);

    [RelayCommand]
    private void CancelSignIn() => _signingIn?.Cancel();

    [RelayCommand]
    private void OpenVerificationPage()
    {
        if (VerificationUrl is not null) Cuebot.App.Shell.UrlLauncher.Open(VerificationUrl);
    }

    [RelayCommand]
    private void OpenBroadcast()
    {
        if (BroadcastUrl.Length > 0) Cuebot.App.Shell.UrlLauncher.Open(BroadcastUrl);
    }

    [RelayCommand]
    private Task SignOutBroadcaster() => Run(() => _youtube.SignOutAsync(YouTubeAccountKind.Broadcaster));

    [RelayCommand]
    private Task SignOutBot() => Run(() => _youtube.SignOutAsync(YouTubeAccountKind.Bot));

    [RelayCommand]
    private Task Connect() => Run(() => _youtube.ConnectAsync());

    [RelayCommand]
    private Task Disconnect() => Run(_youtube.DisconnectAsync);

    [RelayCommand]
    private Task SendTest() => Run(async () =>
    {
        if (TestMessage.Trim().Length == 0) return;
        await _youtube.SendChatMessageAsync(TestMessage);
        TestMessage = "";
    });

    private async Task DeviceSignIn(YouTubeAccountKind account)
    {
        if (!Ready()) return;
        await SigningIn(account, async ct =>
        {
            var auth = await _youtube.BeginDeviceSignInAsync(account, ct);
            UserCode = auth.UserCode;
            VerificationUrl = auth.VerificationUrl;
            Cuebot.App.Shell.UrlLauncher.Open(auth.VerificationUrl);
            await _youtube.CompleteDeviceSignInAsync(auth, ct);
        });
    }

    private async Task BrowserSignIn(YouTubeAccountKind account)
    {
        if (!Ready()) return;
        await SigningIn(account, async ct =>
        {
            IsWaitingForBrowser = true;
            await _youtube.SignInWithBrowserAsync(account, uri => Cuebot.App.Shell.UrlLauncher.Open(uri.AbsoluteUri), ct);
        });
    }

    private bool Ready()
    {
        Error = "";
        if (_youtube.IsConfigured) return true;
        Error = "Enter the OAuth client ID and client secret of your Google Cloud project under Advanced first.";
        return false;
    }

    private async Task SigningIn(YouTubeAccountKind account, Func<CancellationToken, Task> signIn)
    {
        _signingIn?.Cancel();
        _signingIn = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        SigningInAs = account == YouTubeAccountKind.Bot ? "bot" : "broadcaster";
        IsSigningIn = true;
        try
        {
            await signIn(_signingIn.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsSigningIn = false;
            IsWaitingForBrowser = false;
            UserCode = VerificationUrl = null;
            OnPropertyChanged(string.Empty);
        }
    }

    private async Task Run(Func<Task> operation)
    {
        Error = "";
        try { await operation(); }
        catch (Exception ex) { Error = ex.Message; }
        OnPropertyChanged(string.Empty);
    }

    private void Save()
    {
        _youtube.SaveSettings();
        OnPropertyChanged(string.Empty);
    }

    private static string Describe(YouTubeAccount account) =>
        !account.IsSignedIn ? "Not signed in"
        : account.CustomUrl.Length > 0 ? $"{account.ChannelTitle} ({account.CustomUrl})"
        : account.ChannelTitle.Length > 0 ? account.ChannelTitle
        : "Signed in";
}
