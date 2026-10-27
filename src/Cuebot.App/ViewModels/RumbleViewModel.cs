using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Cuebot.Rumble;

namespace Cuebot.App.ViewModels;

public sealed partial class RumbleViewModel : PageViewModel
{
    private readonly RumbleService _rumble;

    public RumbleViewModel(RumbleService rumble) : base("Rumble")
    {
        _rumble = rumble;
        rumble.StatusChanged += () => OnUi(() => OnPropertyChanged(string.Empty));
    }

    /// <summary>Contains the API key, so the view shows it masked.</summary>
    public string LiveStreamApiUrl
    {
        get => _rumble.Settings.LiveStreamApiUrl;
        set { _rumble.Settings.LiveStreamApiUrl = value.Trim(); _rumble.SaveSettings(); OnPropertyChanged(); }
    }

    public decimal PollSeconds
    {
        get => _rumble.Settings.PollSeconds;
        set { _rumble.Settings.PollSeconds = (int)Math.Max(5, value); _rumble.SaveSettings(); OnPropertyChanged(); }
    }

    public bool AutoConnect
    {
        get => _rumble.Settings.AutoConnect;
        set { _rumble.Settings.AutoConnect = value; _rumble.SaveSettings(); OnPropertyChanged(); }
    }

    public string State => _rumble.State.ToString();
    public string StatusMessage => _rumble.StatusMessage;
    public string Channel => _rumble.ChannelName ?? "—";
    public string LiveStatus => _rumble.IsLive ? $"Live: {_rumble.CurrentStreamTitle}" : "Offline";
    public bool IsSignedIn => _rumble.SignedInUser is not null;
    public string ChatAccount => _rumble.SignedInUser is { } user ? $"Sending chat as {user}" : "Not signed in (chat is read-only)";

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _error = "";

    [RelayCommand]
    private Task Connect() => Run(() => _rumble.ConnectAsync());

    [RelayCommand]
    private Task Disconnect() => Run(_rumble.DisconnectAsync);

    [RelayCommand]
    private Task SignIn() => Run(async () =>
    {
        await _rumble.SignInAsync(Username.Trim(), Password);
        Password = "";
    });

    [RelayCommand]
    private void SignOut()
    {
        _rumble.SignOut();
        OnPropertyChanged(string.Empty);
    }

    private async Task Run(Func<Task> operation)
    {
        Error = "";
        try { await operation(); }
        catch (Exception ex) { Error = ex.Message; }
        OnPropertyChanged(string.Empty);
    }
}
