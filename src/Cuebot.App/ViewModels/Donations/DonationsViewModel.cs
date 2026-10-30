using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Donations;
using Cuebot.Donations.StreamElements;
using Cuebot.Donations.Streamlabs;
using Cuebot.Donations.TipeeeStream;
using Cuebot.Donations.DonorDrive;
using Cuebot.Donations.TreatStream;
using Cuebot.Donations.Pally;

namespace Cuebot.App.ViewModels.Donations;

/// <summary>Donations page: one card per donation service (keys, connection, options, test events) and the last events.</summary>
public sealed partial class DonationsViewModel : PageViewModel
{
    private const int RecentShown = 50;
    private readonly DonationServiceBase[] _services;

    public DonationsViewModel(StreamlabsService streamlabs, StreamElementsService streamElements)
        : this(streamlabs, streamElements, null)
    {
    }

    /// <summary>All six services (Streamlabs, StreamElements, TipeeeStream, DonorDrive, TreatStream, Pally.gg).</summary>
    public DonationsViewModel(DonationServices services)
        : this(services.Streamlabs, services.StreamElements, services)
    {
    }

    private DonationsViewModel(StreamlabsService streamlabs, StreamElementsService streamElements, DonationServices? all) : base("Donations")
    {
        Streamlabs = new StreamlabsCardViewModel(streamlabs);
        StreamElements = new StreamElementsCardViewModel(streamElements);
        if (all is not null)
        {
            TipeeeStream = new TipeeeStreamCardViewModel(all.TipeeeStream);
            DonorDrive = new DonorDriveCardViewModel(all.DonorDrive);
            TreatStream = new TreatStreamCardViewModel(all.TreatStream);
            Pally = new PallyCardViewModel(all.Pally);
        }
        _services = all is null ? [streamlabs, streamElements] : [.. all.All];
        foreach (var service in _services) service.RecentChanged += () => OnUi(RefreshRecent);
        RefreshRecent();
    }

    public StreamlabsCardViewModel Streamlabs { get; }
    public StreamElementsCardViewModel StreamElements { get; }

    /// <summary>Null when the page was built with only Streamlabs and StreamElements.</summary>
    public TipeeeStreamCardViewModel? TipeeeStream { get; }
    public DonorDriveCardViewModel? DonorDrive { get; }
    public TreatStreamCardViewModel? TreatStream { get; }
    public PallyCardViewModel? Pally { get; }

    /// <summary>Newest first, every service.</summary>
    public ObservableCollection<DonationRecordViewModel> RecentEvents { get; } = [];

    public bool HasNoEvents => RecentEvents.Count == 0;

    private void RefreshRecent()
    {
        var rows = _services
            .SelectMany(s => s.RecentEvents().Select(r => new DonationRecordViewModel(s.Name, r)))
            .OrderByDescending(r => r.Record.Time)
            .Take(RecentShown)
            .ToArray();
        RecentEvents.Clear();
        foreach (var row in rows) RecentEvents.Add(row);
        OnPropertyChanged(nameof(HasNoEvents));
    }
}

public sealed class DonationRecordViewModel(string service, DonationRecord record)
{
    public DonationRecord Record { get; } = record;
    public string Service { get; } = service;
    public string Time => Record.Time.ToLocalTime().ToString("HH:mm:ss");
    public string Summary => Record.Summary;
    public bool IsTest => Record.IsTest;
    public string EventType => Record.Type;
}

/// <summary>One service's card: token, options, connect/disconnect, status and test events.</summary>
public abstract partial class DonationCardViewModel : ObservableObject
{
    private readonly DonationServiceBase _service;

    protected DonationCardViewModel(DonationServiceBase service)
    {
        _service = service;
        TestKinds = service.TestEventKinds;
        _selectedTestKind = TestKinds.FirstOrDefault() ?? "";
        service.StatusChanged += () => OnUi(() => OnPropertyChanged(string.Empty));
    }

    public string Name => _service.Name;
    public string State => _service.State.ToString();
    public string StatusMessage => _service.StatusMessage;
    public bool IsConnected => _service.State == Cuebot.Core.ConnectionState.Connected;

    /// <summary>The token; the view masks it (PasswordChar).</summary>
    public string Token
    {
        get => GetToken();
        set
        {
            var trimmed = value.Trim();
            if (trimmed == GetToken()) return;
            SetToken(trimmed);
            _service.SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaskedToken));
        }
    }

    /// <summary>"Saved token: ••••••••abcd", never the token itself.</summary>
    public string MaskedToken => GetToken().Length == 0 ? "No token saved" : $"Saved token: {Secrets.Mask(GetToken())}";

    public IReadOnlyList<string> TestKinds { get; }

    [ObservableProperty] private string _selectedTestKind;
    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _info = "";

    protected abstract string GetToken();
    protected abstract void SetToken(string token);

    protected void Save() => _service.SaveSettings();

    [RelayCommand]
    private async Task Connect()
    {
        Error = "";
        try { await _service.ConnectAsync(); }
        catch (Exception ex) { Error = ex.Message; }
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        Error = "";
        await _service.DisconnectAsync();
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void SendTest()
    {
        Info = _service.SendTestEvent(SelectedTestKind)
            ? $"Sent a local test {SelectedTestKind} (isTest = true); nothing was sent to {Name}."
            : $"Unknown test event '{SelectedTestKind}'";
    }

    protected static void OnUi(Action action)
    {
        if (Avalonia.Threading.Dispatcher.UIThread.CheckAccess()) action();
        else Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }
}

public sealed class StreamlabsCardViewModel(StreamlabsService service) : DonationCardViewModel(service)
{
    private StreamlabsSettings S => service.Settings;

    protected override string GetToken() => S.SocketToken;
    protected override void SetToken(string token) => S.SocketToken = token;

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; Save(); OnPropertyChanged(); }
    }

    public bool RelayPlatformEvents
    {
        get => S.RelayPlatformEvents;
        set { S.RelayPlatformEvents = value; Save(); OnPropertyChanged(); }
    }
}

public sealed class StreamElementsCardViewModel(StreamElementsService service) : DonationCardViewModel(service)
{
    private StreamElementsSettings S => service.Settings;

    protected override string GetToken() => S.JwtToken;

    protected override void SetToken(string token)
    {
        S.JwtToken = token;
        service.ResetChannel();
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; Save(); OnPropertyChanged(); }
    }

    public bool RelayPlatformEvents
    {
        get => S.RelayPlatformEvents;
        set { S.RelayPlatformEvents = value; Save(); OnPropertyChanged(); }
    }

    /// <summary>Use the older realtime.streamelements.com Socket.IO endpoint instead of Astro.</summary>
    public bool UseLegacyRealtime
    {
        get => S.Transport == StreamElementsTransport.LegacyRealtime;
        set { S.Transport = value ? StreamElementsTransport.LegacyRealtime : StreamElementsTransport.Astro; Save(); OnPropertyChanged(); }
    }

    public string Channel => S.ChannelName.Length > 0 ? S.ChannelName : "—";
}

public sealed class TipeeeStreamCardViewModel(TipeeeStreamService service) : DonationCardViewModel(service)
{
    private TipeeeStreamSettings S => service.Settings;

    protected override string GetToken() => S.ApiKey;
    protected override void SetToken(string token) => S.ApiKey = token;

    public string Username
    {
        get => S.Username;
        set { S.Username = value.Trim(); Save(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; Save(); OnPropertyChanged(); }
    }

    public bool RelayPlatformEvents
    {
        get => S.RelayPlatformEvents;
        set { S.RelayPlatformEvents = value; Save(); OnPropertyChanged(); }
    }
}

public sealed partial class DonorDriveCardViewModel : DonationCardViewModel
{
    private readonly DonorDriveService _service;

    public DonorDriveCardViewModel(DonorDriveService service) : base(service)
    {
        _service = service;
        service.ProfileChanged += () => OnUi(() => OnPropertyChanged(nameof(ProfileSummary)));
    }

    private DonorDriveSettings S => _service.Settings;

    /// <summary>The participant or team id (public, shown in clear).</summary>
    protected override string GetToken() => S.ProfileId;
    protected override void SetToken(string token) => S.ProfileId = token;

    public IReadOnlyList<string> Programs { get; } = ["Extra Life", "DonorDrive test site", "Other DonorDrive charity (custom site)"];

    public int ProgramIndex
    {
        get => (int)S.Program;
        set
        {
            if (value < 0 || value == (int)S.Program) return;
            S.Program = (DonorDriveProgram)value;
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustom));
            OnPropertyChanged(nameof(ApiBase));
        }
    }

    public bool IsCustom => S.Program == DonorDriveProgram.Custom;

    public string CustomBaseUrl
    {
        get => S.CustomBaseUrl;
        set { S.CustomBaseUrl = value.Trim(); Save(); OnPropertyChanged(); OnPropertyChanged(nameof(ApiBase)); }
    }

    /// <summary>The API root that will be polled, or why the custom site is not usable.</summary>
    public string ApiBase
    {
        get
        {
            try { return $"API: {_service.ApiBase.AbsoluteUri}"; }
            catch (DonorDriveApiException ex) { return ex.Message; }
        }
    }

    public bool IsTeam
    {
        get => S.ProfileType == DonorDriveProfileType.Team;
        set { S.ProfileType = value ? DonorDriveProfileType.Team : DonorDriveProfileType.Participant; Save(); OnPropertyChanged(); }
    }

    public decimal PollSeconds
    {
        get => S.PollSeconds;
        set { S.PollSeconds = Math.Max((int)value, (int)DonorDriveService.MinimumInterval.TotalSeconds); Save(); OnPropertyChanged(); }
    }

    public string Currency
    {
        get => S.Currency;
        set { S.Currency = value.Trim().ToUpperInvariant(); Save(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; Save(); OnPropertyChanged(); }
    }

    public string ProfileSummary => _service.Profile is { } p
        ? $"{p.Name}{(p.EventName.Length > 0 ? $" ({p.EventName})" : "")}: {p.Raised:0.##} of {p.Goal:0.##} {S.Currency} raised, {p.NumDonations} donations"
        : "Not read yet";
}

public sealed partial class TreatStreamCardViewModel : DonationCardViewModel
{
    private readonly TreatStreamService _service;
    private readonly Action<string> _openBrowser;

    public TreatStreamCardViewModel(TreatStreamService service, Action<string>? openBrowser = null) : base(service)
    {
        _service = service;
        _openBrowser = openBrowser ?? Cuebot.App.Shell.UrlLauncher.Open;
    }

    private TreatStreamSettings S => _service.Settings;

    /// <summary>The access token (from signing in, or pasted).</summary>
    protected override string GetToken() => S.AccessToken;
    protected override void SetToken(string token) => S.AccessToken = token;

    public string ClientId
    {
        get => S.ClientId;
        set { S.ClientId = value.Trim(); Save(); OnPropertyChanged(); OnPropertyChanged(nameof(RedirectUri)); }
    }

    public bool HasBuiltInApp => _service.HasBuiltInApp;

    /// <summary>Advanced starts open when the user's own app is needed or already in use.</summary>
    public bool ShowOwnApp => _showOwnApp ??= !_service.HasBuiltInApp || S.ClientId.Trim().Length > 0;
    private bool? _showOwnApp;

    /// <summary>The client secret; the view masks it.</summary>
    public string ClientSecret
    {
        get => S.ClientSecret;
        set { S.ClientSecret = value.Trim(); Save(); OnPropertyChanged(); }
    }

    public decimal RedirectPort
    {
        get => S.RedirectPort;
        set { S.RedirectPort = Math.Clamp((int)value, 1024, 65535); Save(); OnPropertyChanged(); OnPropertyChanged(nameof(RedirectUri)); }
    }

    public string RedirectUri => _service.RedirectUri;

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; Save(); OnPropertyChanged(); }
    }

    public string SignInState => _service.IsSignedIn
        ? S.AccessTokenExpiresAt is { } expires ? $"Signed in (token valid until {expires.ToLocalTime():yyyy-MM-dd})" : "Signed in (pasted token)"
        : "Not signed in";

    [ObservableProperty] private bool _isSigningIn;

    [RelayCommand]
    private async Task SignIn()
    {
        Error = "";
        IsSigningIn = true;
        try
        {
            await _service.SignInAsync(_openBrowser);
            Info = "Signed in to TreatStream.";
            OnPropertyChanged(nameof(Token));
            OnPropertyChanged(nameof(MaskedToken));
            await _service.ConnectAsync();
        }
        catch (Exception ex) { Error = ex.Message; }
        finally { IsSigningIn = false; }
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private async Task SignOut()
    {
        Error = "";
        await _service.SignOutAsync();
        OnPropertyChanged(string.Empty);
    }
}

public sealed partial class PallyCardViewModel(PallyService service) : DonationCardViewModel(service)
{
    private PallySettings S => service.Settings;

    protected override string GetToken() => S.ApiKey;
    protected override void SetToken(string token) => S.ApiKey = token;

    public string PageSlug
    {
        get => S.PageSlug;
        set { S.PageSlug = value.Trim(); Save(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set { S.AutoConnect = value; Save(); OnPropertyChanged(); }
    }

    /// <summary>Asks Pally's server to echo a sample tip back (tests the real connection; arrives with isTest = true).</summary>
    [RelayCommand]
    private async Task EchoTest()
    {
        Error = "";
        Info = await service.SendEchoTestAsync()
            ? "Asked Pally.gg to echo a test tip; it should appear in the last events in a moment (isTest = true)."
            : "Connect to Pally.gg first.";
    }
}
