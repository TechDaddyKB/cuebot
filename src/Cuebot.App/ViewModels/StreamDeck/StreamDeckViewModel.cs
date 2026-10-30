using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.App.Shell;
using Cuebot.Core;
using Cuebot.Server;
using Cuebot.StreamDeck;
using Cuebot.StreamDeck.Hid;

namespace Cuebot.App.ViewModels.StreamDeck;

/// <summary>
/// Stream Deck tab: setup steps (REST API on, token, plugin file) and the connected Stream Deck apps with their devices
/// and visible Cuebot keys. The plugin connects to the REST API listener, so everything here is about that server.
/// </summary>
public sealed partial class StreamDeckViewModel : PageViewModel
{
    public const string PluginFileName = "io.github.cuebot.streamdeck.streamDeckPlugin";

    private readonly StreamDeckHub _hub;
    private readonly ServerManager _servers;
    private readonly string? _pluginFolder;

    /// <param name="pluginSearchFolders">Where to look for the bundled plugin file (default: next to the app and in ./streamdeck).</param>
    /// <param name="usb">Direct USB mode (decks driven without the Stream Deck app); null hides its card.</param>
    public StreamDeckViewModel(StreamDeckHub hub, ServerManager servers, IEnumerable<string>? pluginSearchFolders = null, StreamDeckHidService? usb = null)
        : base("Stream Deck")
    {
        _hub = hub;
        _servers = servers;
        Usb = usb is null ? null : new StreamDeckUsbViewModel(usb);
        var folders = pluginSearchFolders ?? [AppContext.BaseDirectory, Path.Combine(AppContext.BaseDirectory, "streamdeck")];
        _pluginFolder = folders.FirstOrDefault(f => File.Exists(Path.Combine(f, PluginFileName)));
        hub.StatusChanged += () => OnUi(Refresh);
        servers.StatusChanged += () => OnUi(Refresh);
        Refresh();
    }

    /// <summary>The direct USB card, or null.</summary>
    public StreamDeckUsbViewModel? Usb { get; }

    public bool HasUsb => Usb is not null;

    // ---- Status ----------------------------------------------------------------------------------------------------

    public string State => _hub.State.ToString();
    public string StatusMessage => _hub.StatusMessage;
    public bool IsConnected => _hub.State == ConnectionState.Connected;

    public ObservableCollection<StreamDeckInstanceViewModel> Instances { get; } = [];
    public bool HasNoInstances => Instances.Count == 0;

    [ObservableProperty] private string _message = "";

    // ---- Step 1: REST API -------------------------------------------------------------------------------------------

    public bool ApiEnabled => _servers.Settings.Api.Enabled;
    public bool ApiRunning => _servers.Api.IsRunning;

    public string ApiState => ApiRunning
        ? $"The REST API is running on port {Port}."
        : ApiEnabled ? "The REST API is enabled but not running (see the Servers page for the error)." : "The REST API is off.";

    [RelayCommand]
    private async Task EnableApi()
    {
        Message = "";
        try
        {
            _servers.Settings.Api.Enabled = true;
            _servers.EnsureApiToken();
            _servers.SaveSettings();
            await _servers.RestartAsync(ServerKind.Api);
            Message = ApiRunning ? "REST API turned on." : "The REST API did not start; check the Servers page.";
        }
        catch (Exception ex)
        {
            Message = "Could not start the REST API: " + ex.Message;
        }
        Refresh();
    }

    // ---- Step 2: connection details for the plugin ------------------------------------------------------------------

    /// <summary>What to type in the plugin's Host field (127.0.0.1 unless the API listens on a LAN address).</summary>
    public string Host
    {
        get
        {
            var address = _servers.Settings.Api.Address.Trim();
            return address.Length == 0 || address is "0.0.0.0" or "*" or "+" or "localhost" or "::" ? "127.0.0.1" : address;
        }
    }

    public int Port => ApiRunning ? _servers.Api.Port : _servers.Settings.Api.Port;

    [ObservableProperty] private bool _showToken;

    partial void OnShowTokenChanged(bool value) => OnPropertyChanged(nameof(TokenDisplay));

    public bool HasToken => _servers.Settings.Api.Token.Length > 0;

    public string TokenDisplay => !HasToken
        ? "(created when the REST API is turned on)"
        : ShowToken ? _servers.Settings.Api.Token : new string('•', 24);

    /// <summary>The text behind a copy button ("token", "host" or "port"); the token is created if needed.</summary>
    internal string CopyText(string what)
    {
        var text = what switch
        {
            "token" => _servers.EnsureApiToken(),
            "host" => Host,
            "port" => Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => "",
        };
        Refresh();
        return text;
    }

    // ---- Step 3: plugin file ---------------------------------------------------------------------------------------

    public bool HasPluginFile => _pluginFolder is not null;
    public string PluginFile => _pluginFolder is null ? PluginFileName : Path.Combine(_pluginFolder, PluginFileName);

    public string PluginHint => HasPluginFile
        ? "Double-click the plugin file to install it in the Stream Deck app."
        : $"Download {PluginFileName} from the Cuebot release (or the CI build artifact \"Cuebot-StreamDeck-plugin\") and double-click it.";

    public bool IsLinux => OperatingSystem.IsLinux();

    [RelayCommand]
    private void OpenPluginFolder()
    {
        if (_pluginFolder is not null) FolderLauncher.Open(_pluginFolder);
    }

    // ---- Refresh ---------------------------------------------------------------------------------------------------

    [RelayCommand]
    private void Refresh()
    {
        var instances = _hub.Instances;
        Instances.Clear();
        foreach (var instance in instances) Instances.Add(new StreamDeckInstanceViewModel(instance));
        OnPropertyChanged(string.Empty);
    }

}

/// <summary>One connected Stream Deck app (one plugin instance).</summary>
public sealed class StreamDeckInstanceViewModel(StreamDeckInstanceInfo info)
{
    public string Title => $"Stream Deck {Fallback(info.AppVersion)} on {Platform(info.Platform)}";

    public string Details =>
        $"Plugin {Fallback(info.PluginVersion)} · from {info.Remote} · connected {info.ConnectedAt.LocalDateTime:t}";

    public IReadOnlyList<string> Devices { get; } =
        info.Devices.Count == 0 ? ["(no devices reported)"] : [.. info.Devices.Select(d => d.Connected ? d.Summary : $"{d.Summary} · unplugged")];

    public IReadOnlyList<StreamDeckButtonViewModel> Buttons { get; } =
        [.. info.Buttons.Select(b => new StreamDeckButtonViewModel(b, info.Devices.FirstOrDefault(d => d.Id == b.DeviceId)))];

    public bool HasNoButtons => Buttons.Count == 0;

    private static string Fallback(string text) => text.Length > 0 ? text : "?";

    private static string Platform(string platform) => platform switch
    {
        "windows" => "Windows",
        "mac" => "macOS",
        "" => "an unknown system",
        _ => platform,
    };
}

/// <summary>One visible Cuebot key or dial.</summary>
public sealed class StreamDeckButtonViewModel(StreamDeckButton button, StreamDeckDevice? device)
{
    public string ButtonId => button.ButtonId.Length > 0 ? button.ButtonId : "(none)";
    public string Kind => StreamDeckActionKinds.Describe(button.Kind) + (button.IsDial ? " (dial)" : "");
    public string Where => $"{device?.Name ?? "?"} · {button.Position}";
    public string State => $"state {button.State}";
}
