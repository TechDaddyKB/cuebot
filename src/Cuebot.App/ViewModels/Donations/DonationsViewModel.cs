using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Donations;
using Cuebot.Donations.StreamElements;
using Cuebot.Donations.Streamlabs;

namespace Cuebot.App.ViewModels.Donations;

/// <summary>Donations page: Streamlabs and StreamElements tokens, connection, options, test events and the last events.</summary>
public sealed partial class DonationsViewModel : PageViewModel
{
    private const int RecentShown = 50;
    private readonly DonationServiceBase[] _services;

    public DonationsViewModel(StreamlabsService streamlabs, StreamElementsService streamElements) : base("Donations")
    {
        Streamlabs = new StreamlabsCardViewModel(streamlabs);
        StreamElements = new StreamElementsCardViewModel(streamElements);
        _services = [streamlabs, streamElements];
        foreach (var service in _services) service.RecentChanged += () => OnUi(RefreshRecent);
        RefreshRecent();
    }

    public StreamlabsCardViewModel Streamlabs { get; }
    public StreamElementsCardViewModel StreamElements { get; }

    /// <summary>Newest first, both services.</summary>
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

    private static void OnUi(Action action)
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
