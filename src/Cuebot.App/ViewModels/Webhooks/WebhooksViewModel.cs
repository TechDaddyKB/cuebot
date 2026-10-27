using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core;
using Cuebot.Core.Desktop.Clipboard;
using Cuebot.Core.Webhooks;
using Cuebot.Relay;

namespace Cuebot.App.ViewModels.Webhooks;

/// <summary>
/// Webhooks page: settings and status of the user's Cloudflare relay (relay/README.md), the webhook URL to paste into
/// each service that has a handler, and the latest deliveries with their outcome.
/// </summary>
public sealed partial class WebhooksViewModel : PageViewModel
{
    private readonly RelayClient _relay;
    private readonly IClipboardService? _clipboard;

    public WebhooksViewModel(RelayClient relay, IClipboardService? clipboard = null) : base("Webhooks")
    {
        _relay = relay;
        _clipboard = clipboard;
        Routes = [.. relay.Handlers.Select(h => new WebhookRouteViewModel(this, h))];
        relay.StatusChanged += () => OnUi(RefreshStatus);
        relay.DeliveriesChanged += () => OnUi(RefreshDeliveries);
        RefreshDeliveries();
    }

    private RelaySettings S => _relay.Settings;

    public string Url
    {
        get => S.Url;
        set
        {
            var v = value.Trim().TrimEnd('/');
            if (v == S.Url) return;
            S.Url = v;
            Save();
            OnPropertyChanged();
            RefreshUrls();
        }
    }

    /// <summary>Bound to a password box; the token itself is never shown or logged.</summary>
    public string ConnectToken
    {
        get => S.ConnectToken;
        set
        {
            if (value.Trim() == S.ConnectToken) return;
            S.ConnectToken = value.Trim();
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaskedToken));
        }
    }

    public string RouteSecret
    {
        get => S.RouteSecret;
        set
        {
            if (value.Trim() == S.RouteSecret) return;
            S.RouteSecret = value.Trim();
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(MaskedRouteSecret));
            RefreshUrls();
        }
    }

    public string MaskedToken => Masked(S.ConnectToken, "connect token");
    public string MaskedRouteSecret => Masked(S.RouteSecret, "route secret");

    public bool Enabled
    {
        get => S.Enabled;
        set
        {
            if (value == S.Enabled) return;
            S.Enabled = value;
            Save();
            OnPropertyChanged();
            if (!value && _relay.IsRunning) _ = Disconnect();
        }
    }

    public bool AutoConnect
    {
        get => S.AutoConnect;
        set
        {
            if (value == S.AutoConnect) return;
            S.AutoConnect = value;
            Save();
            OnPropertyChanged();
        }
    }

    public string State => _relay.State.ToString();
    public string StatusMessage => _relay.StatusMessage;
    public bool IsConnected => _relay.State == ConnectionState.Connected;

    /// <summary>What is missing before the relay can connect, or "".</summary>
    public string Missing => _relay.MissingSettings() ?? (S.RouteSecret.Length == 0 ? "Enter the route secret to see the webhook URLs." : "");

    public ObservableCollection<WebhookRouteViewModel> Routes { get; }
    public bool HasNoRoutes => Routes.Count == 0;

    public ObservableCollection<WebhookDeliveryRowViewModel> Deliveries { get; } = [];
    public bool HasNoDeliveries => Deliveries.Count == 0;

    [ObservableProperty] private string _error = "";
    [ObservableProperty] private string _info = "";

    [RelayCommand]
    private async Task Connect()
    {
        Error = "";
        Info = "";
        if (!S.Enabled)
        {
            S.Enabled = true;
            Save();
            OnPropertyChanged(nameof(Enabled));
        }
        try { await _relay.ConnectAsync(); }
        catch (Exception ex) { Error = ex.Message; }
        RefreshStatus();
    }

    [RelayCommand]
    private async Task Disconnect()
    {
        Error = "";
        await _relay.DisconnectAsync();
        RefreshStatus();
    }

    internal async Task CopyAsync(string text, string what)
    {
        Error = "";
        Info = "";
        if (_clipboard is not { IsAvailable: true } clipboard)
        {
            Error = "The clipboard is not available; select the URL and copy it by hand.";
            return;
        }
        try
        {
            await clipboard.SetTextAsync(text);
            Info = $"Copied the {what} webhook URL. Paste it into {what}'s webhook settings.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
    }

    private void Save()
    {
        _relay.SaveSettings();
        OnPropertyChanged(nameof(Missing));
    }

    private void RefreshUrls()
    {
        foreach (var r in Routes) r.Refresh();
        OnPropertyChanged(nameof(Missing));
    }

    private void RefreshStatus()
    {
        OnPropertyChanged(nameof(State));
        OnPropertyChanged(nameof(StatusMessage));
        OnPropertyChanged(nameof(IsConnected));
    }

    private void RefreshDeliveries()
    {
        Deliveries.Clear();
        foreach (var d in _relay.RecentDeliveries) Deliveries.Add(new WebhookDeliveryRowViewModel(d, NameOf(d.Service)));
        OnPropertyChanged(nameof(HasNoDeliveries));
    }

    private string NameOf(string service) =>
        _relay.Handlers.FirstOrDefault(h => string.Equals(h.Service, service, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? service;

    internal string? WebhookUrl(string service) => _relay.WebhookUrl(service);

    private static string Masked(string secret, string what)
    {
        if (secret.Length == 0) return $"No {what} saved";
        var tail = secret.Length >= 16 ? secret[^4..] : "";
        return $"Saved {what}: {new string('•', 8)}{tail}";
    }
}

/// <summary>One registered handler and the URL to paste into that service.</summary>
public sealed partial class WebhookRouteViewModel(WebhooksViewModel page, IWebhookHandler handler) : ObservableObject
{
    public string DisplayName => handler.DisplayName;
    public string Service => handler.Service;

    /// <summary>The full URL (includes the route secret), or a hint while the settings are incomplete.</summary>
    public string Url => page.WebhookUrl(handler.Service) ?? "Enter the relay URL and route secret";

    public bool HasUrl => page.WebhookUrl(handler.Service) is not null;

    public void Refresh()
    {
        OnPropertyChanged(nameof(Url));
        OnPropertyChanged(nameof(HasUrl));
        CopyCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasUrl))]
    private Task Copy() => page.WebhookUrl(handler.Service) is { } url ? page.CopyAsync(url, handler.DisplayName) : Task.CompletedTask;
}

/// <summary>A row of the recent-deliveries list (no headers or body).</summary>
public sealed class WebhookDeliveryRowViewModel(RelayDeliveryRecord record, string serviceName)
{
    public string Time => record.HandledAt.ToLocalTime().ToString("HH:mm:ss", CultureInfo.CurrentCulture);
    public string Service => serviceName;
    public string Path => record.Path.Length == 0 ? "" : "/" + record.Path;
    public string Outcome => record.Outcome.ToString();
    public string Detail => record.Detail;
    public bool IsProblem => record.Outcome is WebhookOutcome.Rejected or WebhookOutcome.Failed or WebhookOutcome.NotConfigured;
}
