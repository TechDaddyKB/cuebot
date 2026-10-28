using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Core;
using Cuebot.Server;

namespace Cuebot.App.ViewModels.Servers;

/// <summary>
/// "Servers" page: enable and configure the local API servers (Streamer.bot WebSocket, HTTP, UDP, Speaker.bot), the
/// user's custom WebSocket servers and clients, and copy the OBS dock URLs. Edits are saved at once; "Apply" restarts
/// the built-in servers with the new settings.
/// </summary>
public sealed partial class ServersViewModel : PageViewModel
{
    private readonly ServerManager _servers;

    /// <param name="remote">The "Remote instances" card (links to other Cuebot instances); null hides it.</param>
    public ServersViewModel(ServerManager servers, RemoteInstancesViewModel? remote = null) : base("Servers")
    {
        _servers = servers;
        Remote = remote;
        foreach (var mapping in Settings.Http.Mappings) Mappings.Add(new HttpMappingViewModel(this, mapping));
        foreach (var server in Settings.CustomServers) CustomServers.Add(new CustomServerViewModel(this, server));
        foreach (var client in Settings.CustomClients) CustomClients.Add(new CustomClientViewModel(this, client));
        servers.StatusChanged += () => OnUi(Refresh);
        Refresh();
    }

    internal ServerManager Manager => _servers;

    public RemoteInstancesViewModel? Remote { get; }
    private ServerSettings Settings => _servers.Settings;

    internal void Save() => _servers.SaveSettings();

    // ---- Network ---------------------------------------------------------------------------------------------------

    public bool AllowLan
    {
        get => Settings.AllowLan;
        set { Settings.AllowLan = value; Save(); OnPropertyChanged(); }
    }

    /// <summary>Allowed web origins, one per line.</summary>
    public string AllowedOrigins
    {
        get => string.Join(Environment.NewLine, Settings.AllowedOrigins);
        set
        {
            Settings.AllowedOrigins = value.Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            Save();
            OnPropertyChanged();
        }
    }

    // ---- Streamer.bot WebSocket ------------------------------------------------------------------------------------

    public bool WebSocketEnabled { get => Settings.WebSocket.Enabled; set { Settings.WebSocket.Enabled = value; Changed(); } }
    public string WebSocketAddress { get => Settings.WebSocket.Address; set { Settings.WebSocket.Address = value.Trim(); Changed(); } }
    public decimal WebSocketPort { get => Settings.WebSocket.Port; set { Settings.WebSocket.Port = (int)value; Changed(); } }
    public string WebSocketEndpoint { get => Settings.WebSocket.Endpoint; set { Settings.WebSocket.Endpoint = value.Trim(); Changed(); } }
    public bool WebSocketAuth { get => Settings.WebSocket.AuthenticationEnabled; set { Settings.WebSocket.AuthenticationEnabled = value; Changed(); } }
    public string WebSocketPassword { get => Settings.WebSocket.Password; set { Settings.WebSocket.Password = value; Changed(); } }
    public bool WebSocketEnforce { get => Settings.WebSocket.EnforceAuthentication; set { Settings.WebSocket.EnforceAuthentication = value; Changed(); } }

    // ---- HTTP ------------------------------------------------------------------------------------------------------

    public bool HttpEnabled { get => Settings.Http.Enabled; set { Settings.Http.Enabled = value; Changed(); } }
    public string HttpAddress { get => Settings.Http.Address; set { Settings.Http.Address = value.Trim(); Changed(); } }
    public decimal HttpPort { get => Settings.Http.Port; set { Settings.Http.Port = (int)value; Changed(); } }
    public ObservableCollection<HttpMappingViewModel> Mappings { get; } = [];

    /// <summary>The chat and activity dock URLs (OBS: Docks → Custom Browser Docks, or a browser source).</summary>
    public IReadOnlyList<DockUrlViewModel> DockUrls => _servers.DockUrls.Select(d => new DockUrlViewModel(d.Name, d.Url)).ToArray();

    [RelayCommand]
    private void AddMapping()
    {
        var mapping = new HttpMapping { Path = "/overlays", Folder = "" };
        Settings.Http.Mappings.Add(mapping);
        Mappings.Add(new HttpMappingViewModel(this, mapping));
        Save();
    }

    internal void RemoveMapping(HttpMappingViewModel item)
    {
        Settings.Http.Mappings.Remove(item.Mapping);
        Mappings.Remove(item);
        Save();
    }

    // ---- UDP and Speaker.bot ---------------------------------------------------------------------------------------

    public bool UdpEnabled { get => Settings.Udp.Enabled; set { Settings.Udp.Enabled = value; Changed(); } }
    public string UdpAddress { get => Settings.Udp.Address; set { Settings.Udp.Address = value.Trim(); Changed(); } }
    public decimal UdpPort { get => Settings.Udp.Port; set { Settings.Udp.Port = (int)value; Changed(); } }

    public bool SpeakerWebSocketEnabled { get => Settings.SpeakerBot.WebSocketEnabled; set { Settings.SpeakerBot.WebSocketEnabled = value; Changed(); } }
    public bool SpeakerUdpEnabled { get => Settings.SpeakerBot.UdpEnabled; set { Settings.SpeakerBot.UdpEnabled = value; Changed(); } }
    public string SpeakerAddress { get => Settings.SpeakerBot.Address; set { Settings.SpeakerBot.Address = value.Trim(); Changed(); } }
    public decimal SpeakerWebSocketPort { get => Settings.SpeakerBot.WebSocketPort; set { Settings.SpeakerBot.WebSocketPort = (int)value; Changed(); } }
    public decimal SpeakerUdpPort { get => Settings.SpeakerBot.UdpPort; set { Settings.SpeakerBot.UdpPort = (int)value; Changed(); } }

    // ---- REST API and MCP ------------------------------------------------------------------------------------------

    private const string Masked = "••••••••••••••••";

    public bool ApiEnabled
    {
        get => Settings.Api.Enabled;
        set { Settings.Api.Enabled = value; if (value) _servers.EnsureApiToken(); Changed(); RaiseApiChanged(); }
    }

    public string ApiAddress { get => Settings.Api.Address; set { Settings.Api.Address = value.Trim(); Changed(); RaiseApiChanged(); } }
    public decimal ApiPort { get => Settings.Api.Port; set { Settings.Api.Port = (int)value; Changed(); RaiseApiChanged(); } }

    public bool McpEnabled
    {
        get => Settings.Mcp.Enabled;
        set { Settings.Mcp.Enabled = value; if (value) _servers.EnsureApiToken(); Changed(); RaiseApiChanged(); }
    }

    /// <summary>Read on every tool call, so it applies without a restart.</summary>
    public bool McpRequireConfirmation
    {
        get => Settings.Mcp.RequireConfirmation;
        set { Settings.Mcp.RequireConfirmation = value; Save(); OnPropertyChanged(); }
    }

    /// <summary>Show the token in clear text (masked by default, e.g. while the screen is streamed).</summary>
    [ObservableProperty] private bool _showToken;

    partial void OnShowTokenChanged(bool value) => RaiseApiChanged();

    public bool HasToken => Settings.Api.Token.Length > 0;
    public string TokenDisplay => !HasToken ? "(created when the API or MCP is enabled)" : ShowToken ? Settings.Api.Token : Masked;
    private string DisplayToken => ShowToken && HasToken ? Settings.Api.Token : "<TOKEN>";

    public string ApiUrl => _servers.ApiBaseUrl + Cuebot.Server.Api.ApiServer.Prefix;
    public string McpUrl => _servers.ApiBaseUrl + Cuebot.Server.Api.ApiServer.McpPath;

    // Shown with the token masked unless "Show token" is on; the copy buttons always copy the real token.
    public string ClaudeCodeCommand => Cuebot.Server.Mcp.McpClientConfig.ClaudeCodeCommand(McpUrl, DisplayToken);
    public string HttpConfigJson => Cuebot.Server.Mcp.McpClientConfig.HttpJson(McpUrl, DisplayToken);
    public string StdioConfigJson => StdioJson();

    private static string StdioJson()
    {
        var (command, args) = Cuebot.Server.Mcp.McpClientConfig.StdioLaunch(Environment.ProcessPath,
            System.Reflection.Assembly.GetEntryAssembly()?.Location, Environment.GetEnvironmentVariable("CUEBOT_DATA"));
        return Cuebot.Server.Mcp.McpClientConfig.StdioJson(command, args);
    }

    /// <summary>The real text behind a copy button (token included even while it is masked on screen).</summary>
    internal string CopyText(string what)
    {
        var token = _servers.EnsureApiToken();
        RaiseApiChanged();
        return what switch
        {
            "token" => token,
            "apiUrl" => ApiUrl,
            "mcpUrl" => McpUrl,
            "claude" => Cuebot.Server.Mcp.McpClientConfig.ClaudeCodeCommand(McpUrl, token),
            "http" => Cuebot.Server.Mcp.McpClientConfig.HttpJson(McpUrl, token),
            "stdio" => StdioJson(),
            _ => "",
        };
    }

    [RelayCommand]
    private void RegenerateToken()
    {
        _servers.RegenerateApiToken();
        RaiseApiChanged();
        Message = "New API token created. AI clients and scripts using the old one must be updated.";
    }

    private void RaiseApiChanged()
    {
        OnPropertyChanged(nameof(HasToken));
        OnPropertyChanged(nameof(TokenDisplay));
        OnPropertyChanged(nameof(ApiUrl));
        OnPropertyChanged(nameof(McpUrl));
        OnPropertyChanged(nameof(ClaudeCodeCommand));
        OnPropertyChanged(nameof(HttpConfigJson));
        OnPropertyChanged(nameof(StdioConfigJson));
    }

    // ---- Status ----------------------------------------------------------------------------------------------------

    public ObservableCollection<ServerStatusViewModel> Statuses { get; } = [];
    public ObservableCollection<string> Clients { get; } = [];

    [ObservableProperty] private bool _hasPendingChanges;
    [ObservableProperty] private string _message = "";

    private void Changed([System.Runtime.CompilerServices.CallerMemberName] string? property = null)
    {
        Save();
        HasPendingChanges = true;
        OnPropertyChanged(property);
    }

    /// <summary>Restarts the built-in servers with the current settings.</summary>
    [RelayCommand]
    private async Task Apply()
    {
        Message = "";
        try
        {
            Save();
            await _servers.ApplyAsync();
            HasPendingChanges = false;
            var failed = _servers.Statuses.Where(s => s.Enabled && !s.Running).Select(s => $"{s.Name}: {s.Message}").ToArray();
            Message = failed.Length == 0 ? "Servers restarted." : string.Join(Environment.NewLine, failed);
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        Refresh();
    }

    internal void Refresh()
    {
        Statuses.Clear();
        foreach (var s in _servers.Statuses) Statuses.Add(new ServerStatusViewModel(s));
        Clients.Clear();
        foreach (var c in _servers.WebSocket.Clients)
        {
            Clients.Add($"{c.Remote}  ·  since {c.ConnectedAt:HH:mm:ss}  ·  {(c.Authenticated ? "authenticated" : "not authenticated")}  ·  {c.Subscriptions} subscriptions");
        }
        RaiseApiChanged();
        foreach (var item in CustomServers) item.Refresh();
        foreach (var item in CustomClients) item.Refresh();
        Remote?.Refresh();   // the "can others connect" hint follows the REST API
        OnPropertyChanged(nameof(DockUrls));
    }

    // ---- Custom servers and clients --------------------------------------------------------------------------------

    public ObservableCollection<CustomServerViewModel> CustomServers { get; } = [];
    public ObservableCollection<CustomClientViewModel> CustomClients { get; } = [];

    [RelayCommand]
    private void AddCustomServer()
    {
        var used = Settings.CustomServers.Select(s => s.Port).ToHashSet();
        var port = 9000;
        while (used.Contains(port)) port++;
        var server = new CustomWebSocketServerDefinition { Name = $"Server {Settings.CustomServers.Count + 1}", Port = port };
        Settings.CustomServers.Add(server);
        CustomServers.Add(new CustomServerViewModel(this, server));
        Save();
    }

    internal async Task RemoveCustomServerAsync(CustomServerViewModel item)
    {
        await _servers.CustomServers.StopAsync(item.Definition.Id);
        Settings.CustomServers.Remove(item.Definition);
        CustomServers.Remove(item);
        Save();
    }

    [RelayCommand]
    private void AddCustomClient()
    {
        var client = new CustomWebSocketClientDefinition { Name = $"Client {Settings.CustomClients.Count + 1}" };
        Settings.CustomClients.Add(client);
        CustomClients.Add(new CustomClientViewModel(this, client));
        Save();
    }

    internal async Task RemoveCustomClientAsync(CustomClientViewModel item)
    {
        await _servers.CustomClients.DisconnectAsync(item.Definition.Id);
        Settings.CustomClients.Remove(item.Definition);
        CustomClients.Remove(item);
        Save();
    }
}

public sealed class ServerStatusViewModel(ServerStatus status)
{
    public string Name => status.Name;
    public bool IsRunning => status.Running;
    public bool IsError => status.Enabled && !status.Running;
    public string Text => status.Running
        ? $"Listening on {status.Address}:{status.Port}" + (status.Clients > 0 ? $" · {status.Clients} connected" : "")
        : status.Message;
}

public sealed class DockUrlViewModel(string name, string url)
{
    public string Name => name;
    public string Url => url;
}

public sealed partial class HttpMappingViewModel(ServersViewModel owner, HttpMapping mapping) : ObservableObject
{
    internal HttpMapping Mapping => mapping;

    public string Path { get => mapping.Path; set { mapping.Path = value.Trim(); owner.Save(); OnPropertyChanged(); } }
    public string Folder { get => mapping.Folder; set { mapping.Folder = value.Trim(); owner.Save(); OnPropertyChanged(); } }

    [RelayCommand]
    private void Remove() => owner.RemoveMapping(this);
}

public sealed partial class CustomServerViewModel(ServersViewModel owner, CustomWebSocketServerDefinition server) : ObservableObject
{
    internal CustomWebSocketServerDefinition Definition => server;

    public string Name { get => server.Name; set { server.Name = value.Trim(); owner.Save(); OnPropertyChanged(); } }
    public string Address { get => server.Address; set { server.Address = value.Trim(); owner.Save(); OnPropertyChanged(); } }
    public decimal Port { get => server.Port; set { server.Port = (int)value; owner.Save(); OnPropertyChanged(); } }
    public string Endpoint { get => server.Endpoint; set { server.Endpoint = value.Trim(); owner.Save(); OnPropertyChanged(); } }
    public bool AutoStart { get => server.AutoStart; set { server.AutoStart = value; owner.Save(); OnPropertyChanged(); } }

    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = "Stopped";

    internal void Refresh()
    {
        var servers = owner.Manager.CustomServers;
        IsRunning = servers.IsListening(server.Id);
        StatusText = IsRunning
            ? $"Listening on port {servers.Port(server.Id)} · {servers.ClientCount(server.Id)} connected"
            : "Stopped";
    }

    [RelayCommand]
    private async Task Toggle()
    {
        try
        {
            if (IsRunning) await owner.Manager.CustomServers.StopAsync(server.Id);
            else await owner.Manager.CustomServers.StartAsync(server);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return;
        }
        Refresh();
    }

    [RelayCommand]
    private Task Remove() => owner.RemoveCustomServerAsync(this);
}

public sealed partial class CustomClientViewModel(ServersViewModel owner, CustomWebSocketClientDefinition client) : ObservableObject
{
    internal CustomWebSocketClientDefinition Definition => client;

    public string Name { get => client.Name; set { client.Name = value.Trim(); owner.Save(); OnPropertyChanged(); } }
    public string Url { get => client.Url; set { client.Url = value.Trim(); owner.Save(); OnPropertyChanged(); } }
    public bool AutoConnect { get => client.AutoConnect; set { client.AutoConnect = value; owner.Save(); OnPropertyChanged(); } }
    public bool AutoReconnect { get => client.AutoReconnect; set { client.AutoReconnect = value; owner.Save(); OnPropertyChanged(); } }

    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private string _statusText = "Disconnected";

    internal void Refresh()
    {
        var state = owner.Manager.CustomClients.State(client.Id);
        IsConnected = state == ConnectionState.Connected;
        StatusText = state.ToString();
    }

    [RelayCommand]
    private async Task Toggle()
    {
        try
        {
            if (owner.Manager.CustomClients.State(client.Id) != ConnectionState.Disconnected) await owner.Manager.CustomClients.DisconnectAsync(client.Id);
            else await owner.Manager.CustomClients.ConnectAsync(client);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
            return;
        }
        Refresh();
    }

    [RelayCommand]
    private Task Remove() => owner.RemoveCustomClientAsync(this);
}
