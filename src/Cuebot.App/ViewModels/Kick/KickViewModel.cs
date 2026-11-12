using System.Diagnostics;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Kick;
using Cuebot.Kick.Api;
using Cuebot.Kick.Auth;

namespace Cuebot.App.ViewModels.Kick;

/// <summary>Kick page: app credentials, broadcaster and bot sign-in (browser + loopback), status, channel info, settings.</summary>
public sealed partial class KickViewModel : PageViewModel
{
    private readonly KickService _kick;
    private readonly Cuebot.Obs.ObsService? _obs;
    private CancellationTokenSource? _signIn;

    public KickViewModel(KickService kick, Cuebot.Obs.ObsService? obs = null) : base("Kick")
    {
        _kick = kick;
        _obs = obs;
        RewardEditor = new KickRewardsEditorViewModel(kick);
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

    public bool HasBuiltInApp => _kick.HasBuiltInApp;

    /// <summary>Advanced starts open when the user's own app is needed or already in use.</summary>
    public bool ShowOwnApp => _showOwnApp ??= !_kick.HasBuiltInApp || S.ClientId.Trim().Length > 0;
    private bool? _showOwnApp;

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

    /// <summary>Kick's official events through Cuebot's shared intake (built-in app only).</summary>
    public bool UseSharedEvents
    {
        get => S.UseSharedEvents;
        set { S.UseSharedEvents = value; Save(); _ = ReconnectForEvents(); }
    }

    public bool WebhookChat
    {
        get => S.WebhookChat;
        set { S.WebhookChat = value; Save(); }
    }

    /// <summary>The shared intake only serves Cuebot's built-in Kick app; with an own app the relay setting applies.</summary>
    public bool UsesBuiltInApp => _kick.App.IsBuiltIn;

    public string SharedEventsStatus => !S.UseSharedEvents ? "Off"
        : _kick.SharedEventsStatus is { Length: > 0 } status ? status
        : _kick.IsConnected ? "Connecting..." : "Connects with Kick";

    /// <summary>"Sign in again to let Cuebot ..." when the broadcaster sign-in lacks scopes Cuebot now asks for.</summary>
    public string MissingScopesMessage => _kick.MissingScopesMessage ?? "";

    public bool HasMissingScopes => _kick.MissingScopesMessage is not null;

    public string SubscriberCount => _kick.Channel is { } c
        ? $"{c.ActiveSubscribers} ({c.ActiveGiftedSubscribers} gifted)"
        : "—";

    public string Tags => _kick.Channel is { Tags.Count: > 0 } c ? string.Join(", ", c.Tags) : "—";

    private async Task ReconnectForEvents()
    {
        if (!_kick.IsConnected) return;
        await Run(async () => await _kick.ConnectAsync());
    }

    public bool UseSevenTvEvents
    {
        get => S.UseSevenTvEvents;
        set { S.UseSevenTvEvents = value; Save(); }
    }

    public string SevenTvStatus => _kick.SevenTvStatus;

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
    [ObservableProperty] private string _newTags = "";
    [ObservableProperty] private decimal _adLength = 30;
    [ObservableProperty] private string _adStatus = "";
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
                Cuebot.App.Shell.UrlLauncher.Open(url.ToString());
            }, _signIn.Token);
            Info = account == KickAccount.Bot ? "Bot account signed in." : "Signed in and connected.";
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
        if (SignInUrl.Length > 0) Cuebot.App.Shell.UrlLauncher.Open(SignInUrl);
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
    private Task UpdateTags() => Run(async () =>
    {
        var tags = await _kick.SetTagsAsync(NewTags);
        NewTags = "";
        Info = tags.Count == 0 ? "Tags removed." : $"Tags set to {string.Join(", ", tags)}.";
        await _kick.RefreshChannelAsync();
    });

    // ---- ads ----

    [RelayCommand]
    private Task RunAd() => Run(async () =>
    {
        var ad = await _kick.RunAdBreakAsync((int)AdLength);
        Info = $"Ad break started; {ad.RemainingAdBreaks} left in this period.";
        await LoadAdStatus();
    });

    [RelayCommand]
    private Task RefreshAdStatus() => Run(LoadAdStatus);

    [RelayCommand]
    private Task EnrollInAds() => Run(async () =>
    {
        await _kick.EnrollInAdsAsync();
        Info = "Your channel is enrolled in Kick ads.";
        await LoadAdStatus();
    });

    private async Task LoadAdStatus()
    {
        var s = await _kick.GetAdStatusAsync();
        AdStatus = !s.OptedIn ? "Not enrolled in Kick ads"
            : s.AdsBlocked ? "Ads are blocked on this channel right now"
            : $"{s.RemainingAdBreaks} of {s.MaxAdBreaks} ad breaks left (per {Math.Max(1, s.LimitPeriodSeconds / 60)} min)"
              + (s.AutoAdsActive ? " · automatic ads on" : "");
    }

    // ---- stream key ----

    public bool HasObs => _obs is not null;

    /// <summary>Puts the channel's ingest address and stream key into OBS's stream settings (custom RTMP). The key is not shown.</summary>
    [RelayCommand]
    private Task SendStreamKeyToObs() => Run(async () =>
    {
        if (_obs is null) return;
        if (!_obs.IsConnected) throw new InvalidOperationException("Connect OBS first (OBS page).");
        var credentials = await _kick.GetStreamCredentialsAsync();
        var status = await _obs.RequestAsync("GetStreamStatus");
        if (status.TryGetProperty("outputActive", out var active) && active.GetBoolean())
            throw new InvalidOperationException("OBS is streaming; stop the stream before changing where it goes.");
        await _obs.RequestAsync("SetStreamServiceSettings", new
        {
            streamServiceType = "rtmp_custom",
            streamServiceSettings = new { server = credentials.Url, key = credentials.Key },
        });
        Info = "OBS now streams to your Kick channel.";
    });

    [RelayCommand]
    private Task SendTest() => Run(async () =>
    {
        if (TestMessage.Trim().Length == 0) return;
        await _kick.SendChatMessageAsync(TestMessage, TestAsBot, fallback: false);
        TestMessage = "";
    });

    // ---- channel rewards ----

    /// <summary>The rewards editor (its own card on the page).</summary>
    public KickRewardsEditorViewModel RewardEditor { get; }

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
}
